using InvoiceFlowAI.Contracts.Recipe;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Validation input. The registry runs node / connection / port checks
/// against this graph and either returns an empty result or throws with
/// the first reason code.
/// </summary>
public sealed record RecipeValidationRequest(
    IReadOnlyList<RecipeNode> Nodes,
    IReadOnlyList<RecipeConnection> Connections);