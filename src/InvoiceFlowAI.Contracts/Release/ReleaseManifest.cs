namespace InvoiceFlowAI.Contracts.Release;

/// <summary>
/// Release manifest. <c>SchemaVersion</c> is numeric on the wire per
/// <c>release-manifest.example.json</c>. The manifest is produced by CI, not
/// at design time — local test fixtures carry fixture-only placeholder
/// values, real values must come from the signed CI artefact.
/// </summary>
public sealed record ReleaseManifest(
    int SchemaVersion,
    string ApplicationVersion,
    string GitRevision,
    string RuntimeIdentifier,
    string Configuration,
    bool Signed,
    ReleaseManifestWebView2 WebView2,
    ReleaseManifestPlaywright Playwright,
    IReadOnlyList<ReleaseManifestAsset> Assets,
    string ModelManifestPath,
    string BrowserManifestPath,
    string LicenseManifestPath,
    string ManifestSha256);