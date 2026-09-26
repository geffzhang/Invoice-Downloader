// WebView backend manifest (design §11 / Task 11). Describes the
// Avalonia.Controls.WebView runtime that the publish pipeline ships:
// the backend file name (WebView2Loader.dll or libcef.dll), the
// installed runtime version, and the Chromium revision. The
// verifier checks the file is present in the publish output and
// that the runtime matches the locked version.

namespace InvoiceFlowAI.Application.Release;

public sealed record WebViewBackendManifest(
    string SchemaVersion,
    string BackendKind,
    string BackendRelativePath,
    string ExpectedRuntimeVersion,
    string ExpectedChromiumRevision);