using InvoiceFlowAI.Contracts.Rules;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Persistence boundary the RuleSet bootstrapper depends on. The production
/// EF Core implementation lands in Task 4; the unit-test in-memory
/// implementation lives in the test project.
/// </summary>
public interface IRuleSetBootstrapStore
{
    Task<RuleSetDocument?> FindCurrentAsync(string ruleSetId, CancellationToken cancellationToken);

    Task SaveAsync(RuleSetDocument document, CancellationToken cancellationToken);

    Task UpdateUserSettingsRuleSetAsync(string ruleSetId, int ruleSetVersion, CancellationToken cancellationToken);
}