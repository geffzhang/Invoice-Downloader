namespace InvoiceFlowAI.Contracts.Release;

public sealed record ReleaseManifestAsset(
    string RelativePath,
    long Length,
    string Sha256);