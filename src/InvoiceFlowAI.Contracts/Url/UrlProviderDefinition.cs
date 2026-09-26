namespace InvoiceFlowAI.Contracts.Url;

/// <summary>
/// URL provider entry. <c>CapabilitiesVersion</c> is the version of the
/// URL recovery capability contract this provider implements; it
/// participates in the <c>UrlProviderRegistry.RegistryFingerprint</c>.
/// </summary>
public sealed record UrlProviderDefinition(
    string ProviderId,
    int Priority,
    bool SupportsDirectHttp,
    bool SupportsBrowserFallback,
    string CapabilitiesVersion);