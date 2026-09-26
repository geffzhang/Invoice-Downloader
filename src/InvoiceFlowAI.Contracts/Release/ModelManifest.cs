namespace InvoiceFlowAI.Contracts.Release;

/// <summary>
/// Model manifest (design §11 / Task 11). Each entry is a model
/// asset shipped alongside the application (DeepSeek API contract
/// stub, OCR weights, etc.). The <c>Kind</c> field routes the
/// asset to the right consumer; <c>Revision</c> is the upstream
/// vendor revision that produced the file.
/// </summary>
public sealed record ModelManifest(
    int SchemaVersion,
    string Vendor,
    IReadOnlyList<ModelManifestAsset> Assets,
    string ManifestSha256);

public sealed record ModelManifestAsset(
    string RelativePath,
    long Length,
    string Sha256,
    string Kind,
    string? Revision,
    string? Notes);