// WebView asset verifier. The Avalonia WebView loads index.html and
// its companion JS from a local directory under the publish root. The
// verifier runs at startup and refuses to mount the WebView if the
// expected assets are missing, the manifest is stale, or any required
// entry is absent. The same checks are used in the smoke-test path
// (build manifest in Task 11) and the runtime path so a missing file
// always produces the same WebAssetMissing code.

using System.Security.Cryptography;
using System.Text;

namespace InvoiceFlowAI.App.WebView;

public interface IWebViewAssetVerifier
{
    WebViewAssetReport Verify(WebViewAssetManifest manifest);
    string ComputeSha256(string path);
}

public sealed record WebViewAssetEntry(string RelativePath, string ExpectedSha256, long SizeBytes);

public sealed record WebViewAssetManifest(
    string RootDirectory,
    string IndexHtmlPath,
    IReadOnlyList<WebViewAssetEntry> Entries);

public sealed record WebViewAssetReport(
    bool AllPresent,
    IReadOnlyList<WebViewAssetEntry> Missing,
    IReadOnlyList<WebViewAssetMismatch> Mismatches);

public sealed record WebViewAssetMismatch(string RelativePath, string ExpectedSha256, string ActualSha256);

public sealed class WebViewAssetVerifier : IWebViewAssetVerifier
{
    public WebViewAssetReport Verify(WebViewAssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var missing = new List<WebViewAssetEntry>();
        var mismatches = new List<WebViewAssetMismatch>();

        var indexPath = Path.Combine(manifest.RootDirectory, manifest.IndexHtmlPath);
        if (!File.Exists(indexPath))
        {
            missing.Add(new WebViewAssetEntry(manifest.IndexHtmlPath, "", 0));
        }

        foreach (var entry in manifest.Entries)
        {
            var fullPath = Path.Combine(manifest.RootDirectory, entry.RelativePath);
            if (!File.Exists(fullPath))
            {
                missing.Add(entry);
                continue;
            }
            var actual = ComputeSha256(fullPath);
            if (!string.Equals(actual, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(new WebViewAssetMismatch(entry.RelativePath, entry.ExpectedSha256, actual));
            }
        }
        return new WebViewAssetReport(missing.Count == 0 && mismatches.Count == 0, missing, mismatches);
    }

    public string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
