using InvoiceFlowAI.Contracts.Providers;

namespace InvoiceFlowAI.Application.Rules;

/// <summary>
/// Deterministic ordering for the provider-rule registry: <c>Priority
/// DESC, ProviderId ASC</c>. Used by the <c>recover-urls</c> and
/// <c>collect-candidates</c> nodes to pick the right rule for a candidate.
/// </summary>
public sealed class ProviderRuleOrderingService
{
    public IReadOnlyList<ProviderRuleDefinition> Order(ProviderRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Rules
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }
}