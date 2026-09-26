namespace InvoiceFlowAI.Contracts.Recipe;

/// <summary>
/// Immutable, diagnostic-only DAG descriptor. Holds no auth code, mail
/// body, OCR text, raw image, temporary path, or un-redacted URL.
/// </summary>
public sealed record PipelineRecipe(
    string SchemaVersion,
    string RecipeId,
    string RecipeVersion,
    string ZeroPipelineRecipeVersion,
    IReadOnlyList<RecipeNode> Nodes,
    IReadOnlyList<RecipeConnection> Connections,
    RecipeExecutionPolicy Execution,
    IReadOnlyDictionary<string, string>? Metadata = null);