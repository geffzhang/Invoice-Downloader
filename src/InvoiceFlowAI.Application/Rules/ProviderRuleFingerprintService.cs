using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Contracts.Providers;

namespace InvoiceFlowAI.Application.Rules;

/// <summary>
/// Computes the provider-registry fingerprint. Two registries produce the
/// same fingerprint iff their schema, ordering and rule shape match.
/// </summary>
public sealed class ProviderRuleFingerprintService
{
    private readonly ProviderRuleOrderingService _order = new();
    private readonly ConfigurationFingerprintService _fingerprint = new();

    public string Compute(ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var ordered = _order.Order(registry);
        var projected = new
        {
            registry.SchemaVersion,
            Rules = ordered.Select(r => new
            {
                r.ProviderId,
                r.ProviderFamily,
                r.Priority,
                r.EvidenceCode,
                r.SpecialParserId,
            }).ToArray(),
        };
        return _fingerprint.ComputeFromObject(projected);
    }
}