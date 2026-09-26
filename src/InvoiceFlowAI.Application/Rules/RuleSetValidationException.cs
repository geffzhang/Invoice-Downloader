namespace InvoiceFlowAI.Application.Rules;

public sealed class RuleSetValidationException : Exception
{
    public RuleSetValidationException(string reasonCode, string safeMessage)
        : base(safeMessage)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}