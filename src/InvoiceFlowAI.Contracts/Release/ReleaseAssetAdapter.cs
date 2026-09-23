namespace InvoiceFlowAI.Contracts.Release;

/// <summary>
/// Adapter asset entry that augments a wire <see cref="ReleaseManifestAsset"/>
/// with a <c>Kind</c> field. The verifier uses the adapter form so
/// the wire JSON (with no Kind) stays small, while the in-process
/// verifier can still route native binaries through PE checks.
/// </summary>
public sealed record ReleaseAssetAdapter(
    string RelativePath,
    long Length,
    string Sha256,
    string? Kind);