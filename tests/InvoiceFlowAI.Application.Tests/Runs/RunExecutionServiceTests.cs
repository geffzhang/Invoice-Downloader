using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class RunExecutionServiceTests
{
    [Fact]
    public async Task Run_publishes_monotonic_progress_with_scanned_email_count()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner, EmptyMailboxScanner>();
        services.AddSingleton<ICandidateCollectionStage, EmptyCandidateStage>();
        services.AddSingleton<IUrlRecoveryStage, PassThroughRecoveryStage>();
        services.AddSingleton<IDocumentExtractionStage, EmptyExtractionStage>();
        services.AddSingleton<IArtifactPairingStage, EmptyPairingStage>();
        services.AddSingleton<IDocumentArchivingStage, EmptyArchiveStage>();
        services.AddSingleton<IReportExportStage, CompletedReportStage>();
        await using var provider = services.BuildServiceProvider();
        var coordinator = new RecordingCoordinator(order);
        coordinator.TerminalEventSequence = 73;
        var publisher = new RecordingEventPublisher(order);
        var service = new RunExecutionService(
            provider.GetRequiredService<InvoiceFlowAI.Application.Configuration.RecipeRegistry>(),
            provider.GetRequiredService<PipelineRunFactory>(),
            coordinator,
            publisher,
            TimeProvider.System);

        await service.ExecuteAsync(new RunStartRequest(
            "run-empty", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            Path.GetTempPath(), "Example Co", "standard"), CancellationToken.None);

        coordinator.FinalizationRequests.Should().ContainSingle();
        coordinator.FinalizationRequests[0].RunId.Should().Be("run-empty");
        coordinator.FinalizationRequests[0].AllCandidatesArrived.Should().BeTrue();
        coordinator.FinalizationRequests[0].CandidateResults.Should().BeEmpty();
        publisher.Events.Select(item => item.EventName).Should().ContainInOrder("run.progress", "run.terminal");
        var progress = publisher.Events.Where(item => item.EventName == "run.progress")
            .Select(item => item.Payload.Should().BeOfType<RunProgressPayload>().Subject.Percent)
            .ToArray();
        progress.Should().HaveCountGreaterThan(1);
        progress.Should().OnlyHaveUniqueItems();
        progress.Should().BeInAscendingOrder();
        publisher.Events.Where(item => item.EventName == "run.progress")
            .Select(item => item.Payload.Should().BeOfType<RunProgressPayload>().Subject.Stats)
            .Where(stats => stats is not null)
            .Select(stats => stats!.Emails)
            .Should().Contain(2);
        publisher.Events.Last().Sequence.Should().Be(73);
        order.IndexOf("commit:1").Should().BeLessThan(order.IndexOf("publish:1"));
        order.IndexOf("finalize").Should().BeLessThan(order.IndexOf("publish:73"));
    }

    [Fact]
    public async Task Candidate_result_is_finalized_and_its_event_is_published_after_commit()
    {
        var order = new List<string>();
        var candidateResult = NewCandidateResult();
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner, EmptyMailboxScanner>();
        services.AddSingleton<ICandidateCollectionStage>(new OneResultCandidateStage(candidateResult));
        services.AddSingleton<IUrlRecoveryStage, PassThroughRecoveryStage>();
        services.AddSingleton<IDocumentExtractionStage, CandidateExtractionStage>();
        services.AddSingleton<IArtifactPairingStage, EmptyPairingStage>();
        services.AddSingleton<IDocumentArchivingStage, EmptyArchiveStage>();
        services.AddSingleton<IReportExportStage, CompletedReportStage>();
        await using var provider = services.BuildServiceProvider();
        var coordinator = new RecordingCoordinator(order);
        var publisher = new RecordingEventPublisher(order);
        var service = new RunExecutionService(
            provider.GetRequiredService<InvoiceFlowAI.Application.Configuration.RecipeRegistry>(),
            provider.GetRequiredService<PipelineRunFactory>(), coordinator, publisher, TimeProvider.System);

        await service.ExecuteAsync(new RunStartRequest(
            "run-one", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            Path.GetTempPath(), "Example Co", "standard"), CancellationToken.None);

        coordinator.FinalizationRequests.Should().ContainSingle();
        coordinator.FinalizationRequests[0].CandidateResults.Should().ContainSingle()
            .Which.Candidate.DocumentId.Value.Should().Be("document-1");
        coordinator.PacketRequests.Should().ContainSingle(item => item.EventType == "run.candidate");
        var candidateEvent = publisher.Events.Single(item => item.EventName == "run.candidate");
        candidateEvent.Sequence.Should().Be(coordinator.PacketRequests.Single(item => item.EventType == "run.candidate").EventSequence);
        candidateEvent.Payload.Should().BeOfType<RunCandidateEventPayload>()
            .Which.DocumentId.Should().Be("document-1");
        order.IndexOf("commit:2").Should().BeLessThan(order.IndexOf("publish:2"));
        JsonSerializer.Serialize(candidateEvent.Payload).Should().NotContain("TOP_SECRET");
    }

    [Fact]
    public async Task Failed_candidate_commit_is_not_published_and_terminal_uses_durable_sequence()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner, EmptyMailboxScanner>();
        services.AddSingleton<ICandidateCollectionStage>(new OneResultCandidateStage(NewCandidateResult()));
        services.AddSingleton<IUrlRecoveryStage, PassThroughRecoveryStage>();
        services.AddSingleton<IDocumentExtractionStage, CandidateExtractionStage>();
        services.AddSingleton<IArtifactPairingStage, EmptyPairingStage>();
        services.AddSingleton<IDocumentArchivingStage, EmptyArchiveStage>();
        services.AddSingleton<IReportExportStage, CompletedReportStage>();
        await using var provider = services.BuildServiceProvider();
        var coordinator = new RecordingCoordinator(order)
        {
            FailPacketEventType = "run.candidate",
        };
        var publisher = new RecordingEventPublisher(order);
        var service = new RunExecutionService(
            provider.GetRequiredService<InvoiceFlowAI.Application.Configuration.RecipeRegistry>(),
            provider.GetRequiredService<PipelineRunFactory>(), coordinator, publisher, TimeProvider.System);

        await service.ExecuteAsync(new RunStartRequest(
            "run-commit-failed", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            Path.GetTempPath(), "Example Co", "standard"), CancellationToken.None);

        coordinator.FinalizationRequests.Should().ContainSingle();
        coordinator.FinalizationRequests[0].RunFailure.Should().NotBeNull();
        publisher.Events.Should().ContainSingle(item => item.EventName == "run.terminal");
        publisher.Events.Should().NotContain(item => item.EventName == "run.candidate");
        publisher.Events.Single(item => item.EventName == "run.terminal").Sequence.Should().Be(3);
    }

    [Fact]
    public async Task Stage_failure_is_forwarded_to_finalization_as_safe_run_failure()
    {
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner, EmptyMailboxScanner>();
        services.AddSingleton<ICandidateCollectionStage, ThrowingCandidateStage>();
        services.AddSingleton<IUrlRecoveryStage, PassThroughRecoveryStage>();
        services.AddSingleton<IDocumentExtractionStage, EmptyExtractionStage>();
        services.AddSingleton<IArtifactPairingStage, EmptyPairingStage>();
        services.AddSingleton<IDocumentArchivingStage, EmptyArchiveStage>();
        services.AddSingleton<IReportExportStage, CompletedReportStage>();
        await using var provider = services.BuildServiceProvider();
        var coordinator = new RecordingCoordinator(order);
        var publisher = new RecordingEventPublisher(order);
        var service = new RunExecutionService(
            provider.GetRequiredService<InvoiceFlowAI.Application.Configuration.RecipeRegistry>(),
            provider.GetRequiredService<PipelineRunFactory>(), coordinator, publisher, TimeProvider.System);

        await service.ExecuteAsync(new RunStartRequest(
            "run-failed-stage", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            Path.GetTempPath(), "Example Co", "standard"), CancellationToken.None);

        coordinator.FinalizationRequests.Should().ContainSingle();
        var runFailure = coordinator.FinalizationRequests[0].RunFailure;
        runFailure.Should().NotBeNull();
        runFailure!.ReasonCode.Should().Be("COLLECT_CANDIDATES_FAILED");
        runFailure.SafeMessage.Should().Be("The pipeline stage failed.");
        publisher.Events.Last().EventName.Should().Be("run.terminal");
        order.IndexOf("finalize").Should().BeLessThan(order.IndexOf("publish:" + publisher.Events.Last().Sequence));
    }

    [Fact]
    public async Task Cancellation_disposes_pipeline_then_finalizes_once_before_terminal_publish()
    {
        using var cancellation = new CancellationTokenSource();
        var order = new List<string>();
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner>(new CancellingMailboxScanner(cancellation));
        services.AddSingleton<ICandidateCollectionStage, EmptyCandidateStage>();
        services.AddSingleton<IUrlRecoveryStage, PassThroughRecoveryStage>();
        services.AddSingleton<IDocumentExtractionStage, EmptyExtractionStage>();
        services.AddSingleton<IArtifactPairingStage, EmptyPairingStage>();
        services.AddSingleton<IDocumentArchivingStage, EmptyArchiveStage>();
        services.AddSingleton<IReportExportStage, CompletedReportStage>();
        await using var provider = services.BuildServiceProvider();
        var coordinator = new RecordingCoordinator(order);
        var publisher = new RecordingEventPublisher(order);
        var service = new RunExecutionService(
            provider.GetRequiredService<InvoiceFlowAI.Application.Configuration.RecipeRegistry>(),
            provider.GetRequiredService<PipelineRunFactory>(), coordinator, publisher, TimeProvider.System);

        await service.ExecuteAsync(new RunStartRequest(
            "run-cancelled", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            Path.GetTempPath(), "Example Co", "standard"), cancellation.Token);

        coordinator.FinalizationRequests.Should().ContainSingle();
        coordinator.FinalizationRequests[0].CancellationRequested.Should().BeTrue();
        coordinator.FinalizationRequests[0].AllCandidatesArrived.Should().BeTrue();
        order.Count(item => item == "finalize").Should().Be(1);
        publisher.Events.Last().EventName.Should().Be("run.terminal");
        order.IndexOf("finalize").Should().BeLessThan(order.IndexOf("publish:" + publisher.Events.Last().Sequence));
    }

    private static CandidateProcessResult NewCandidateResult() => new(
        new DocumentCandidate(
            DocumentIdentity.Create("document-1"), 1, "correlation-1", "uid-1", "invoice.pdf",
            "application/pdf", 10, 1, "attachment"),
        CandidateStatus.Resolved);

    private sealed class EmptyMailboxScanner : IMailboxScanner
    {
        public Task<MailboxScanResult> ScanAsync(MailboxScanRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new MailboxScanResult(
                [new MailboxMessage("INBOX", "1", 1, null, "", "", [], false),
                 new MailboxMessage("INBOX", "2", 1, null, "", "", [], false)],
                Array.Empty<MailboxAttachmentCandidate>(), 2, "", false));
    }

    private sealed class CancellingMailboxScanner(CancellationTokenSource cancellation) : IMailboxScanner
    {
        public Task<MailboxScanResult> ScanAsync(MailboxScanRequest request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<MailboxScanResult>(cancellationToken);
        }
    }

    private sealed class EmptyCandidateStage : ICandidateCollectionStage
    {
        public Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken)
            => Task.FromResult(new CandidateBatch(Array.Empty<CandidateWorkItem>()));
    }

    private sealed class OneResultCandidateStage(CandidateProcessResult result) : ICandidateCollectionStage
    {
        public Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken)
            => Task.FromResult(new CandidateBatch(Array.Empty<CandidateWorkItem>(), [result]));
    }

    private sealed class ThrowingCandidateStage : ICandidateCollectionStage
    {
        public Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken)
            => throw new InvalidOperationException("TOP_SECRET exception details");
    }

    private sealed class PassThroughRecoveryStage : IUrlRecoveryStage
    {
        public Task<CandidateBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken) => Task.FromResult(input);
    }

    private sealed class EmptyExtractionStage : IDocumentExtractionStage
    {
        public Task<ExtractionBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken)
            => Task.FromResult(new ExtractionBatch(Array.Empty<CandidateProcessResult>()));
    }

    private sealed class CandidateExtractionStage : IDocumentExtractionStage
    {
        public Task<ExtractionBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken)
            => Task.FromResult(new ExtractionBatch(input.EffectiveTerminalResults));
    }

    private sealed class EmptyPairingStage : IArtifactPairingStage
    {
        public Task<PairingBatch> ExecuteAsync(ExtractionBatch input, CancellationToken cancellationToken)
            => Task.FromResult(new PairingBatch(Array.Empty<PairingResult>(), input.Results));
    }

    private sealed class EmptyArchiveStage : IDocumentArchivingStage
    {
        public Task<ArchiveBatch> ExecuteAsync(ArchiveStageRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ArchiveBatch(request.Batch.CandidateResults, Array.Empty<RunFailure>()));
    }

    private sealed class CompletedReportStage : IReportExportStage
    {
        public Task<RunSummary> ExecuteAsync(PipelineItem<ArchiveBatch> input, CancellationToken cancellationToken)
            => Task.FromResult(new RunSummary(
                input.RunId, RunTerminalStatus.Completed, "RUN_COMPLETED",
                0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), null, null,
                DateTimeOffset.UtcNow, false, Array.Empty<RunFailure>()));
    }

    private sealed class RecordingCoordinator(List<string> order) : IRunCoordinator
    {
        public List<RunFinalizationRequest> FinalizationRequests { get; } = [];
        public List<PacketCommitRequest> PacketRequests { get; } = [];
        public long? TerminalEventSequence { get; set; }
        public string? FailPacketEventType { get; set; }
        private long LastCommittedSequence { get; set; }

        public Task<RunCommitResult> CommitPacketAsync(PacketCommitRequest request, CancellationToken cancellationToken)
        {
            PacketRequests.Add(request);
            order.Add($"commit:{request.EventSequence}");
            if (request.EventType == FailPacketEventType)
            {
                return Task.FromException<RunCommitResult>(new InvalidOperationException("packet commit failed"));
            }
            LastCommittedSequence = Math.Max(LastCommittedSequence, request.EventSequence);
            return Task.FromResult(new RunCommitResult(request.RunId, request.EventSequence,
                request.EventSequence + 1, false, null));
        }

        public Task<RunTerminalDecision> FinalizeAsync(RunFinalizationRequest request, CancellationToken cancellationToken)
        {
            order.Add("finalize");
            FinalizationRequests.Add(request);
            var decision = new TerminalDecisionService().Decide(
                request.RunId, request.CandidateResults, request.RunFailure, request.CancellationRequested,
                request.AllCandidatesArrived, request.FinalizerFailures, request.CompletedAtUtc);
            return Task.FromResult(decision with { TerminalEventSequence = TerminalEventSequence ?? LastCommittedSequence + 1 });
        }
    }

    private sealed class RecordingEventPublisher(List<string> order) : IRunEventPublisher
    {
        public List<(string EventName, object Payload, string RunId, long Sequence)> Events { get; } = [];

        public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
        {
            order.Add($"publish:{eventSequence}");
            Events.Add((eventName, payload, runId, eventSequence));
        }
    }
}