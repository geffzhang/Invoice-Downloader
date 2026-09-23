namespace InvoiceFlowAI.Contracts.Providers;

/// <summary>
/// Provider-rule entry as published by <c>providers/registry.v1.json</c>.
/// Each rule is keyed by <c>ProviderFamily</c> + <c>EvidenceCode</c>; the
/// registry is frozen at boot and its <c>RegistryFingerprint</c> participates
/// in the run's <c>ConfigurationFingerprint</c>.
/// </summary>
public sealed record ProviderRuleDefinition(
    string ProviderId,
    string ProviderFamily,
    int Priority,
    string EvidenceCode,
    string SpecialParserId);