// Avalonia.Controls.WebView host. The host owns the WebView2 control,
// wires it to the WebViewRpcBridge, and verifies the local assets at
// startup. The runtime path here is intentionally minimal — the
// production wiring of the WebView2 control surface is environment-
// specific (it depends on Edge runtime, MSIX vs unpackaged, etc.) and
// is exercised in Task 11's end-to-end smoke test. The host here is
// the testable seam: it consumes IWebViewAssetVerifier and
// IWebViewRpcBridge so the smoke test can run without a real
// WebView2 instance.

using InvoiceFlowAI.App.Rpc;

namespace InvoiceFlowAI.App.WebView;

public interface IAvaloniaWebViewHost
{
    string IndexUrl { get; }
    WebViewAssetReport AssetReport { get; }
    bool Ready { get; }
    void Mount();
    void Unmount();
}

public sealed class AvaloniaWebViewHost : IAvaloniaWebViewHost
{
    private readonly WebViewAssetManifest _manifest;
    private readonly IWebViewAssetVerifier _verifier;
    private readonly IWebViewRpcBridge _bridge;

    public AvaloniaWebViewHost(
        WebViewAssetManifest manifest,
        IWebViewAssetVerifier verifier,
        IWebViewRpcBridge bridge)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public string IndexUrl => $"file:///{_manifest.RootDirectory.Replace('\\', '/').TrimEnd('/')}/{_manifest.IndexHtmlPath.TrimStart('/')}";

    public WebViewAssetReport AssetReport => _verifier.Verify(_manifest);

    public bool Ready => AssetReport.AllPresent;

    public void Mount()
    {
        if (!Ready)
        {
            throw new InvalidOperationException(
                $"WebView assets are not ready. Missing: {string.Join(", ", AssetReport.Missing.Select(m => m.RelativePath))}");
        }
        _bridge.Start();
    }

    public void Unmount()
    {
        _bridge.Stop();
    }
}
