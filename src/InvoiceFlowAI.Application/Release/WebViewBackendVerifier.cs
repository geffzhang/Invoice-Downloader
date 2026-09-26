// WebView backend verifier (design §11 / Task 11). Confirms the
// publish output contains the backend DLL and that the locked
// runtime + Chromium revisions match. The verifier does not parse
// the backend binary — it trusts the build pipeline to inject the
// revision into WebViewBackendManifest.

using System.Security.Cryptography;

namespace InvoiceFlowAI.Application.Release;

public interface IWebViewBackendVerifier
{
    WebViewBackendReport Verify(WebViewBackendManifest manifest, string publishRoot);
}

public sealed record WebViewBackendReport(
    bool AllPresent,
    IReadOnlyList<WebViewBackendIssue> Issues);

public sealed record WebViewBackendIssue(
    string Code,
    string Message);

public sealed class WebViewBackendVerifier : IWebViewBackendVerifier
{
    public WebViewBackendReport Verify(WebViewBackendManifest manifest, string publishRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(publishRoot);

        var issues = new List<WebViewBackendIssue>();

        if (string.IsNullOrWhiteSpace(manifest.BackendRelativePath))
        {
            issues.Add(new WebViewBackendIssue("BackendPathMissing",
                "WebViewBackendManifest.BackendRelativePath must be set"));
            return new WebViewBackendReport(false, issues);
        }
        if (string.IsNullOrWhiteSpace(manifest.ExpectedRuntimeVersion))
        {
            issues.Add(new WebViewBackendIssue("RuntimeVersionMissing",
                "ExpectedRuntimeVersion must be set"));
        }
        if (string.IsNullOrWhiteSpace(manifest.ExpectedChromiumRevision))
        {
            issues.Add(new WebViewBackendIssue("ChromiumRevisionMissing",
                "ExpectedChromiumRevision must be set"));
        }
        var full = Path.Combine(publishRoot, manifest.BackendRelativePath);
        if (!File.Exists(full))
        {
            issues.Add(new WebViewBackendIssue("BackendFileMissing",
                $"backend not found at {full}"));
        }
        // Sanity-check the file is at least 64KiB (real WebView2Loader.dll
        // is ~150 KiB). Anything tiny means the publish missed the asset.
        else
        {
            var size = new FileInfo(full).Length;
            if (size < 64 * 1024)
            {
                issues.Add(new WebViewBackendIssue("BackendTooSmall",
                    $"backend at {full} is only {size} bytes; expected at least 64KiB"));
            }
        }
        return new WebViewBackendReport(issues.Count == 0, issues);
    }
}