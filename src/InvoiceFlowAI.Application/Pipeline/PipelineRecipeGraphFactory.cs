using System.Text.Json;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Pipeline.Nodes;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Recipe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Graph;
using ZeroPipeline.Core.Nodes;
using ZeroPipeline.Core.Ports;
using ZeroPipeline.Recipe.Builder;
using ZeroPipeline.Recipe.Models;
using ZeroPipeline.Recipe.Registry;

namespace InvoiceFlowAI.Application.Pipeline;

public sealed class PipelineRecipeGraphFactory
{
    private readonly RecipeRegistry _recipeRegistry;
    private readonly IServiceProvider _services;

    public PipelineRecipeGraphFactory(RecipeRegistry recipeRegistry, IServiceProvider services)
    {
        _recipeRegistry = recipeRegistry ?? throw new ArgumentNullException(nameof(recipeRegistry));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task<PipelineGraph> BuildAsync(PipelineRecipe recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        await _recipeRegistry.ValidateAsync(
            new RecipeValidationRequest(recipe.Nodes, recipe.Connections),
            cancellationToken).ConfigureAwait(false);

        var nodeRegistry = CreateNodeRegistry();
        var model = new RecipeModel
        {
            RecipeId = recipe.RecipeId,
            Name = recipe.RecipeId,
            Version = recipe.RecipeVersion,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes = recipe.Nodes.Select(node => new NodeRecipeModel
            {
                Id = node.NodeId,
                Name = node.NodeId,
                NodeType = node.Type,
                Parameters = node.Parameters.ToDictionary(
                    pair => pair.Key,
                    pair => JsonSerializer.Serialize(pair.Value),
                    StringComparer.Ordinal),
            }).ToList(),
            Connections = recipe.Connections.Select(connection => new ConnectionRecipeModel
            {
                SourceNodeId = connection.FromNodeId,
                SourcePortName = connection.FromPort,
                TargetNodeId = connection.ToNodeId,
                TargetPortName = connection.ToPort,
            }).ToList(),
        };

        var graph = new RecipeGraphBuilder(nodeRegistry).BuildGraph(model);
        graph.Validate();
        return graph;
    }

    private NodeRegistry CreateNodeRegistry()
    {
        var registry = new NodeRegistry();
        registry.Register("validate-request", (id, name, _) => new ValidateRequestNode(id, name));
        registry.Register("scan-mailbox", (id, name, _) =>
            new ScanMailboxNode(_services.GetRequiredService<IMailboxScanner>(), id, name));
        registry.Register("collect-candidates", (id, name, _) =>
            new CollectCandidatesNode(_services.GetRequiredService<ICandidateCollectionStage>(), id, name));
        registry.Register("recover-urls", (id, name, _) =>
            new RecoverUrlsNode(_services.GetRequiredService<IUrlRecoveryStage>(), id, name));
        registry.Register("extract-documents", (id, name, _) =>
            new ExtractDocumentsNode(_services.GetRequiredService<IDocumentExtractionStage>(), id, name));
        registry.Register("pair-artifacts", (id, name, _) =>
            new PairArtifactsNode(_services.GetRequiredService<IArtifactPairingStage>(), id, name));
        registry.Register("archive-documents", (id, name, _) =>
            new ArchiveDocumentsNode(_services.GetRequiredService<IDocumentArchivingStage>(), id, name));
        registry.Register("export-report", (id, name, _) =>
            new ExportReportNode(_services.GetRequiredService<IReportExportStage>(), id, name));
        return registry;
    }
}

public sealed class PipelineRun : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly PipelineRunInputPort _runInput;

