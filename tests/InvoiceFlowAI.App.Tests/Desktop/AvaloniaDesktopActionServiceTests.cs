using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Desktop;

public sealed class AvaloniaDesktopActionServiceTests
{
    [Fact]
    public async Task OpenRunFolder_uses_configured_output_root_without_accepting_a_path()
    {
        using var temp = new TempDirectory();
        var launcher = new FakePathLauncher();
        var service = CreateService(temp.Path, launcher: launcher);

        var result = await service.OpenRunFolderAsync(null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        launcher.OpenedPath.Should().Be(temp.Path);
    }

    [Fact]
    public async Task OpenManualReviewFolder_uses_run_scoped_location_and_reports_missing_folder_safely()
    {
        using var temp = new TempDirectory();
        var launcher = new FakePathLauncher();
        var service = CreateService(temp.Path, launcher: launcher);
        var reviewPath = Path.Combine(temp.Path, "archive", "run-1", "review");
        Directory.CreateDirectory(reviewPath);

        var opened = await service.OpenManualReviewFolderAsync("run-1", CancellationToken.None);

        opened.Succeeded.Should().BeTrue();
        launcher.OpenedPath.Should().Be(reviewPath);
        Directory.Delete(reviewPath, recursive: true);

        var missing = await service.OpenManualReviewFolderAsync("run-1", CancellationToken.None);
        missing.Succeeded.Should().BeFalse();
        missing.Message.Should().NotContain(temp.Path);
    }

    [Fact]
    public async Task OpenFile_launches_only_a_committed_run_scoped_artifact()
    {
        using var temp = new TempDirectory();
        var relativePath = "archive/run-1/invoice.pdf";
        var filePath = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "invoice");
        var artifacts = new FakeArchiveArtifactStore
        {
            Artifacts = [NewArtifact("run-1", "doc-1", relativePath, ArchiveArtifactState.Committed)],
        };
        var launcher = new FakePathLauncher();
        var service = CreateService(temp.Path, launcher: launcher, artifacts: artifacts);

        var opened = await service.OpenFileAsync(new RunFileOpenRequest("run-1", DocumentId: "doc-1"), CancellationToken.None);

        opened.Succeeded.Should().BeTrue();
        launcher.OpenedPath.Should().Be(filePath);

        var rejected = await service.OpenFileAsync(
            new RunFileOpenRequest("run-1", DocumentId: "doc-other"),
            CancellationToken.None);
        rejected.Succeeded.Should().BeFalse();
        rejected.Message.Should().NotContain(temp.Path);
    }

