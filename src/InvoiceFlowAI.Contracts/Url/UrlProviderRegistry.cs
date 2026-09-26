namespace InvoiceFlowAI.Contracts.Url;

public sealed record UrlProviderRegistry(
    string SchemaVersion,
    string RegistryFingerprint,
    IReadOnlyList<UrlProviderDefinition> Providers);