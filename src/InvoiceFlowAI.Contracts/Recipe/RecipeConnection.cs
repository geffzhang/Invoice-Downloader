namespace InvoiceFlowAI.Contracts.Recipe;

public sealed record RecipeConnection(
    string FromNodeId,
    string FromPort,
    string ToNodeId,
    string ToPort);