    [Fact]
    public async Task OpenFile_rejects_artifact_traversal_and_uncommitted_state()
    {
        using var temp = new TempDirectory();
        var artifacts = new FakeArchiveArtifactStore
        {
            Artifacts =
            [
                NewArtifact("run-1", "doc-traversal", "../outside.pdf", ArchiveArtifactState.Committed),
                NewArtifact("run-1", "doc-pending", "archive/pending.pdf", ArchiveArtifactState.Prepared),
            ],
        };
        var service = CreateService(temp.Path, artifacts: artifacts);

        var traversal = await service.OpenFileAsync(
            new RunFileOpenRequest("run-1", DocumentId: "doc-traversal"),
            CancellationToken.None);
        var pending = await service.OpenFileAsync(
            new RunFileOpenRequest("run-1", DocumentId: "doc-pending"),
            CancellationToken.None);

        traversal.Succeeded.Should().BeFalse();
        pending.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task OpenFile_opens_report_only_after_persisted_token_is_issued_and_consumed()
    {
        using var temp = new TempDirectory();
        var relativePath = "reports/run-1/report.xlsx";
        var filePath = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "workbook");
        var reports = new FakeReportApplicationService(relativePath, "hash-1");
        var launcher = new FakePathLauncher();
        var service = CreateService(temp.Path, launcher: launcher, reports: reports);

        var result = await service.OpenFileAsync(
            new RunFileOpenRequest("run-1", ReportPath: relativePath, ContentHash: "hash-1"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        reports.IssuedRequest.Should().BeEquivalentTo(new ReportOpenRequest("run-1", relativePath, "hash-1"));
        reports.ResolvedTokenId.Should().Be("token-1");
        launcher.OpenedPath.Should().Be(filePath);
    }

    [Fact]
    public async Task OpenFile_returns_safe_result_when_os_launcher_fails()
    {
        using var temp = new TempDirectory();
        var relativePath = "archive/run-1/invoice.pdf";
        var filePath = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "invoice");
        var launcher = new FakePathLauncher { Exception = new IOException("private C:\\user\\data path") };
        var service = CreateService(temp.Path, launcher: launcher, artifacts: new FakeArchiveArtifactStore
        {
            Artifacts = [NewArtifact("run-1", "doc-1", relativePath, ArchiveArtifactState.Committed)],
        });

        var result = await service.OpenFileAsync(new RunFileOpenRequest("run-1", DocumentId: "doc-1"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().NotContain("private");
        result.Message.Should().NotContain(temp.Path);
    }

    [Fact]
    public async Task Window_commands_are_delegated_to_the_ui_dispatcher()
    {
        using var temp = new TempDirectory();
        var window = new FakeWindowCommandDispatcher();
        var service = CreateService(temp.Path, window: window);

        var result = await service.ExecuteWindowCommandAsync("minimize", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        window.Commands.Should().ContainSingle().Which.Should().Be("minimize");
    }

    [Fact]
    public async Task Avalonia_window_dispatcher_executes_commands_through_ui_thread_dispatcher()
    {
        var uiDispatcher = new FakeAvaloniaUiDispatcher();
        var windowDispatcher = new AvaloniaWindowCommandDispatcher(new AvaloniaMainWindowAccessor(), uiDispatcher);

        var executed = await windowDispatcher.ExecuteAsync("maximize", CancellationToken.None);

        executed.Should().BeFalse();
        uiDispatcher.InvocationCount.Should().Be(1);
    }

    private static AvaloniaDesktopActionService CreateService(
        string outputRoot,
        FakePathLauncher? launcher = null,
        FakeArchiveArtifactStore? artifacts = null,
        FakeReportApplicationService? reports = null,
        FakeWindowCommandDispatcher? window = null)
    {
        var data = new ReportRunData(
            "run-1", "Completed", "RUN_COMPLETED", DateTimeOffset.UtcNow,
            [], [], new Dictionary<string, int>(), "reports/run-1/report.xlsx", "hash-1",
            new ReportCandidateCounts(0, 0, 0, 0, 0, 0, 0, 0, 0))
        {
            OutputRoot = outputRoot,
        };
        return new AvaloniaDesktopActionService(
            new FakeDesktopRunService(outputRoot),
            new FakeReportRunDataSource(data),
            artifacts ?? new FakeArchiveArtifactStore(),
            reports ?? new FakeReportApplicationService("reports/run-1/report.xlsx", "hash-1"),
            launcher ?? new FakePathLauncher(),
            window ?? new FakeWindowCommandDispatcher());
    }

    private static ArchiveArtifactSnapshot NewArtifact(
        string runId,
        string documentId,
        string relativePath,
        ArchiveArtifactState state)
        => new(
            $"artifact-{documentId}",
            new ArchiveArtifactKey(runId, documentId, 1, "invoice", "hash"),
            "temp", relativePath, Path.GetFileName(relativePath), "hash", state,
            DateTimeOffset.UtcNow, state == ArchiveArtifactState.Committed ? DateTimeOffset.UtcNow : null);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"invoiceflow-actions-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FakeDesktopRunService(string outputRoot) : IDesktopRunService
    {
        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RunContextSnapshot(false, false, false, 0, null, outputRoot, null, null, null, null));
        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class FakeReportRunDataSource(ReportRunData data) : IReportRunDataSource
    {
        public Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<ReportRunData?>(runId == data.RunId ? data : null);
    }

    private sealed class FakeArchiveArtifactStore : IArchiveArtifactStore
    {
        public IReadOnlyList<ArchiveArtifactSnapshot> Artifacts { get; init; } = [];
        public Task<ArchiveArtifactSnapshot?> FindByKeyAsync(ArchiveArtifactKey key, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task InsertPreparedAsync(ArchiveArtifactSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task MarkCommittedAsync(string artifactId, DateTimeOffset committedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task MarkRecoveryRequiredAsync(string artifactId, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task UpdateCommittedLocationAsync(string artifactId, string relativePath, string finalPath, string fileName,
            IUnitOfWork transaction, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListByRunAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(Artifacts.Where(item => item.Key.RunId == runId).ToArray());
        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListCommittedForInventoryAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(Artifacts.Where(item => item.State == ArchiveArtifactState.Committed).ToArray());
        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListRecoverableAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(Artifacts.Where(item => item.State is ArchiveArtifactState.Prepared or ArchiveArtifactState.RecoveryRequired).ToArray());
    }

    private sealed class FakeReportApplicationService(string reportPath, string contentHash) : IReportApplicationService
    {
        public ReportOpenRequest? IssuedRequest { get; private set; }
        public string? ResolvedTokenId { get; private set; }

        public Task<ReportExportRpcResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ReportOpenToken> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken)
        {
            IssuedRequest = request;
            if (request.ReportPath != reportPath || request.ContentHash != contentHash)
                throw new InvalidOperationException("Not the persisted report.");
            return Task.FromResult(new ReportOpenToken("token-1", request.RunId, reportPath, contentHash, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false));
        }
        public Task<ReportOpenResolution> ResolveTokenAsync(string tokenId, CancellationToken cancellationToken)
        {
            ResolvedTokenId = tokenId;
            return Task.FromResult(new ReportOpenResolution(tokenId, "run-1", reportPath, contentHash, true, null));
        }
    }

    private sealed class FakePathLauncher : IDesktopPathLauncher
    {
        public string? OpenedPath { get; private set; }
        public Exception? Exception { get; init; }
        public Task OpenAsync(string path, CancellationToken cancellationToken)
        {
            OpenedPath = path;
            if (Exception is not null) return Task.FromException(Exception);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindowCommandDispatcher : IWindowCommandDispatcher
    {
        public List<string> Commands { get; } = [];
        public Task<bool> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeAvaloniaUiDispatcher : IAvaloniaUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken)
        {
            InvocationCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }
    }
}
