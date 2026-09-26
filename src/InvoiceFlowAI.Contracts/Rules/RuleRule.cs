namespace InvoiceFlowAI.Contracts.Rules;

/// <summary>
/// One rule in a RuleSet. <c>When</c> carries the input predicates;
/// <c>Then</c> carries the archive-folder / category / review decisions.
/// Shape mirrors <c>rules/default.v1.json</c> exactly.
/// </summary>
public sealed record RuleRule(
    string RuleId,
    int Priority,
    bool Enabled,
    RuleRuleWhen When,
    RuleRuleThen Then);