namespace InvoiceFlowAI.Contracts.Rules;

/// <summary>Schema-fixed, persisted representation of a RuleSet version.</summary>
public sealed record RuleSetDocument(
    string SchemaVersion,
    string RuleSetId,
    IReadOnlyList<RuleRule> Rules);