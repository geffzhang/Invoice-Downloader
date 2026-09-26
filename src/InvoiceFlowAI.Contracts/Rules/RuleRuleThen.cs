namespace InvoiceFlowAI.Contracts.Rules;

/// <summary>Action applied to documents that match the rule's <c>When</c>.</summary>
public sealed record RuleRuleThen(
    string ArchiveFolder,
    string Category,
    bool RequireManualReview,
    bool AllowCrossMessagePairing);