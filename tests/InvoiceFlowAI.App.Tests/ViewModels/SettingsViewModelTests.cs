using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using InvoiceFlowAI.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Load_applies_locked_output_directory_and_dates()
    {
        var harness = SettingsHarness.Create(new RunContextSnapshot(
            ExplicitRunContext: true,
            ControlledRun: true,
            AutostartEnabled: false,
            AutostartDelayMs: 0,
            RunId: null,
            LockedOutputPath: @"D:\runs\locked",
            LockedDateFrom: new DateOnly(2026, 9, 1),
            LockedDateTo: new DateOnly(2026, 9, 30),
            LockedEmail: "locked@163.com",
            AccountId: null));
        var viewModel = harness.CreateViewModel();

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        viewModel.OutputDirectory.Should().Be(@"D:\runs\locked");
        viewModel.OutputDirectoryLocked.Should().BeTrue();
        viewModel.DateRangeLocked.Should().BeTrue();
        viewModel.EmailLocked.Should().BeTrue();
        viewModel.Email.Should().Be("locked@163.com");
        viewModel.ChooseDirectoryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Save_persists_secrets_account_and_profile_settings()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@163.com";
        viewModel.AuthCode = "auth-123";
        viewModel.ApiKey = "sk-123";
        viewModel.CompanyName = "示例科技";
        viewModel.OutputDirectory = @"D:\out";

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        (await harness.PersistentSecrets.GetAsync("mail.imap.auth-code", TestContext.Current.CancellationToken))
            .Should().Be("auth-123");
        (await harness.PersistentSecrets.GetAsync("deepseek.api-key", TestContext.Current.CancellationToken))
            .Should().Be("sk-123");
        harness.Accounts.Saved.Should().ContainSingle();
        harness.Accounts.Saved[0].Account.EmailAddress.Should().Be("alice@163.com");
        harness.Accounts.Saved[0].Account.ImapHost.Should().Be("imap.163.com");
        harness.Accounts.Saved[0].Account.ImapPort.Should().Be(993);
        harness.Accounts.Saved[0].ExpectedRevision.Should().Be(0);
        harness.Settings.Updates.Should().ContainSingle();
        harness.Settings.Updates[0].CompanyName.Should().Be("示例科技");
        harness.Settings.Updates[0].LastOutputDirectory.Should().Be(@"D:\out");
        viewModel.AuthCode.Should().BeEmpty();
        viewModel.ApiKey.Should().BeEmpty();
    }

    [Fact]
    public async Task Turning_off_remember_deletes_persisted_secrets_and_clears_profile()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        viewModel.RememberSettings = false;
        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        harness.PersistentSecrets.Deleted.Should().BeEquivalentTo("mail.imap.auth-code", "deepseek.api-key");
        harness.Settings.Updates.Should().ContainSingle();
        harness.Settings.Updates[0].CompanyName.Should().BeEmpty();
        harness.Settings.Updates[0].LastOutputDirectory.Should().BeNull();
    }

    [Fact]
    public async Task Turning_off_remember_then_testing_api_key_keeps_the_session_key_for_start()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@163.com";
        viewModel.CompanyName = "示例科技";
        viewModel.OutputDirectory = @"D:\out";
        viewModel.DateFrom = new DateTime(2026, 9, 1);
        viewModel.DateTo = new DateTime(2026, 9, 30);
        viewModel.RememberSettings = false;
        viewModel.ApiKey = "sk-1";

        await viewModel.TestApiKeyCommand.ExecuteAsync(null);
        await viewModel.StartCommand.ExecuteAsync(null);

        (await harness.SecretsStore.GetAsync("deepseek.api-key", TestContext.Current.CancellationToken))
            .Should().Be("sk-1");
        harness.PersistentSecrets.Deleted.Should().Contain("deepseek.api-key");
        harness.Runs.Started.Should().ContainSingle();
    }

    [Fact]
    public async Task Turning_off_remember_keeps_a_new_session_auth_code_and_deletes_the_persisted_copy()
    {
        var harness = SettingsHarness.Create();
        await harness.PersistentSecrets.SaveAsync(
            "mail.imap.auth-code", "persisted-auth", TestContext.Current.CancellationToken);
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.RememberSettings = false;
        viewModel.AuthCode = "new-auth";

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        (await harness.SessionSecrets.GetAsync("mail.imap.auth-code", TestContext.Current.CancellationToken))
            .Should().Be("new-auth");
        (await harness.PersistentSecrets.GetAsync("mail.imap.auth-code", TestContext.Current.CancellationToken))
            .Should().BeNull();
        viewModel.AuthCode.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_email_rejects_unsupported_domain_without_calling_the_service()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@gmail.com";

        await viewModel.TestEmailCommand.ExecuteAsync(null);

        viewModel.EmailStatus.Should().Be(SettingsUiStatus.Error);
        viewModel.EmailStatusMessage.Should().Be("当前支持 QQ 邮箱和 163 邮箱。");
        harness.Tests.MailboxCalls.Should().Be(0);
    }

    [Fact]
    public async Task Test_email_maps_failure_code_to_product_copy()
    {
        var harness = SettingsHarness.Create();
        harness.Tests.MailboxResult = new AccountTestResult("default-mailbox", false, "INBOX", "Mailbox login failed.", "IMAP_LOGIN_FAILED");
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@163.com";

        await viewModel.TestEmailCommand.ExecuteAsync(null);

        viewModel.EmailStatus.Should().Be(SettingsUiStatus.Error);
        viewModel.EmailStatusMessage.Should().Be("邮箱授权码无效或已过期,请重新生成后重试。");
    }

    [Fact]
    public async Task Start_requires_company_and_output_directory()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@163.com";
        viewModel.CompanyName = "";
        viewModel.OutputDirectory = "";
        viewModel.DateFrom = DateTime.Today.AddDays(-7);
        viewModel.DateTo = DateTime.Today;

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.PageError.Should().Be("请填写公司名称。");
        harness.Runs.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_starts_the_run_and_navigates_to_processing()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@163.com";
        viewModel.CompanyName = "示例科技";
        viewModel.OutputDirectory = @"D:\out";
        viewModel.DateFrom = new DateTime(2026, 9, 1);
        viewModel.DateTo = new DateTime(2026, 9, 30);

        await viewModel.StartCommand.ExecuteAsync(null);

        harness.Runs.Started.Should().ContainSingle();
        harness.Runs.Started[0].CompanyName.Should().Be("示例科技");
        harness.Runs.Started[0].DateFrom.Should().Be(new DateOnly(2026, 9, 1));
        harness.Runs.Started[0].DateTo.Should().Be(new DateOnly(2026, 9, 30));
        harness.Session.RunId.Should().Be(harness.Runs.Started[0].RunId);
        harness.Navigator.Requested.Should().Equal("processing");
    }

    private sealed class SettingsHarness
    {
        private SettingsHarness(RunContextSnapshot context)
        {
            Context = context;
            Accounts = new FakeAccountApplicationService();
            Tests = new FakeAccountTestService();
            Settings = new FakeUserSettingsStore();
            PersistentSecrets = new InMemorySecretStore();
            SessionSecrets = new InMemorySecretStore();
            SecretsStore = new CompositeSecretStore(PersistentSecrets, SessionSecrets);
            Picker = new FakeDirectoryPicker();
            Runs = new FakeDesktopRunService(context);
            Session = new FakeRunSession();
            Navigator = new RecordingNavigator();
        }

        public RunContextSnapshot Context { get; }
        public FakeAccountApplicationService Accounts { get; }
        public FakeAccountTestService Tests { get; }
        public FakeUserSettingsStore Settings { get; }
        public InMemorySecretStore PersistentSecrets { get; }
        public InMemorySecretStore SessionSecrets { get; }
        public ISecretStore SecretsStore { get; }
        public FakeDirectoryPicker Picker { get; }
        public FakeDesktopRunService Runs { get; }
        public FakeRunSession Session { get; }
        public RecordingNavigator Navigator { get; }

        public static SettingsHarness Create(RunContextSnapshot? context = null) => new(context ?? new RunContextSnapshot(
            ExplicitRunContext: false,
            ControlledRun: false,
            AutostartEnabled: false,
            AutostartDelayMs: 0,
            RunId: null,
            LockedOutputPath: null,
            LockedDateFrom: null,
            LockedDateTo: null,
            LockedEmail: null,
            AccountId: null));

        public SettingsViewModel CreateViewModel()
        {
            var operations = TestScopedOperationRunner.Create(services =>
            {
                services.AddScoped<IAccountApplicationService>(_ => Accounts);
                services.AddScoped<IAccountTestService>(_ => Tests);
                services.AddScoped<IUserSettingsStore>(_ => Settings);
                services.AddScoped<IPersistentSecretStore>(_ => PersistentSecrets);
                services.AddScoped<ISessionSecretStore>(_ => SessionSecrets);
                services.AddScoped<ISecretRetentionStore>(provider => new SecretApplicationService(
                    provider.GetRequiredService<IPersistentSecretStore>(),
                    provider.GetRequiredService<ISessionSecretStore>()));
                services.AddScoped<ISecretStore>(_ => SecretsStore);
                services.AddScoped<IDesktopRunService>(_ => Runs);
            });

            return new SettingsViewModel(
                operations,
                Picker,
                Session,
                Navigator,
                new NullNotificationService(),
                PersistentSecrets,
                NullLogger<SettingsViewModel>.Instance);
        }
    }

    private sealed class FakeAccountApplicationService : IAccountApplicationService
    {
        public List<AccountSaveRequest> Saved { get; } = [];
        public MailboxAccountSnapshot? Current { get; private set; }

        public Task<AccountListResult> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AccountListResult(Current is null ? [] : [Current]));

        public Task<MailboxAccountSnapshot> SaveAsync(AccountSaveRequest request, CancellationToken cancellationToken)
        {
            Saved.Add(request);
            Current = new MailboxAccountSnapshot(
                request.Account.AccountId,
                request.Account.EmailAddress,
                request.Account.ImapHost,
                request.Account.ImapPort,
                request.Account.UseTls,
                request.Account.CredentialName,
                request.Account.DisplayName,
                1,
                true,
                "al***@163.com",
                DateTimeOffset.UtcNow,
                request.Account.DefaultMailbox);
            return Task.FromResult(Current);
        }
    }

    private sealed class FakeAccountTestService : IAccountTestService
    {
        public int MailboxCalls { get; private set; }
        public AccountTestResult MailboxResult { get; set; } = new("default-mailbox", true, "INBOX", "");

        public Task<AccountTestResult> TestMailboxAsync(AccountTestRequest request, CancellationToken cancellationToken)
        {
            MailboxCalls++;
            return Task.FromResult(MailboxResult);
        }

        public Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ProviderTestResult(request.ProviderId, true));
    }

    private sealed class FakeUserSettingsStore : IUserSettingsStore
    {
        public List<SettingsUpdateRequest> Updates { get; } = [];

        public Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Create(0, "", null));

        public Task<UserSettingsSnapshot> UpdateAsync(SettingsUpdateRequest request, CancellationToken cancellationToken)
        {
            Updates.Add(request);
            return Task.FromResult(Create(
                request.ExpectedRevision + 1,
                request.CompanyName ?? "",
                request.LastOutputDirectory));
        }

        public Task ImportSnapshotAsync(UserSettingsSnapshot snapshot, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        private static UserSettingsSnapshot Create(int revision, string companyName, string? outputDirectory)
            => new(
                revision,
                "default-mailbox",
                "INBOX",
                new MailboxFilterRules(false, null, null),
                new PipelineOptionsPatch(),
                true,
                "default",
                1,
                "fingerprint",
                DateTimeOffset.UtcNow,
                companyName,
                outputDirectory);
    }

    private sealed class InMemorySecretStore : IPersistentSecretStore, ISessionSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public List<string> Deleted { get; } = [];

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            _values.Remove(name);
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectoryPicker : IDirectoryPicker
    {
        public Task<DirectoryChooseResult> ChooseAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DirectoryChooseResult(false, @"D:\picked"));
    }

    private sealed class FakeDesktopRunService(RunContextSnapshot context) : IDesktopRunService
    {
        public List<RunStartRequest> Started { get; } = [];

        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(context);

        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
        {
            Started.Add(request);
            return Task.FromResult(new RunStartResult(true, request.RunId, null));
        }

        public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    public sealed class FakeRunSession : IRunSession
    {
        public string? RunId { get; private set; }
        public void SetRunId(string? runId) => RunId = runId;
    }

    private sealed class RecordingNavigator : IAppNavigator
    {
        public List<string> Requested { get; } = [];
        public Task NavigateAsync(string key, CancellationToken cancellationToken = default)
        {
            Requested.Add(key);
            return Task.CompletedTask;
        }
    }

    private sealed class NullNotificationService : INotificationService
    {
        public void Show(string message, NotificationKind kind) { }
    }
}