    internal PipelineRun(
        AsyncServiceScope scope,
        PipelineExecutor executor,
        PipelineContext context,
        RunSummaryCaptureNode summaryCapture,
        RunArchiveCaptureNode archiveCapture,
        IReadOnlyList<RunFailureCaptureNode> failureCaptures)
    {
        _scope = scope;
        Executor = executor;
        Context = context;
        SummaryCapture = summaryCapture;
        ArchiveCapture = archiveCapture;
        FailureCaptures = failureCaptures;
        var inputPort = executor.Graph.Nodes.SelectMany(node => node.InputPorts)
            .OfType<ZeroPipeline.Core.Ports.InputPort<InvoiceFlowAI.Domain.Runs.RunInput>>()
            .Single();
        _runInput = new PipelineRunInputPort(inputPort);
    }

    public PipelineExecutor Executor { get; }
    public PipelineContext Context { get; }
    public RunSummaryCaptureNode SummaryCapture { get; }
    public InvoiceFlowAI.Domain.Runs.RunSummary? CompletedSummary => SummaryCapture.Summary;
    public ArchiveBatch? ArchivedBatch => ArchiveCapture.Batch;
    public IReadOnlyList<InvoiceFlowAI.Domain.Runs.RunFailure> Failures =>
        FailureCaptures.SelectMany(capture => capture.Failures).ToArray();

    private RunArchiveCaptureNode ArchiveCapture { get; }
    private IReadOnlyList<RunFailureCaptureNode> FailureCaptures { get; }

    public void Submit(InvoiceFlowAI.Domain.Runs.RunInput input, long sequence = 1) => _runInput.Submit(input, sequence);

    public Task ExecuteStepAsync(CancellationToken cancellationToken = default) =>
        Executor.ExecuteStepAsync(Context, cancellationToken);

    public Task StartStreamingAsync(int maxCycles, CancellationToken cancellationToken = default) =>
        Executor.StartStreamingAsync(Context, maxCycles, cancellationToken);

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    private sealed class PipelineRunInputPort
    {
        private readonly ZeroPipeline.Core.Ports.InputPort<InvoiceFlowAI.Domain.Runs.RunInput> _input;

        public PipelineRunInputPort(ZeroPipeline.Core.Ports.InputPort<InvoiceFlowAI.Domain.Runs.RunInput> input) => _input = input;

        public void Submit(InvoiceFlowAI.Domain.Runs.RunInput input, long sequence) =>
            _input.Deliver(new ZeroPipeline.Core.Ports.DataPacket<InvoiceFlowAI.Domain.Runs.RunInput>(input, sequence, isEndOfStream: false));
    }
}

