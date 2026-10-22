using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class AnalysisViewModelTests
{
    [Fact]
    public async Task Refresh_derives_counts_from_summary_breakdown_and_groups()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            summary: new Dictionary<string, int> { ["success_count"] = 5, ["manual_check_count"] = 2 },
            groups:
            [
                new RunGroupedErrorResult("manual_review", "Manual review", 2, []),
                new RunGroupedErrorResult("retained_record", "Retained", 1, []),
            ]);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.SuccessCount.Should().Be(5);
        viewModel.ManualCheckCount.Should().Be(2);
        viewModel.RetentionCount.Should().Be(1);
        viewModel.ProcessingErrorCount.Should().Be(0);
        viewModel.TotalCount.Should().Be(8);
        viewModel.HasManualReview.Should().BeTrue();
        viewModel.GroupedErrors.Should().OnlyContain(group => group.Label != "Manual review" && group.Label != "Retained");
    }

    [Fact]
    public async Task Refresh_falls_back_to_breakdown_when_summary_is_missing()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            breakdown: new Dictionary<string, int>
            {
                ["manual_review"] = 3,
                ["retained_record"] = 4,
                ["processing_error"] = 1,
            });
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.ManualCheckCount.Should().Be(3);
        viewModel.RetentionCount.Should().Be(4);
        viewModel.ProcessingErrorCount.Should().Be(1);
        viewModel.HasProcessingErrorWarning.Should().BeTrue();
        viewModel.ProcessingErrorWarning.Should().Be("检测到 1 条真实异常，请通过“导出结果明细”继续排查。");
    }

    [Fact]
    public async Task Refresh_never_renders_the_server_quota_message()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(quota: true, quotaMessage: "Provider quota exhausted.");
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.HasQuotaWarning.Should().BeTrue();
        viewModel.QuotaMessage.Should().Be("大模型额度已用尽，请补充额度后重新运行。");
    }

    [Fact]
    public async Task Refresh_builds_status_copy_for_mixed_results()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            summary: new Dictionary<string, int>
            {
                ["success_count"] = 6,
                ["manual_check_count"] = 2,
                ["retention_count"] = 1,
                ["processing_error_count"] = 0,
            });
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.SummaryText.Should().Be(
            "共整理 9 条结果记录。已整理 6 条成功记录；请优先查看 2 条待人工复核记录，另有 1 条暂存记录可按需导出查看。");
    }

    [Fact]
    public async Task Export_writes_the_report_and_opens_it_through_the_desktop_service()
    {
        var harness = new AnalysisHarness();
        harness.Session.SetRunId("run-9");
        harness.Results.Snapshot = Snapshot(summary: new Dictionary<string, int> { ["success_count"] = 1 });
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.ExportCommand.ExecuteAsync(null);

        harness.Reports.Exported.Should().ContainSingle();
        harness.Reports.Exported[0].RunId.Should().Be("run-9");
        harness.Actions.OpenedFiles.Should().ContainSingle();
        harness.Actions.OpenedFiles[0].ReportPath.Should().Be(@"D:\out\report.xlsx");
        harness.Actions.OpenedFiles[0].ContentHash.Should().Be("hash-1");
        viewModel.LastExportFileName.Should().Be("report.xlsx");
        viewModel.HasLastExport.Should().BeTrue();
    }

    [Fact]
    public async Task Export_failure_shows_product_copy_and_no_open_call()
    {
        var harness = new AnalysisHarness();
        harness.Session.SetRunId("run-9");
        harness.Reports.ExportException = new InvalidOperationException("disk full at C:\\secret\\path");
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.ExportCommand.ExecuteAsync(null);

        viewModel.HasPageError.Should().BeTrue();
        viewModel.PageError.Should().Be("导出失败，请稍后重试。");
        viewModel.PageError.Should().NotContain("secret");
        harness.Actions.OpenedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_new_batch_clears_the_run_session_and_goes_home()
    {
        var harness = new AnalysisHarness();
        harness.Session.SetRunId("run-9");
        var viewModel = harness.CreateViewModel();

        await viewModel.StartNewBatchCommand.ExecuteAsync(null);

        harness.Session.RunId.Should().BeNull();
        harness.Navigator.Requested.Should().Equal("settings");
    }

    [Fact]
    public async Task Unknown_group_keys_fall_back_to_a_neutral_label()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            groups: [new RunGroupedErrorResult("mystery_code", "Raw English label", 2, [])]);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.GroupedErrors.Should().ContainSingle();
        viewModel.GroupedErrors[0].Label.Should().Be("待处理记录");
        viewModel.GroupedErrors[0].Count.Should().Be(2);
    }

    private static RunResultsSnapshot Snapshot(
        IReadOnlyDictionary<string, int>? summary = null,
        IReadOnlyDictionary<string, int>? breakdown = null,
        IReadOnlyList<RunGroupedErrorResult>? groups = null,
        bool quota = false,
        string? quotaMessage = null)
        => new(
            new Dictionary<string, int>(),
            [],
            [],
            groups ?? [],
            @"D:\out\manual-review",
            @"D:\out",
            summary ?? new Dictionary<string, int>(),
            null,
            null,
            null,
            breakdown ?? new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            quota,
            quotaMessage,
            null);

    private sealed class AnalysisHarness
    {
        public FakeResultsQueryService Results { get; } = new();
        public FakeReportApplicationService Reports { get; } = new();
        public FakeDesktopActionService Actions { get; } = new();
        public FakeRunSession Session { get; } = new();
        public RecordingNavigator Navigator { get; } = new();
        public NullNotificationService Notifications { get; } = new();

        public AnalysisViewModel CreateViewModel()
        {
            var operations = TestScopedOperationRunner.Create(services =>
            {
                services.AddSingleton<IRunResultsQueryService>(Results);
                services.AddSingleton<IReportApplicationService>(Reports);
                services.AddSingleton<IDesktopActionService>(Actions);
                services.AddSingleton<IDesktopRunService>(new FakeDesktopRunService(
                    new RunContextSnapshot(false, false, false, 0, "run-1", null, null, null, null, null)));
            });

            return new AnalysisViewModel(
                operations,
                Session,
                Navigator,
                Notifications,
                AnalysisOptions.Fast,
                NullLogger<AnalysisViewModel>.Instance);
        }
    }

    private sealed class FakeResultsQueryService : IRunResultsQueryService
    {
        public RunResultsSnapshot Snapshot { get; set; } = new(
            new Dictionary<string, int>(),
            [],
            [],
            [],
            null,
            null,
            new Dictionary<string, int>(),
            null,
            null,
            null,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            false,
            null,
            null);

        public Task<RunResultsSnapshot> GetAsync(string? runId, CancellationToken cancellationToken)
            => Task.FromResult(Snapshot);
    }

    private sealed class FakeReportApplicationService : IReportApplicationService
    {
        public List<ReportExportRequest> Exported { get; } = [];
        public Exception? ExportException { get; set; }

        public Task<ReportExportRpcResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken)
        {
            Exported.Add(request);
            if (ExportException is not null)
            {
                return Task.FromException<ReportExportRpcResult>(ExportException);
            }

            return Task.FromResult(new ReportExportRpcResult(
                request.RunId, @"D:\out\report.xlsx", "hash-1", 3, 1, "v1", false));
        }

        public Task<ReportOpenToken> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReportOpenResolution> ResolveTokenAsync(string tokenId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeDesktopActionService : IDesktopActionService
    {
        public List<RunFileOpenRequest> OpenedFiles { get; } = [];
        public List<string?> OpenedFolders { get; } = [];
        public List<string?> OpenedReviewFolders { get; } = [];

        public Task<DesktopActionResult> OpenRunFolderAsync(string? runId, CancellationToken cancellationToken)
        {
            OpenedFolders.Add(runId);
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> OpenManualReviewFolderAsync(string? runId, CancellationToken cancellationToken)
        {
            OpenedReviewFolders.Add(runId);
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> OpenFileAsync(RunFileOpenRequest request, CancellationToken cancellationToken)
        {
            OpenedFiles.Add(request);
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }
    }
}
