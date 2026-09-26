namespace InvoiceFlowAI.Contracts.Rules;

public sealed record RuleAction(
    string Kind,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Parameters = null);