public sealed class PipelineRunFactory
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PipelineRunFactory(IServiceScopeFactory scopeFactory) =>
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task<PipelineRun> CreateAsync(
        PipelineRecipe recipe,
        CancellationToken cancellationToken)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var graphFactory = scope.ServiceProvider.GetRequiredService<PipelineRecipeGraphFactory>();
            var graph = await graphFactory.BuildAsync(recipe, cancellationToken).ConfigureAwait(false);
            var summaryPort = graph.Nodes.SelectMany(node => node.OutputPorts)
                .OfType<OutputPort<InvoiceFlowAI.Domain.Runs.RunSummary>>()
                .Single();
            var summaryCapture = new RunSummaryCaptureNode();
            graph.AddNode(summaryCapture);
            graph.Connect(summaryPort, summaryCapture.Input);

            var archivePort = graph.Nodes.SelectMany(node => node.OutputPorts)
                .OfType<OutputPort<PipelineItem<ArchiveBatch>>>()
                .Single();
            var archiveCapture = new RunArchiveCaptureNode();
            graph.AddNode(archiveCapture);
            graph.Connect(archivePort, archiveCapture.Input);

            var failurePorts = graph.Nodes.SelectMany(node => node.OutputPorts)
                .OfType<OutputPort<InvoiceFlowAI.Domain.Runs.RunFailure>>()
                .ToArray();
            var failureCaptures = failurePorts.Select((port, index) =>
            {
                var capture = new RunFailureCaptureNode($"run-failure-capture-{index}");
                graph.AddNode(capture);
                graph.Connect(port, capture.Input);
                return capture;
            }).ToArray();

            var executor = new PipelineExecutor(graph);
            var context = new PipelineContext(cancellationToken);
            await executor.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
            return new PipelineRun(scope, executor, context, summaryCapture, archiveCapture, failureCaptures);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public static class PipelineApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddInvoiceFlowApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<INodeTypeRegistry>(BuiltInNodeTypes.Default);
        services.AddSingleton<IConnectionPolicy>(BuiltInConnections.Default);
        services.AddSingleton<IMailboxChannelRegistry, MailboxChannelRegistry>();
        services.AddSingleton<IEmailTierClassifier, EmailTierClassifier>();
        services.AddSingleton<IAttachmentCandidatePolicy, AttachmentCandidatePolicy>();
        services.AddSingleton<IPairingEngine, PairingEngine>();
        services.AddScoped<IArtifactPairingStage, ArtifactPairingStage>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ActiveRunRegistry>();
        services.AddScoped<IDesktopRunService, DesktopRunService>();
        services.AddScoped<RunResultsQueryService>();
        services.AddScoped<IDesktopRunExecutor, RunExecutionService>();
        services.AddScoped<ITerminalDecisionService, TerminalDecisionService>();
        services.AddScoped<IReportExportStage, ReportExportStage>();
        services.AddScoped<IRunCoordinator, RunCoordinator>();
        services.AddScoped<IEventReplayService, EventReplayService>();
        services.AddScoped<ICheckpointService, CheckpointService>();
        services.AddSingleton(provider => new RecipeRegistry(
            provider.GetRequiredService<INodeTypeRegistry>(),
            provider.GetRequiredService<IConnectionPolicy>(),
            DefaultPipelineRecipeLoader.LoadAsync));
        services.AddScoped<PipelineRecipeGraphFactory>();
        services.AddSingleton<PipelineRunFactory>();
        return services;
    }
}

public sealed class RunSummaryCaptureNode : PipelineNode
{
    public RunSummaryCaptureNode() : base("run-summary-capture", "Run summary capture") =>
        Input = AddInputPort<InvoiceFlowAI.Domain.Runs.RunSummary>("Completed", 1, BackpressurePolicy.Block);

    public InputPort<InvoiceFlowAI.Domain.Runs.RunSummary> Input { get; }
    public InvoiceFlowAI.Domain.Runs.RunSummary? Summary { get; private set; }

    protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Input.TryReceive(out var packet) && !packet.IsEndOfStream)
        {
            Summary = packet.Payload;
        }
        return Task.CompletedTask;
    }
}

public sealed class RunArchiveCaptureNode : PipelineNode
{
    public RunArchiveCaptureNode() : base("run-archive-capture", "Run archive capture") =>
        Input = AddInputPort<PipelineItem<ArchiveBatch>>("Archived", 1, BackpressurePolicy.Block);

    public InputPort<PipelineItem<ArchiveBatch>> Input { get; }
    public ArchiveBatch? Batch { get; private set; }

    protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Input.TryReceive(out var packet) && !packet.IsEndOfStream)
        {
            Batch = packet.Payload.Payload;
        }
        return Task.CompletedTask;
    }
}

public sealed class RunFailureCaptureNode : PipelineNode
{
    private readonly List<InvoiceFlowAI.Domain.Runs.RunFailure> _failures = [];

    public RunFailureCaptureNode(string id) : base(id, "Run failure capture") =>
        Input = AddInputPort<InvoiceFlowAI.Domain.Runs.RunFailure>("Failure", 16, BackpressurePolicy.Block);

    public InputPort<InvoiceFlowAI.Domain.Runs.RunFailure> Input { get; }
    public IReadOnlyList<InvoiceFlowAI.Domain.Runs.RunFailure> Failures => _failures;

    protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (Input.TryReceive(out var packet))
        {
            if (!packet.IsEndOfStream)
            {
                _failures.Add(packet.Payload);
            }
        }
        return Task.CompletedTask;
    }
}