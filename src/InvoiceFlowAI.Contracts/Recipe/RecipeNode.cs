namespace InvoiceFlowAI.Contracts.Recipe;

public sealed record RecipeNode(
    string NodeId,
    string Type,
    string TypeVersion,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyDictionary<string, RecipePortBinding>? Ports = null);