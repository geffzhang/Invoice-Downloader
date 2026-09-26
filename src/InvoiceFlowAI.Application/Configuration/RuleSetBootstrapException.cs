namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Surfaced when the bootstrapper cannot reconcile an existing RuleSet
/// version with the canonical built-in definition. The reason code is one
/// of the stable public constants in
/// <see cref="InvoiceFlowAI.Contracts.Errors.RpcErrorCodes"/>.
/// </summary>
public sealed class RuleSetBootstrapException : Exception
{
    public RuleSetBootstrapException(string reasonCode, string safeMessage)
        : base(safeMessage)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}