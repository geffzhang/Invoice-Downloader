using FluentAssertions;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class ProcessingViewModelTests
{
    [Fact]
    public async Task Refresh_maps_stage_codes_to_product_copy_and_counts()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "extract-documents", progress: 42, stats: new(10, 3, 1));
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.ProgressValue.Should().Be(42);
        viewModel.StatusText.Should().Be("正在识别票据...");
        viewModel.EmailCount.Should().Be(10);
        viewModel.InvoiceCount.Should().Be(3);
        viewModel.ErrorCount.Should().Be(1);
        viewModel.StatusText.Should().NotContain("extract-documents");
    }

    [Fact]
    public async Task Refresh_maps_failure_code_to_product_copy_without_leaking_the_code()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Failed, stage: "scan-mailbox", lastError: "IMAP_LOGIN_FAILED");
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.StatusText.Should().Be("处理失败");
        viewModel.HasLastError.Should().BeTrue();
        viewModel.LastErrorMessage.Should().Be("邮箱授权码无效或已过期,请更新授权码后重试。");
        viewModel.LastErrorMessage.Should().NotContain("IMAP");
    }

    [Fact]
    public async Task Refresh_synthesizes_activity_log_from_snapshot_changes_only()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "scan-mailbox", progress: 10, stats: new(5, 0, 0));
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var afterFirst = viewModel.Logs.Count;
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var afterSecond = viewModel.Logs.Count;
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "extract-documents", progress: 30, stats: new(5, 2, 0));
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        afterFirst.Should().BeGreaterThan(0);
        afterSecond.Should().Be(afterFirst);
        viewModel.Logs.Count.Should().BeGreaterThan(afterFirst);
        viewModel.Logs.Should().OnlyContain(entry => !entry.Message.Contains("scan-mailbox"));
    }

    [Fact]
    public async Task Refresh_surfaces_quota_and_mailbox_failures_as_fixed_chinese_copy()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(
            RunState.Running,
            stage: "extract-documents",
            quota: true,
            quotaMessage: "Provider quota exhausted.",
            mailboxFailures: [new RunMailboxFetchFailureDiagnostic(12, "IMAP_MESSAGE_FETCH_FAILED")]);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.HasQuotaWarning.Should().BeTrue();
        viewModel.QuotaMessage.Should().Be("大模型额度已用尽，请补充额度后重新运行。");
        viewModel.HasMailboxWarning.Should().BeTrue();
        viewModel.MailboxFailureMessage.Should().Be("1 封邮件读取失败,已跳过。");
    }

    [Fact]
    public async Task Stop_requests_a_safe_stop_and_disables_the_button()
    {
        var harness = new ProcessingHarness();
        harness.Session.SetRunId("run-1");
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "scan-mailbox", canStop: true);
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StopCommand.ExecuteAsync(null);

        harness.Runs.Stopped.Should().ContainSingle();
        harness.Runs.Stopped[0].RunId.Should().Be("run-1");
        viewModel.StopPending.Should().BeTrue();
        viewModel.StopCommand.CanExecute(null).Should().BeFalse();
        viewModel.Logs.Should().Contain(entry => entry.Message == "已收到安全停止指令,正在收尾...");
    }

    [Fact]
    public async Task Terminal_state_navigates_to_analysis_once()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Completed, stage: "completed", progress: 100);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        harness.Navigator.Requested.Should().Equal("analysis");
    }

    [Fact]
    public async Task Run_events_trigger_an_immediate_refresh_without_reading_the_payload()
    {
        var harness = new ProcessingHarness(pollInterval: TimeSpan.FromHours(1));
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "scan-mailbox", progress: 15, stats: new(4, 0, 0));
        var viewModel = harness.CreateViewModel();
        await viewModel.OnActivatedAsync(TestContext.Current.CancellationToken);

        harness.Bus.Publish(new AppRunEvent("run.progress", "run-1", 1, DateTimeOffset.UtcNow, new object()));
        await Task.Yield();

        viewModel.EmailCount.Should().Be(4);
        await viewModel.OnDeactivatedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_run_id_shows_a_page_error()
    {
        var harness = new ProcessingHarness(context: new RunContextSnapshot(
            false, false, false, 0, null, null, null, null, null, null));
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.HasPageError.Should().BeTrue();
        viewModel.PageError.Should().Be("任务记录不可用,请返回重新开始。");
    }

    private static RunProgressSnapshot Snapshot(
        RunState state,
        string stage,
        int progress = 0,
        RunProgressStats? stats = null,
        string? lastError = null,
        bool quota = false,
        string? quotaMessage = null,
        bool canStop = false,
        IReadOnlyList<RunMailboxFetchFailureDiagnostic>? mailboxFailures = null)
        => new(
            state,
            state is RunState.Running or RunState.Stopping,
            canStop,
            state == RunState.Stopping,
            progress,
            stage,
            stats ?? new RunProgressStats(0, 0, 0),
            [],
            lastError,
            quota,
            quotaMessage,
            null,
            null,
            null)
        {
            MailboxFetchFailures = mailboxFailures ?? [],
        };

    private sealed class ProcessingHarness
    {
        private readonly TimeSpan _pollInterval;

        public ProcessingHarness(RunContextSnapshot? context = null, TimeSpan? pollInterval = null)
        {
            Runs = new FakeDesktopRunService(context ?? new RunContextSnapshot(
                false, false, false, 0, "run-1", null, null, null, null, "default-mailbox"));
            _pollInterval = pollInterval ?? ProcessingOptions.Fast.PollInterval;
        }

        public FakeDesktopRunService Runs { get; }
        public FakeRunSession Session { get; } = new();
        public RecordingNavigator Navigator { get; } = new();
        public NullNotificationService Notifications { get; } = new();
        public AppEventBus Bus { get; } = new(NullLogger<AppEventBus>.Instance);
        public SynchronousUiDispatcher Dispatcher { get; } = new();

        public ProcessingViewModel CreateViewModel()
        {
            var operations = TestScopedOperationRunner.Create(services =>
                services.AddSingleton<IDesktopRunService>(Runs));

            return new ProcessingViewModel(
                operations,
                Session,
                Navigator,
                Notifications,
                Bus,
                Dispatcher,
                new ProcessingOptions(_pollInterval, TimeSpan.Zero),
                NullLogger<ProcessingViewModel>.Instance);
        }
    }
}
