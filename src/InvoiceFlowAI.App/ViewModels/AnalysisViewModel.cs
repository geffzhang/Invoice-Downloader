using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class AnalysisViewModel : ObservableObject, IPageViewModel
{
    private readonly IScopedOperationRunner _operations;
    private readonly IRunSession _runSession;
    private readonly IAppNavigator _navigator;
    private readonly INotificationService _notifications;
    private readonly AnalysisOptions _options;
    private readonly ILogger<AnalysisViewModel> _logger;

    private CancellationTokenSource? _pollCancellation;
    private string? _runId;
    private bool _started;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _retentionCount;

    [ObservableProperty]
    private int _manualCheckCount;

    [ObservableProperty]
    private int _processingErrorCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _summaryText = AnalysisCopy.BuildStatusText(0, 0, 0, 0);

    [ObservableProperty]
    private string _quotaMessage = string.Empty;

    [ObservableProperty]
    private string _processingErrorWarning = string.Empty;

    [ObservableProperty]
    private string _manualReviewTitle = string.Empty;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _manualCheckPath = string.Empty;

    [ObservableProperty]
    private string _lastExportFileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isExporting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _pageError = string.Empty;

    public AnalysisViewModel(
        IScopedOperationRunner operations,
        IRunSession runSession,
        IAppNavigator navigator,
        INotificationService notifications,
        AnalysisOptions options,
        ILogger<AnalysisViewModel> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Key => "analysis";

    public string Title => AnalysisCopy.Title;

    public string Eyebrow => AnalysisCopy.Eyebrow;

    public ObservableCollection<AnalysisGroupItem> GroupedErrors { get; } = [];

    public bool HasPageError => !string.IsNullOrWhiteSpace(PageError);

    public bool HasQuotaWarning => !string.IsNullOrWhiteSpace(QuotaMessage);

    public bool HasProcessingErrorWarning => !string.IsNullOrWhiteSpace(ProcessingErrorWarning);

    public bool HasManualReview => ManualCheckCount > 0;

    public bool HasOutputPath => !string.IsNullOrWhiteSpace(OutputPath);

    public bool HasLastExport => !string.IsNullOrWhiteSpace(LastExportFileName);

    public bool HasGroupedErrors => GroupedErrors.Count > 0;

    public string ExportButtonText => IsExporting ? AnalysisCopy.ExportingButton : AnalysisCopy.ExportButton;

    public string OutputTitle => HasOutputPath ? AnalysisCopy.OutputTitle : AnalysisCopy.OutputTitleEmpty;

    public string OutputHint => HasOutputPath ? AnalysisCopy.OutputHint : AnalysisCopy.OutputHintEmpty;

    public string SuccessHelper => SuccessCount > 0 ? AnalysisCopy.SuccessHelper : AnalysisCopy.SuccessHelperEmpty;

    public string RetentionHelper => RetentionCount > 0 ? AnalysisCopy.RetentionHelper : AnalysisCopy.RetentionHelperEmpty;

    public string ManualHelper => ManualCheckCount > 0 ? AnalysisCopy.ManualHelper : AnalysisCopy.ManualHelperEmpty;

    partial void OnPageErrorChanged(string value) => OnPropertyChanged(nameof(HasPageError));

    partial void OnQuotaMessageChanged(string value) => OnPropertyChanged(nameof(HasQuotaWarning));

    partial void OnProcessingErrorWarningChanged(string value) => OnPropertyChanged(nameof(HasProcessingErrorWarning));

    partial void OnOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutputPath));
        OnPropertyChanged(nameof(OutputTitle));
        OnPropertyChanged(nameof(OutputHint));
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastExportFileNameChanged(string value) => OnPropertyChanged(nameof(HasLastExport));

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(ExportButtonText));

    partial void OnSuccessCountChanged(int value) => OnPropertyChanged(nameof(SuccessHelper));

    partial void OnRetentionCountChanged(int value) => OnPropertyChanged(nameof(RetentionHelper));

    partial void OnManualCheckCountChanged(int value)
    {
        OnPropertyChanged(nameof(ManualHelper));
        OnPropertyChanged(nameof(HasManualReview));
        OpenManualReviewFolderCommand.NotifyCanExecuteChanged();
    }

    public Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCancellation.Token);
        return Task.CompletedTask;
    }

    public Task OnDeactivatedAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _pollCancellation?.Cancel();
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var runId = await ResolveRunIdAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(runId))
        {
            PageError = AnalysisCopy.RunUnavailable;
            return;
        }

        try
        {
            var snapshot = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IRunResultsQueryService>()
                    .GetAsync(runId, token),
                cancellationToken).ConfigureAwait(true);

            Apply(snapshot);
            PageError = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载结果失败。");
            PageError = AnalysisCopy.LoadFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        var runId = _runId ?? _runSession.RunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        IsExporting = true;
        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IReportApplicationService>()
                    .ExportAsync(new ReportExportRequest(runId), token),
                cancellationToken).ConfigureAwait(true);

            LastExportFileName = Path.GetFileName(result.ReportPath);

            var opened = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopActionService>()
                    .OpenFileAsync(
                        new RunFileOpenRequest(runId, ReportPath: result.ReportPath, ContentHash: result.ContentHash),
                        token),
                cancellationToken).ConfigureAwait(true);

            if (!opened.Succeeded)
            {
                _notifications.Show(AnalysisCopy.ExportOpenedFailed, NotificationKind.Warning);
            }

            PageError = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 页面切换导致的取消不算失败。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "导出结果失败。");
            PageError = AnalysisCopy.ExportFailed;
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private async Task OpenOutputFolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopActionService>()
                    .OpenRunFolderAsync(_runId, token),
                cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Warning);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开输出目录失败。");
            _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenManualReviewFolder))]
    private async Task OpenManualReviewFolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopActionService>()
                    .OpenManualReviewFolderAsync(_runId, token),
                cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Warning);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开待人工复核目录失败。");
            _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Error);
        }
    }

    [RelayCommand]
    private Task StartNewBatchAsync(CancellationToken cancellationToken)
    {
        _runSession.SetRunId(null);
        _runId = null;
        return _navigator.NavigateAsync("settings", cancellationToken);
    }

    private bool CanExport() => !IsExporting && !string.IsNullOrWhiteSpace(_runId ?? _runSession.RunId);

    private bool CanOpenOutputFolder() => HasOutputPath;

    private bool CanOpenManualReviewFolder() => ManualCheckCount > 0;

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
                _logger.LogError(exception, "刷新结果失败。");
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
        ExportCommand.NotifyCanExecuteChanged();
        return candidate;
    }

    private void Apply(RunResultsSnapshot snapshot)
    {
        var success = Count(snapshot.Summary, "success_count", snapshot.SuccessInvoices.Count);

        var manual = Count(snapshot.Summary, "manual_check_count",
            Count(snapshot.ResultBreakdown, "manual_review", GroupCount(snapshot.GroupedErrorInvoices, "manual_review")));
        var retention = Count(snapshot.Summary, "retention_count",
            Count(snapshot.ResultBreakdown, "retained_record", GroupCount(snapshot.GroupedErrorInvoices, "retained_record")));
        var processing = Count(snapshot.Summary, "processing_error_count",
            Count(snapshot.ResultBreakdown, "processing_error", GroupCount(snapshot.GroupedErrorInvoices, "processing_error")));

        var totalErrors = snapshot.GroupedErrorInvoices.Sum(group => group.Count);
        var pending = manual + retention + processing;
        if (pending == 0)
        {
            pending = Count(snapshot.Summary, "error_count", totalErrors);
        }

        SuccessCount = success;
        ManualCheckCount = manual;
        RetentionCount = retention;
        ProcessingErrorCount = processing;
        TotalCount = success + pending;
        SummaryText = $"共整理 {TotalCount} 条结果记录。{AnalysisCopy.BuildStatusText(success, manual, retention, processing)}";
        QuotaMessage = snapshot.QuotaExhausted ? AnalysisCopy.QuotaWarning : string.Empty;
        ProcessingErrorWarning = processing > 0 ? AnalysisCopy.ProcessingErrorWarning(processing) : string.Empty;
        ManualReviewTitle = AnalysisCopy.ManualReviewTitle(manual);
        OutputPath = snapshot.OutputPath ?? ParentFolder(snapshot.ManualCheckPath) ?? string.Empty;
        ManualCheckPath = snapshot.ManualCheckPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(snapshot.LastExportPath))
        {
            LastExportFileName = Path.GetFileName(snapshot.LastExportPath);
        }

        GroupedErrors.Clear();
        foreach (var group in snapshot.GroupedErrorInvoices.Where(group => group.Count > 0))
        {
            GroupedErrors.Add(new AnalysisGroupItem(
                group.Key,
                AnalysisCopy.MapGroupLabel(group.Key),
                group.Count));
        }

        OnPropertyChanged(nameof(HasGroupedErrors));
    }

    private static int Count(IReadOnlyDictionary<string, int> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && value > 0 ? value : fallback;

    private static int GroupCount(IReadOnlyList<RunGroupedErrorResult> groups, string key)
        => groups.Where(group => string.Equals(group.Key, key, StringComparison.Ordinal)).Sum(group => group.Count);

    private static string? ParentFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
