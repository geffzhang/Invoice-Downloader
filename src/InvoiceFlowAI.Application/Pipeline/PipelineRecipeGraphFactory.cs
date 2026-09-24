using System.Text.Json;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pipeline.Nodes;
using InvoiceFlowAI.Contracts.Recipe;
using Microsoft.Extensions.DependencyInjection;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Graph;
using ZeroPipeline.Core.Nodes;
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

    internal PipelineRun(AsyncServiceScope scope, PipelineExecutor executor, PipelineContext context)
    {
        _scope = scope;
        Executor = executor;
        Context = context;
        var inputPort = executor.Graph.Nodes.SelectMany(node => node.InputPorts)
            .OfType<ZeroPipeline.Core.Ports.InputPort<InvoiceFlowAI.Domain.Runs.RunInput>>()
            .Single();
        _runInput = new PipelineRunInputPort(inputPort);
    }

    public PipelineExecutor Executor { get; }
    public PipelineContext Context { get; }

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
            var executor = new PipelineExecutor(graph);
            var context = new PipelineContext(cancellationToken);
            await executor.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
            return new PipelineRun(scope, executor, context);
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
        services.AddSingleton(provider => new RecipeRegistry(
            provider.GetRequiredService<INodeTypeRegistry>(),
            provider.GetRequiredService<IConnectionPolicy>(),
            defaultLoader: null));
        services.AddScoped<PipelineRecipeGraphFactory>();
        services.AddSingleton<PipelineRunFactory>();
        return services;
    }
}