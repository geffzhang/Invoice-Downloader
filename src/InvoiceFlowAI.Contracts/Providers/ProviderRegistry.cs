namespace InvoiceFlowAI.Contracts.Providers;

/// <summary>
/// Frozen provider registry. Shape mirrors <c>providers/registry.v1.json</c>
/// — collection is named <c>rules</c> on the wire, not <c>providers</c>.
/// </summary>
public sealed record ProviderRegistry(
    string SchemaVersion,
    string RegistryFingerprint,
    IReadOnlyList<ProviderRuleDefinition> Rules);