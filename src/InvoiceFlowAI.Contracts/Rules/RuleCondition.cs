namespace InvoiceFlowAI.Contracts.Rules;

public sealed record RuleCondition(
    string Field,
    string Operator,
    string? Value = null,
    IReadOnlyList<string>? AnyOf = null);