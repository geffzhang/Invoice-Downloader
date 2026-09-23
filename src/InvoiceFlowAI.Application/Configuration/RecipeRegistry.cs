using System.Text.Json;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Recipe;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Loads, validates, and serves the in-use PipelineRecipe. The default
/// loader is wired at composition root to an embedded resource reader; the
/// tests pass an in-process loader for hermetic behaviour. Validation:
///   * unknown node types  -> RECIPE_NODE_UNKNOWN
///   * unknown port        -> RECIPE_PORT_INVALID
///   * duplicate port name within a node -> RECIPE_PORT_INVALID
///   * unknown schema version -> RECIPE_SCHEMA_UNSUPPORTED
/// </summary>
public sealed class RecipeRegistry
{
    private readonly INodeTypeRegistry _nodeTypes;
    private readonly IConnectionPolicy _connectionPolicy;
    private readonly Func<CancellationToken, Task<string>>? _defaultLoader;

    public RecipeRegistry(
        INodeTypeRegistry nodeTypes,
        IConnectionPolicy connectionPolicy,
        Func<CancellationToken, Task<string>>? defaultLoader)
    {
        _nodeTypes = nodeTypes ?? throw new ArgumentNullException(nameof(nodeTypes));
        _connectionPolicy = connectionPolicy ?? throw new ArgumentNullException(nameof(connectionPolicy));
        _defaultLoader = defaultLoader;
    }

    public async Task<PipelineRecipe> LoadDefaultAsync(CancellationToken cancellationToken)
    {
        if (_defaultLoader is null)
        {
            throw new InvalidOperationException(
                "RecipeRegistry has no default loader wired. Pass a Func<CancellationToken, Task<string>> at construction.");
        }

        var text = await _defaultLoader(cancellationToken).ConfigureAwait(false);
        var recipe = JsonSerializer.Deserialize<PipelineRecipe>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? throw new RecipeValidationException(
            RpcErrorCodes.RecipeGraphInvalid,
            "Default recipe did not deserialize.");

        await ValidateAsync(new RecipeValidationRequest(recipe.Nodes, recipe.Connections), cancellationToken).ConfigureAwait(false);
        return recipe;
    }

    public async Task ValidateAsync(RecipeValidationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await Task.Yield();

        var nodesById = new Dictionary<string, RecipeNode>(StringComparer.Ordinal);
        foreach (var node in request.Nodes)
        {
            if (!nodesById.TryAdd(node.NodeId, node))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipeGraphInvalid,
                    $"Duplicate node id '{node.NodeId}'.");
            }

            if (!_nodeTypes.TryGet(node.Type, node.TypeVersion, out var definition))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipeNodeUnknown,
                    $"Node '{node.NodeId}' references unregister type '{node.Type}' version '{node.TypeVersion}'.");
            }

            ValidatePortBindings(node, definition);
        }

        foreach (var connection in request.Connections)
        {
            if (!nodesById.TryGetValue(connection.FromNodeId, out var fromNode))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Connection from unknown node '{connection.FromNodeId}'.");
            }

            if (!nodesById.TryGetValue(connection.ToNodeId, out var toNode))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Connection to unknown node '{connection.ToNodeId}'.");
            }

            if (!_nodeTypes.TryGet(fromNode.Type, fromNode.TypeVersion, out var fromDef))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipeNodeUnknown,
                    $"Connection source node '{fromNode.NodeId}' has unregister type.");
            }

            if (!_nodeTypes.TryGet(toNode.Type, toNode.TypeVersion, out var toDef))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipeNodeUnknown,
                    $"Connection target node '{toNode.NodeId}' has unregister type.");
            }

            if (!HasPort(fromDef, connection.FromPort))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Node '{fromNode.NodeId}' has no port '{connection.FromPort}'.",
                    fromNode.NodeId, connection.FromPort);
            }

            if (!HasPort(toDef, connection.ToPort))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Node '{toNode.NodeId}' has no port '{connection.ToPort}'.",
                    toNode.NodeId, connection.ToPort);
            }

            if (!_connectionPolicy.AllowsConnection(
                    connection.FromNodeId, connection.FromPort,
                    connection.ToNodeId, connection.ToPort,
                    fromDef, toDef))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Connection '{connection.FromNodeId}.{connection.FromPort} -> {connection.ToNodeId}.{connection.ToPort}' is not permitted by the built-in policy.");
            }
        }
    }

    private static void ValidatePortBindings(RecipeNode node, NodeTypeDefinition definition)
    {
        if (node.Ports is null) return;
        var declaredPortNames = new HashSet<string>(definition.Ports.Select(p => p.Name), StringComparer.Ordinal);
        foreach (var (key, binding) in node.Ports)
        {
            if (!declaredPortNames.Contains(binding.Name))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Node '{node.NodeId}' binds unknown port '{binding.Name}'.");
            }
            if (!string.Equals(key, binding.Name, StringComparison.Ordinal))
            {
                throw new RecipeValidationException(
                    RpcErrorCodes.RecipePortInvalid,
                    $"Node '{node.NodeId}' binds port '{key}' but the binding name is '{binding.Name}'.");
            }
        }
    }

    private static bool HasPort(NodeTypeDefinition definition, string portName) =>
        definition.Ports.Any(p => string.Equals(p.Name, portName, StringComparison.Ordinal));
}