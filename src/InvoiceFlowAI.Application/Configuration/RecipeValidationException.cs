namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Surfaced when the recipe registry rejects a recipe. Reason code is
/// one of the <c>RECIPE_*</c> constants in
/// <see cref="InvoiceFlowAI.Contracts.Errors.RpcErrorCodes"/>.
/// </summary>
public sealed class RecipeValidationException : Exception
{
    public RecipeValidationException(string reasonCode, string safeMessage)
        : base(safeMessage)
    {
        ReasonCode = reasonCode;
        FailedNodeId = string.Empty;
        FailedPort = string.Empty;
    }

    public RecipeValidationException(string reasonCode, string safeMessage, string failedNodeId, string failedPort)
        : base(safeMessage)
    {
        ReasonCode = reasonCode;
        FailedNodeId = failedNodeId;
        FailedPort = failedPort;
    }

    public string ReasonCode { get; }
    public string FailedNodeId { get; }
    public string FailedPort { get; }
}