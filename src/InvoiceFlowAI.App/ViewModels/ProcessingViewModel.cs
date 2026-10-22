using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class ProcessingViewModel : ObservableObject, IPageViewModel
{
    private const int MaxLogEntries = 200;

    private readonly IScopedOperationRunner _operations;
    private readonly IRunSession _runSession;
    private readonly IAppNavigator _navigator;
    private readonly INotificationService _notifications;
    private readonly IAppEventBus _bus;
    private readonly IAvaloniaUiDispatcher _dispatcher;
    private readonly ProcessingOptions _options;
    private readonly ILogger<ProcessingViewModel> _logger;

    private CancellationTokenSource? _pollCancellation;
    private IDisposable? _runEventSubscription;
    private string? _runId;
    private int _lastEmails;
    private int _lastInvoices;
    private int _lastErrors;
    private RunState _lastState = RunState.Idle;
    private bool _stopLogged;
    private bool _stopAccepted;
    private bool _navigated;
    private bool _navigationInFlight;
    private bool _started;

    [ObservableProperty]
    private string _runIdLabel = string.Empty;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private string _statusText = ProcessingCopy.Waiting;

    [ObservableProperty]
    private int _emailCount;

    [ObservableProperty]
    private int _invoiceCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _canStop;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _stopPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private string _pageError = string.Empty;

    [ObservableProperty]
    private string _quotaMessage = string.Empty;

    [ObservableProperty]
    private string _mailboxFailureMessage = string.Empty;

    [ObservableProperty]
    private string _lastErrorMessage = string.Empty;

    public ProcessingViewModel(
        IScopedOperationRunner operations,
        IRunSession runSession,
        IAppNavigator navigator,
        INotificationService notifications,
        IAppEventBus bus,
        IAvaloniaUiDispatcher dispatcher,
        ProcessingOptions options,
        ILogger<ProcessingViewModel> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLogs));
    }

    public string Key => "processing";

    public string Title => ProcessingCopy.Title;

    public string Eyebrow => ProcessingCopy.Eyebrow;

    public ObservableCollection<ActivityLogEntry> Logs { get; } = [];

    public bool HasLogs => Logs.Count > 0;

    public bool HasPageError => !string.IsNullOrWhiteSpace(PageError);

    public bool HasQuotaWarning => !string.IsNullOrWhiteSpace(QuotaMessage);

    public bool HasMailboxWarning => !string.IsNullOrWhiteSpace(MailboxFailureMessage);

    public bool HasLastError => !string.IsNullOrWhiteSpace(LastErrorMessage);

    partial void OnPageErrorChanged(string value) => OnPropertyChanged(nameof(HasPageError));

    partial void OnQuotaMessageChanged(string value) => OnPropertyChanged(nameof(HasQuotaWarning));

    partial void OnMailboxFailureMessageChanged(string value) => OnPropertyChanged(nameof(HasMailboxWarning));

    partial void OnLastErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasLastError));

    public Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        ResetForRun();
        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _runEventSubscription = _bus.Subscribe(_ => _dispatcher.Post(RefreshFromEvent));
        _ = PollLoopAsync(_pollCancellation.Token);
        return Task.CompletedTask;
    }

    public Task OnDeactivatedAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _runEventSubscription?.Dispose();
        _runEventSubscription = null;
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        return Task.CompletedTask;
    }

    private void ResetForRun()
    {
        var sessionRunId = _runSession.RunId;
        if (string.IsNullOrWhiteSpace(sessionRunId) || string.Equals(sessionRunId, _runId, StringComparison.Ordinal))
        {
            return;
        }

        _runId = null;
        RunIdLabel = string.Empty;
        _navigated = false;
        _navigationInFlight = false;
        _stopLogged = false;
        _stopAccepted = false;
        _lastState = RunState.Idle;
        _lastEmails = 0;
        _lastInvoices = 0;
        _lastErrors = 0;
        IsRunning = false;
        CanStop = false;
        StopPending = false;
        Logs.Clear();
    }

    private async void RefreshFromEvent()
    {
        try
        {
            await RefreshAsync(_pollCancellation?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面切换或轮询取消。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "处理运行事件时刷新进度失败。");
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var runId = await ResolveRunIdAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(runId))
        {
            PageError = ProcessingCopy.RunUnavailable;
            return;
        }

        var snapshot = await _operations.RunAsync(
            (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                .GetProgressAsync(runId, token),
            cancellationToken).ConfigureAwait(true);

        await ApplyAsync(snapshot, cancellationToken).ConfigureAwait(true);
        PageError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanStopRun))]
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var runId = _runId ?? _runSession.RunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                    .StopAsync(new RunStopRequest(runId), token),
                cancellationToken).ConfigureAwait(true);

            if (!result.Accepted)
            {
                _notifications.Show(ProcessingCopy.StopRejected, NotificationKind.Warning);
                return;
            }

            _stopAccepted = true;
            _stopLogged = true;
            StopPending = true;
            AppendLog(ProcessingCopy.StopAccepted);
        }
        catch (OperationCanceledException)
        {
            // 页面切换导致的取消不算失败。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "发送停止指令失败。");
            _notifications.Show(ProcessingCopy.StopFailed, NotificationKind.Error);
        }
    }

    private bool CanStopRun() => IsRunning && CanStop && !StopPending;

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "刷新进度失败。");
                PageError = ProcessingCopy.LoadFailed;
            }

            try
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<string?> ResolveRunIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_runId))
        {
            return _runId;
        }

        var candidate = _runSession.RunId;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var context = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>().GetContextAsync(token),
                cancellationToken).ConfigureAwait(true);
            candidate = context.RunId;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        _runId = candidate;
        _runSession.SetRunId(candidate);
        RunIdLabel = candidate;
        return candidate;
    }

    private async Task ApplyAsync(RunProgressSnapshot snapshot, CancellationToken cancellationToken)
    {
        ProgressValue = snapshot.RunState == RunState.Completed ? 100 : snapshot.Progress;
        IsRunning = snapshot.IsRunning;
        CanStop = snapshot.CanStop;
        StopPending = _stopAccepted || snapshot.StopRequested;
        EmailCount = snapshot.Stats.Emails;
        InvoiceCount = snapshot.Stats.Invoices;
        ErrorCount = snapshot.Stats.Errors;
        StatusText = MapStatus(snapshot);
        QuotaMessage = snapshot.QuotaExhausted ? ProcessingCopy.QuotaWarning : string.Empty;
        MailboxFailureMessage = snapshot.MailboxFetchFailures.Count == 0
            ? string.Empty
            : $"{snapshot.MailboxFetchFailures.Count} 封邮件读取失败,已跳过。";
        LastErrorMessage = string.IsNullOrWhiteSpace(snapshot.LastError)
            ? string.Empty
            : ProcessingCopy.MapFailureCode(snapshot.LastError);

        AppendDeltaLogs(snapshot);

        if (snapshot.StopRequested && !_stopLogged)
        {
            _stopLogged = true;
            AppendLog(ProcessingCopy.StopAccepted);
        }

        if (!snapshot.IsRunning && snapshot.RunState is RunState.Completed or RunState.Failed or RunState.Cancelled)
        {
            if (_lastState != snapshot.RunState)
            {
                AppendLog(snapshot.RunState switch
                {
                    RunState.Completed => ProcessingCopy.CompletedLog,
                    RunState.Cancelled => ProcessingCopy.CancelledLog,
                    _ => ProcessingCopy.FailedLog,
                });
            }

            if (!_navigated && !_navigationInFlight)
            {
                // 导航成功后才记为已完成;失败时由下一次轮询重试。
                _navigationInFlight = true;
                try
                {
                    if (_options.TerminalNavigationDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.TerminalNavigationDelay, cancellationToken).ConfigureAwait(true);
                    }

                    // 轮询令牌会在离开本页时被取消,导航本身不能再受它约束。
                    await _navigator.NavigateAsync("analysis", CancellationToken.None).ConfigureAwait(true);
                    _navigated = true;
                }
                finally
                {
                    _navigationInFlight = false;
                }
            }
        }

        _lastState = snapshot.RunState;
    }

    private void AppendDeltaLogs(RunProgressSnapshot snapshot)
    {
        if (Logs.Count == 0)
        {
            AppendLog(ProcessingCopy.ReadyLog);
        }

        if (snapshot.Stats.Emails > _lastEmails)
        {
            AppendLog($"已获取 {snapshot.Stats.Emails} 封邮件。");
        }

        if (snapshot.Stats.Invoices > _lastInvoices)
        {
            AppendLog($"已识别 {snapshot.Stats.Invoices} 张票据。");
        }

        if (snapshot.Stats.Errors > _lastErrors)
        {
            AppendLog($"新增 {snapshot.Stats.Errors} 条待处理记录。");
        }

        _lastEmails = snapshot.Stats.Emails;
        _lastInvoices = snapshot.Stats.Invoices;
        _lastErrors = snapshot.Stats.Errors;
    }

    private void AppendLog(string message)
    {
        Logs.Insert(0, new ActivityLogEntry(DateTimeOffset.Now, message));
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private static string MapStatus(RunProgressSnapshot snapshot)
    {
        if (snapshot.StopRequested)
        {
            return ProcessingCopy.StopRequested;
        }

        return snapshot.RunState switch
        {
            RunState.Completed => ProcessingCopy.Completed,
            RunState.Failed => ProcessingCopy.Failed,
            RunState.Cancelled => ProcessingCopy.Cancelled,
            RunState.Stopping => ProcessingCopy.StopRequested,
            RunState.Running => ProcessingCopy.MapStage(snapshot.StatusText),
            _ => ProcessingCopy.Waiting,
        };
    }
}
