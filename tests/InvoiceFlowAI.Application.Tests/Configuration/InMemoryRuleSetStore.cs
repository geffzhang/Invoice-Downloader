// Minimal in-memory IRulSetBootstrapStore implementation for the bootstrapper
// unit tests. The production EF Core-backed implementation lands in Task 4.

using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Contracts.Rules;

namespace InvoiceFlowAI.Application.Tests.Configuration;

internal sealed class InMemoryRuleSetStore : IRuleSetBootstrapStore
{
    public List<RuleSetDocument> SavedVersions { get; } = new();
    public List<UserSettingsStub> UserSettingsUpdates { get; } = new();

    private bool _seededTampered;

    public void SeedTamperedDefault()
    {
        _seededTampered = true;
    }

    public Task<RuleSetDocument?> FindCurrentAsync(string ruleSetId, CancellationToken cancellationToken)
    {
        if (SavedVersions.Count > 0)
        {
            return Task.FromResult<RuleSetDocument?>(SavedVersions[0]);
        }

        if (_seededTampered)
        {
            // A document with a fingerprint that *will not* match the
            // built-in canonical fingerprint — so the bootstrapper rejects it.
            return Task.FromResult<RuleSetDocument?>(new RuleSetDocument(
                SchemaVersion: "1.0",
                RuleSetId: ruleSetId,
                Rules: new[]
                {
                    new RuleRule("rule-tampered", 1, true,
                        new RuleRuleWhen(DocumentType: "FlightInvoice", SellerContains: null),
                        new RuleRuleThen("folder", "category", false, true)),
                }));
        }

        return Task.FromResult<RuleSetDocument?>(null);
    }

    public Task SaveAsync(RuleSetDocument document, CancellationToken cancellationToken)
    {
        SavedVersions.Add(document);
        return Task.CompletedTask;
    }

    public Task UpdateUserSettingsRuleSetAsync(string ruleSetId, int ruleSetVersion, CancellationToken cancellationToken)
    {
        UserSettingsUpdates.Add(new UserSettingsStub(ruleSetId, ruleSetVersion));
        return Task.CompletedTask;
    }
}

internal sealed record UserSettingsStub(string RuleSetId, int RuleSetVersion);