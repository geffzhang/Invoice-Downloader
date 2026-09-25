using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Application.Pipeline.Nodes;
using InvoiceFlowAI.Contracts.Recipe;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using Microsoft.Extensions.DependencyInjection;
using ZeroPipeline.Core.Ports;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class PipelineRecipeGraphFactoryTests
{
    [Fact]
    public async Task Builds_and_executes_all_eight_typed_nodes_in_recipe_order()
    {
        var calls = new List<string>();
        var services = CreateServices(calls);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<PipelineRunFactory>();
        await using var run = await factory.CreateAsync(CreateRecipe(), CancellationToken.None);
        run.Executor.Graph.NodeCount.Should().Be(8);
        run.Executor.Graph.ConnectionCount.Should().Be(7);

        run.Submit(new RunInput(
            "run-42",
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 2),
            "output",
            string.Empty,
            "account-1"));

        for (var cycle = 0; cycle < 3; cycle++)
        {
            await run.ExecuteStepAsync();
        }

        calls.Should().Equal("scan", "collect", "recover", "extract", "pair", "archive", "report");
        run.Executor.Graph.Nodes.Select(node => node.GetType().Name).Should().Contain(
            new[]
            {
                "ValidateRequestNode", "ScanMailboxNode", "CollectCandidatesNode", "RecoverUrlsNode",
                "ExtractDocumentsNode", "PairArtifactsNode", "ArchiveDocumentsNode", "ExportReportNode",
            });
    }

    [Fact]
    public async Task Fails_graph_creation_when_a_required_stage_is_not_registered()
    {
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner, FakeScanner>();
        services.AddSingleton<IUrlRecoveryStage, FakeRecoveryStage>();
        services.AddSingleton<IDocumentExtractionStage, FakeExtractionStage>();
        services.AddSingleton<IArtifactPairingStage, FakePairingStage>();
        services.AddSingleton<IDocumentArchivingStage, FakeArchiveStage>();
        services.AddSingleton<IReportExportStage, FakeReportStage>();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<PipelineRunFactory>();

        var act = () => factory.CreateAsync(CreateRecipe(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ICandidateCollectionStage*");
    }

    [Fact]
    public async Task Cancellation_from_stage_is_propagated_not_converted_to_failure_packet()
    {
        using var cancellation = new CancellationTokenSource();
        var scanner = new FakeScanner();
        var node = new CollectCandidatesNode(new CancellingCollectionStage(cancellation));
        var input = new SourceNode<PipelineItem<MailboxScanResult>>(
            "scan-source",
            new PipelineItem<MailboxScanResult>("run-cancel", EmptyScan(), 1, IsFinal: true));
        var graph = new ZeroPipeline.Core.Graph.PipelineGraph();
        graph.AddNode(input).AddNode(node);
        graph.Connect(input.Output, node.InputPorts.OfType<InputPort<PipelineItem<MailboxScanResult>>>().Single());
        var executor = new ZeroPipeline.Core.Execution.PipelineExecutor(graph);
        var context = new ZeroPipeline.Core.Execution.PipelineContext(CancellationToken.None);
        await executor.InitializeAsync(context, CancellationToken.None);

        var act = () => executor.ExecuteStepAsync(context, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        node.OutputPorts.OfType<OutputPort<RunFailure>>().Single().IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Stage_exception_is_emitted_on_failure_port_with_safe_message()
    {
        var node = new CollectCandidatesNode(new ThrowingCollectionStage());
        var input = new SourceNode<PipelineItem<MailboxScanResult>>(
            "scan-source",
            new PipelineItem<MailboxScanResult>("run-fail", EmptyScan(), 1, IsFinal: true));
        var sink = new FailureCaptureNode();
        var graph = new ZeroPipeline.Core.Graph.PipelineGraph();
        graph.AddNode(input).AddNode(node).AddNode(sink);
        graph.Connect(input.Output, node.InputPorts.OfType<InputPort<PipelineItem<MailboxScanResult>>>().Single());
        graph.Connect(node.OutputPorts.OfType<OutputPort<RunFailure>>().Single(), sink.Input);
        var executor = new ZeroPipeline.Core.Execution.PipelineExecutor(graph);
        var context = new ZeroPipeline.Core.Execution.PipelineContext(CancellationToken.None);
        await executor.InitializeAsync(context, CancellationToken.None);

        await executor.ExecuteStepAsync(context, CancellationToken.None);

        sink.Failure.Should().NotBeNull();
        sink.Failure!.RunId.Should().Be("run-fail");
        sink.Failure.ReasonCode.Should().Be("COLLECT_CANDIDATES_FAILED");
        sink.Failure.SafeMessage.Should().Be("The pipeline stage failed.");
    }

    private static ServiceCollection CreateServices(List<string> calls)
    {
        var services = new ServiceCollection();
        services.AddInvoiceFlowApplication();
        services.AddSingleton<IMailboxScanner>(new FakeScanner(calls));
        services.AddSingleton<ICandidateCollectionStage>(new FakeCollectionStage(calls));
        services.AddSingleton<IUrlRecoveryStage>(new FakeRecoveryStage(calls));
        services.AddSingleton<IDocumentExtractionStage>(new FakeExtractionStage(calls));
        services.AddSingleton<IArtifactPairingStage>(new FakePairingStage(calls));
        services.AddSingleton<IDocumentArchivingStage>(new FakeArchiveStage(calls));
        services.AddSingleton<IReportExportStage>(new FakeReportStage(calls));
        return services;
    }

    private static PipelineRecipe CreateRecipe()
    {
        var nodes = new[]
        {
            Node("validate", "validate-request"),
            Node("scan", "scan-mailbox"),
            Node("candidates", "collect-candidates"),
            Node("recover", "recover-urls"),
            Node("extract", "extract-documents"),
            Node("pair", "pair-artifacts"),
            Node("archive", "archive-documents"),
            Node("report", "export-report"),
        };
        var edges = new[]
        {
            new RecipeConnection("validate", "Valid", "scan", "Input"),
            new RecipeConnection("scan", "Messages", "candidates", "Messages"),
            new RecipeConnection("candidates", "Candidates", "recover", "Candidates"),
            new RecipeConnection("recover", "Candidates", "extract", "Candidates"),
            new RecipeConnection("extract", "Results", "pair", "Results"),
            new RecipeConnection("pair", "Pairs", "archive", "Pairs"),
            new RecipeConnection("archive", "Archived", "report", "Archived"),
        };
        return new PipelineRecipe(
            "1.0",
            "test-recipe",
            "1",
            "1.2.0",
            nodes,
            edges,
            new RecipeExecutionPolicy(1, 4, 8, 4, 8, 64, 1, 2, 2, 2, 1, 2, 1, 32, 32, 2, 300, 600));
    }

    private static RecipeNode Node(string id, string type) =>
        new(id, type, "1.0", new Dictionary<string, object?>());

    private static MailboxScanResult EmptyScan() =>
        new(Array.Empty<MailboxMessage>(), Array.Empty<MailboxAttachmentCandidate>(), 0, string.Empty, false);

    private sealed class FakeScanner(List<string>? calls = null) : IMailboxScanner
    {
        public Task<MailboxScanResult> ScanAsync(MailboxScanRequest request, CancellationToken cancellationToken)
        {
            calls?.Add("scan");
            return Task.FromResult(EmptyScan());
        }
    }

    private sealed class FakeCollectionStage(List<string>? calls = null) : ICandidateCollectionStage
    {
        public Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken)
        {
            calls?.Add("collect");
            return Task.FromResult(new CandidateBatch(Array.Empty<CandidateWorkItem>()));
        }
    }

    private sealed class FakeRecoveryStage(List<string>? calls = null) : IUrlRecoveryStage
    {
        public Task<CandidateBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken)
        {
            calls?.Add("recover");
            return Task.FromResult(input);
        }
    }

    private sealed class FakeExtractionStage(List<string>? calls = null) : IDocumentExtractionStage
    {
        public Task<ExtractionBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken)
        {
            calls?.Add("extract");
            return Task.FromResult(new ExtractionBatch(Array.Empty<CandidateProcessResult>()));
        }
    }

    private sealed class FakePairingStage(List<string>? calls = null) : IArtifactPairingStage
    {
        public Task<PairingBatch> ExecuteAsync(ExtractionBatch input, CancellationToken cancellationToken)
        {
            calls?.Add("pair");
            return Task.FromResult(new PairingBatch(Array.Empty<PairingResult>(), input.Results));
        }
    }

    private sealed class FakeArchiveStage(List<string>? calls = null) : IDocumentArchivingStage
    {
        public Task<ArchiveBatch> ExecuteAsync(ArchiveStageRequest request, CancellationToken cancellationToken)
        {
            calls?.Add("archive");
            return Task.FromResult(new ArchiveBatch(request.Batch.CandidateResults, Array.Empty<RunFailure>()));
        }
    }

    private sealed class FakeReportStage(List<string>? calls = null) : IReportExportStage
    {
        public Task<RunSummary> ExecuteAsync(PipelineItem<ArchiveBatch> input, CancellationToken cancellationToken)
        {
            calls?.Add("report");
            return Task.FromResult(new RunSummary(
                input.RunId,
                RunTerminalStatus.Completed,
                "COMPLETED",
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                new Dictionary<string, int>(),
                null,
                null,
                DateTimeOffset.UtcNow,
                false,
                Array.Empty<RunFailure>()));
        }
    }

    private sealed class CancellingCollectionStage(CancellationTokenSource source) : ICandidateCollectionStage
    {
        public Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CandidateBatch(Array.Empty<CandidateWorkItem>()));
        }
    }

    private sealed class ThrowingCollectionStage : ICandidateCollectionStage
    {
        public Task<CandidateBatch> ExecuteAsync(MailboxScanResult input, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive internal detail");
    }

    private sealed class SourceNode<T> : ZeroPipeline.Core.Nodes.PipelineNode
    {
        private readonly T _value;
        private bool _sent;

        public SourceNode(string id, T value) : base(id, id)
        {
            _value = value;
            Output = AddOutputPort<T>("Output");
        }

        public OutputPort<T> Output { get; }

        protected override Task OnExecuteAsync(ZeroPipeline.Core.Execution.PipelineContext context, CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                Output.Emit(_value, 1);
                _sent = true;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FailureCaptureNode : ZeroPipeline.Core.Nodes.PipelineNode
    {
        public FailureCaptureNode() : base("failure-sink", "Failure sink") =>
            Input = AddInputPort<RunFailure>("Input", 1, BackpressurePolicy.Block);

        public InputPort<RunFailure> Input { get; }
        public RunFailure? Failure { get; private set; }

        protected override Task OnExecuteAsync(ZeroPipeline.Core.Execution.PipelineContext context, CancellationToken cancellationToken)
        {
            if (Input.TryReceive(out var packet) && !packet.IsEndOfStream) Failure = packet.Payload;
            return Task.CompletedTask;
        }
    }
}