namespace InvoiceFlowAI.Contracts.Rules;

/// <summary>Pre-conditions that select documents for the rule action.</summary>
public sealed record RuleRuleWhen(
    string? DocumentType = null,
    string? SellerContains = null);