using InvoiceFlowAI.Application.Rules;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rules;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Ensures the canonical default RuleSet (version 1) is present in the
/// store. Idempotent — re-running is a no-op. If a tampered version with a
/// different fingerprint is already present, throws with
/// <c>RULESET_REVISION_CONFLICT</c> rather than silently overwriting.
/// </summary>
public sealed class RuleSetBootstrapper
{
    private readonly IRuleSetBootstrapStore _store;
    private readonly RuleSetValidator _validator;
    private readonly ConfigurationFingerprintService _fingerprint = new();

    public RuleSetBootstrapper(IRuleSetBootstrapStore store, RuleSetValidator validator)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<RuleSetBootstrapResult> EnsureDefaultAsync(CancellationToken cancellationToken)
    {
        const string ruleSetId = "default";
        const int version = 1;

        var existing = await _store.FindCurrentAsync(ruleSetId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _validator.Validate(existing);
            var canonicalFingerprint = ComputeCanonicalFingerprint();
            var existingFingerprint = _fingerprint.ComputeFromObject(existing);
            if (!string.Equals(existingFingerprint, canonicalFingerprint, StringComparison.Ordinal))
            {
                throw new RuleSetBootstrapException(
                    RpcErrorCodes.RulesetRevisionConflict,
                    $"Existing '{ruleSetId}' RuleSet version {version} has been tampered with; refusing to overwrite.");
            }

            return new RuleSetBootstrapResult(existing.RuleSetId, version, Inserted: false);
        }

        var document = BuiltInRuleSets.Default();
        _validator.Validate(document);
        await _store.SaveAsync(document, cancellationToken).ConfigureAwait(false);
        await _store.UpdateUserSettingsRuleSetAsync(document.RuleSetId, version, cancellationToken).ConfigureAwait(false);
        return new RuleSetBootstrapResult(document.RuleSetId, version, Inserted: true);
    }

    private string ComputeCanonicalFingerprint()
    {
        var canonical = BuiltInRuleSets.Default();
        return _fingerprint.ComputeFromObject(canonical);
    }
}

public sealed record RuleSetBootstrapResult(string RuleSetId, int Version, bool Inserted);

/// <summary>
/// The canonical, hard-coded default RuleSet. Lives next to the validator
/// so any schema/version change forces a single co-located edit and the
/// bootstrap fingerprint is recomputed in lock-step with the canonical
/// definition.
/// </summary>
public static class BuiltInRuleSets
{
    public static RuleSetDocument Default() => new(
        SchemaVersion: "1.0",
        RuleSetId: "default",
        Rules: new[]
        {
            new RuleRule(
                RuleId: "archive-flight",
                Priority: 100,
                Enabled: true,
                When: new RuleRuleWhen(DocumentType: "FlightInvoice", SellerContains: "air"),
                Then: new RuleRuleThen(
                    ArchiveFolder: "transport/flight",
                    Category: "transport",
                    RequireManualReview: false,
                    AllowCrossMessagePairing: true)),
        });
}