using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IPageViewModel
{
    private const string AuthCodeSecretName = "mail.imap.auth-code";
    private const string ApiKeySecretName = "deepseek.api-key";
    private const string DefaultAccountId = "default-mailbox";
    private const string DefaultMailbox = "INBOX";

    private readonly IScopedOperationRunner _operations;
    private readonly IDirectoryPicker _directoryPicker;
    private readonly IRunSession _runSession;
    private readonly IAppNavigator _navigator;
    private readonly INotificationService _notifications;
    private readonly ILogger<SettingsViewModel> _logger;

    private UserSettingsSnapshot? _snapshot;
    private MailboxAccountSnapshot? _account;
    private bool _rememberAtLoad = true;
    private bool _loaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestEmailCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _authCode = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _rememberSettings = true;

    [ObservableProperty]
    private string _companyName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DateTime? _dateFrom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DateTime? _dateTo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseDirectoryCommand))]
    private bool _outputDirectoryLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _dateRangeLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestEmailCommand))]
    private bool _emailLocked;

    [ObservableProperty]
    private string _pageError = string.Empty;

    [ObservableProperty]
    private SettingsUiStatus _emailStatus;

    [ObservableProperty]
    private string _emailStatusMessage = string.Empty;

    [ObservableProperty]
    private SettingsUiStatus _apiStatus;

    [ObservableProperty]
    private string _apiStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _needsInitialSetup;

    public SettingsViewModel(
        IScopedOperationRunner operations,
        IDirectoryPicker directoryPicker,
        IRunSession runSession,
        IAppNavigator navigator,
        INotificationService notifications,
        ILogger<SettingsViewModel> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _directoryPicker = directoryPicker ?? throw new ArgumentNullException(nameof(directoryPicker));
        _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Key => "settings";

    public string Title => SettingsCopy.Title;

    public string Eyebrow => SettingsCopy.Eyebrow;

    public bool HasPageError => !string.IsNullOrWhiteSpace(PageError);

    public bool HasEmailStatus => EmailStatus != SettingsUiStatus.Idle;

    public bool HasApiStatus => ApiStatus != SettingsUiStatus.Idle;

    public bool IsControlledRun => OutputDirectoryLocked || DateRangeLocked || EmailLocked;

    partial void OnPageErrorChanged(string value) => OnPropertyChanged(nameof(HasPageError));

    partial void OnEmailStatusChanged(SettingsUiStatus value) => OnPropertyChanged(nameof(HasEmailStatus));

    partial void OnApiStatusChanged(SettingsUiStatus value) => OnPropertyChanged(nameof(HasApiStatus));

    partial void OnOutputDirectoryLockedChanged(bool value) => OnPropertyChanged(nameof(IsControlledRun));

    partial void OnDateRangeLockedChanged(bool value) => OnPropertyChanged(nameof(IsControlledRun));

    partial void OnEmailLockedChanged(bool value) => OnPropertyChanged(nameof(IsControlledRun));

    public Task OnActivatedAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public Task OnDeactivatedAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            var state = await _operations.RunAsync(
                async (provider, token) =>
                {
                    var settingsStore = provider.GetRequiredService<IUserSettingsStore>();
                    var accounts = provider.GetRequiredService<IAccountApplicationService>();
                    var runs = provider.GetRequiredService<IDesktopRunService>();

                    var snapshot = await settingsStore.LoadAsync(token).ConfigureAwait(false);
                    var accountList = await accounts.ListAsync(token).ConfigureAwait(false);
                    var context = await runs.GetContextAsync(token).ConfigureAwait(false);
                    return new LoadedState(snapshot, accountList.Items.FirstOrDefault(), context);
                },
                cancellationToken).ConfigureAwait(true);

            _snapshot = state.Snapshot;
            _account = state.Account;

            EmailLocked = !string.IsNullOrWhiteSpace(state.Context.LockedEmail);
            OutputDirectoryLocked = !string.IsNullOrWhiteSpace(state.Context.LockedOutputPath);
            DateRangeLocked = state.Context.LockedDateFrom is not null || state.Context.LockedDateTo is not null;

            Email = state.Context.LockedEmail ?? state.Account?.EmailAddress ?? string.Empty;
            CompanyName = state.Snapshot.CompanyName;
            OutputDirectory = state.Context.LockedOutputPath ?? state.Snapshot.LastOutputDirectory ?? string.Empty;

            var today = DateTime.Today;
            DateFrom = ToDateTime(state.Context.LockedDateFrom) ?? today.AddDays(-29);
            DateTo = ToDateTime(state.Context.LockedDateTo) ?? today;

            NeedsInitialSetup = string.IsNullOrWhiteSpace(CompanyName)
                || string.IsNullOrWhiteSpace(OutputDirectory)
                || state.Account is null;

            _loaded = true;
            PageError = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载设置失败。");
            PageError = SettingsCopy.SaveFailed;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!_loaded || IsBusy)
        {
            return;
        }

        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);
            PageError = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存设置失败。");
            PageError = SettingsCopy.SaveFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestEmail))]
    private async Task TestEmailAsync(CancellationToken cancellationToken)
    {
        var error = ValidateEmail(Email);
        if (error is not null)
        {
            EmailStatus = SettingsUiStatus.Error;
            EmailStatusMessage = error;
            return;
        }

        EmailStatus = SettingsUiStatus.Testing;
        EmailStatusMessage = SettingsCopy.TestingEmail;
        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);
            var accountId = _account?.AccountId;
            if (accountId is null)
            {
                EmailStatus = SettingsUiStatus.Error;
                EmailStatusMessage = SettingsCopy.AccountMissing;
                return;
            }

            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IAccountTestService>()
                    .TestMailboxAsync(new AccountTestRequest(accountId), token),
                cancellationToken).ConfigureAwait(true);

            EmailStatus = result.Succeeded ? SettingsUiStatus.Success : SettingsUiStatus.Error;
            EmailStatusMessage = result.Succeeded
                ? SettingsCopy.EmailTestPassed
                : SettingsCopy.MapEmailFailure(result.FailureCode);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "测试邮箱授权码失败。");
            EmailStatus = SettingsUiStatus.Error;
            EmailStatusMessage = SettingsCopy.EmailTestFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestApiKey))]
    private async Task TestApiKeyAsync(CancellationToken cancellationToken)
    {
        ApiStatus = SettingsUiStatus.Testing;
        ApiStatusMessage = SettingsCopy.TestingApiKey;
        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await SaveSecretAsync(ApiKeySecretName, ApiKey, cancellationToken).ConfigureAwait(true);
                ApiKey = string.Empty;
            }

            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IAccountTestService>()
                    .TestProviderAsync(new ProviderTestRequest("deepseek", ApiKeySecretName), token),
                cancellationToken).ConfigureAwait(true);

            ApiStatus = result.Succeeded ? SettingsUiStatus.Success : SettingsUiStatus.Error;
            ApiStatusMessage = result.Succeeded
                ? SettingsCopy.ApiTestPassed
                : SettingsCopy.MapApiFailure(result.FailureCode);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "测试 API Key 失败。");
            ApiStatus = SettingsUiStatus.Error;
            ApiStatusMessage = SettingsCopy.ApiTestFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChooseDirectory))]
    private async Task ChooseDirectoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _directoryPicker.ChooseAsync(cancellationToken).ConfigureAwait(true);
            if (!result.Cancelled && !string.IsNullOrWhiteSpace(result.Path))
            {
                OutputDirectory = result.Path;
                PageError = string.Empty;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "选择输出目录失败。");
            PageError = SettingsCopy.DirectoryPickFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var error = ValidateForStart();
        if (error is not null)
        {
            PageError = error;
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);

            var accountId = _account?.AccountId;
            if (accountId is null)
            {
                PageError = SettingsCopy.AccountMissing;
                return;
            }

            var request = new RunStartRequest(
                $"run-{Guid.NewGuid():N}",
                accountId,
                DateOnly.FromDateTime(DateFrom!.Value.Date),
                DateOnly.FromDateTime(DateTo!.Value.Date),
                OutputDirectory,
                CompanyName.Trim(),
                "full");

            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                    .StartAsync(request, token),
                cancellationToken).ConfigureAwait(true);

            if (!result.Accepted)
            {
                PageError = SettingsCopy.MapStartRejection(result.RejectionCode);
                return;
            }

            _runSession.SetRunId(result.RunId);
            await _navigator.NavigateAsync("processing", cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "启动任务失败。");
            PageError = SettingsCopy.StartFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTestEmail() => _loaded && !IsBusy && !EmailLocked;

    private bool CanTestApiKey() => _loaded && !IsBusy;

    private bool CanChooseDirectory() => _loaded && !IsBusy && !OutputDirectoryLocked;

    private bool CanStart() => _loaded && !IsBusy;

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot ?? throw new InvalidOperationException("Settings must be loaded before saving.");
        var email = Email.Trim();
        var remember = RememberSettings;
        var companyName = remember ? CompanyName.Trim() : string.Empty;
        var outputDirectory = remember ? NullIfBlank(OutputDirectory) : null;

        var state = await _operations.RunAsync(
            async (provider, token) =>
            {
                var settingsStore = provider.GetRequiredService<IUserSettingsStore>();
                var secretRetention = provider.GetRequiredService<ISecretRetentionStore>();
                var secretStore = provider.GetRequiredService<ISecretStore>();
                var accounts = provider.GetRequiredService<IAccountApplicationService>();

                var retention = remember ? SecretRetention.Persistent : SecretRetention.Session;
                var turningOffRemember = _rememberAtLoad && !remember;
                if (turningOffRemember)
                {
                    await secretStore.DeleteAsync(AuthCodeSecretName, token).ConfigureAwait(false);
                    await secretStore.DeleteAsync(ApiKeySecretName, token).ConfigureAwait(false);
                }

                _rememberAtLoad = remember;

                // 先清除旧的持久化密钥,再写入本轮输入,避免关闭"记住设置"时把刚输入的值一起删掉。
                if (!string.IsNullOrWhiteSpace(AuthCode))
                {
                    await secretRetention.SetAsync(
                        new SecretSetRequest(AuthCodeSecretName, AuthCode.Trim(), retention), token).ConfigureAwait(false);
                    AuthCode = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(ApiKey))
                {
                    await secretRetention.SetAsync(
                        new SecretSetRequest(ApiKeySecretName, ApiKey.Trim(), retention), token).ConfigureAwait(false);
                    ApiKey = string.Empty;
                }

                var savedAccount = _account;
                if (email.Length > 0)
                {
                    var draft = new MailboxAccountDraft(
                        _account?.AccountId ?? DefaultAccountId,
                        email,
                        email.EndsWith("@163.com", StringComparison.OrdinalIgnoreCase) ? "imap.163.com" : "imap.qq.com",
                        993,
                        true,
                        AuthCodeSecretName,
                        email,
                        DefaultMailbox);

                    if (!Matches(_account, draft))
                    {
                        savedAccount = await accounts.SaveAsync(
                            new AccountSaveRequest(draft, _account?.Revision ?? 0), token).ConfigureAwait(false);
                    }
                }

                var accountId = savedAccount?.AccountId ?? snapshot.CurrentAccountId;
                if (turningOffRemember
                    || !string.Equals(snapshot.CompanyName, companyName, StringComparison.Ordinal)
                    || !string.Equals(snapshot.LastOutputDirectory, outputDirectory, StringComparison.Ordinal)
                    || !string.Equals(snapshot.CurrentAccountId, accountId, StringComparison.Ordinal))
                {
                    snapshot = await settingsStore.UpdateAsync(
                        new SettingsUpdateRequest(
                            snapshot.Revision,
                            AccountId: accountId,
                            CompanyName: companyName,
                            LastOutputDirectory: outputDirectory),
                        token).ConfigureAwait(false);
                }

                return new PersistedState(savedAccount, snapshot);
            },
            cancellationToken).ConfigureAwait(true);

        _account = state.Account;
        _snapshot = state.Snapshot;
        Email = _account?.EmailAddress ?? Email;
        NeedsInitialSetup = string.IsNullOrWhiteSpace(_snapshot.CompanyName)
            || string.IsNullOrWhiteSpace(_snapshot.LastOutputDirectory)
            || _account is null;
    }

    private async Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        var retention = RememberSettings ? SecretRetention.Persistent : SecretRetention.Session;
        await _operations.RunAsync(
            (provider, token) => provider.GetRequiredService<ISecretRetentionStore>()
                .SetAsync(new SecretSetRequest(name, value.Trim(), retention), token),
            cancellationToken).ConfigureAwait(true);
    }

    private string? ValidateForStart()
    {
        var emailError = ValidateEmail(Email);
        if (emailError is not null)
        {
            return emailError;
        }

        if (string.IsNullOrWhiteSpace(CompanyName))
        {
            return SettingsCopy.CompanyRequired;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return SettingsCopy.DirectoryRequired;
        }

        if (DateFrom is null || DateTo is null)
        {
            return SettingsCopy.DateRequired;
        }

        return DateFrom.Value.Date > DateTo.Value.Date ? SettingsCopy.DateOrderInvalid : null;
    }

    private static string? ValidateEmail(string email)
    {
        var value = email.Trim();
        if (value.Length == 0)
        {
            return SettingsCopy.EmailRequired;
        }

        return value.EndsWith("@qq.com", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("@163.com", StringComparison.OrdinalIgnoreCase)
            ? null
            : SettingsCopy.EmailUnsupported;
    }

    private static bool Matches(MailboxAccountSnapshot? account, MailboxAccountDraft draft)
        => account is not null
            && string.Equals(account.AccountId, draft.AccountId, StringComparison.Ordinal)
            && string.Equals(account.EmailAddress, draft.EmailAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(account.ImapHost, draft.ImapHost, StringComparison.Ordinal)
            && account.ImapPort == draft.ImapPort
            && account.UseTls == draft.UseTls
            && string.Equals(account.CredentialName, draft.CredentialName, StringComparison.Ordinal)
            && string.Equals(account.DisplayName, draft.DisplayName, StringComparison.Ordinal)
            && string.Equals(account.DefaultMailbox, draft.DefaultMailbox, StringComparison.Ordinal);

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);

    private sealed record LoadedState(
        UserSettingsSnapshot Snapshot,
        MailboxAccountSnapshot? Account,
        RunContextSnapshot Context);

    private sealed record PersistedState(MailboxAccountSnapshot? Account, UserSettingsSnapshot Snapshot);
}
