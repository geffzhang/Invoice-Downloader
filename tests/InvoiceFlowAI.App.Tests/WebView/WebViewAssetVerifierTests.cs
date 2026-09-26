// Verifies WebViewAssetVerifier (Task 9):
//   * All entries present and matching → AllPresent=true
//   * Missing file → reported in Missing list
//   * Hash mismatch → reported in Mismatches list
//   * SHA-256 is computed in lowercase hex

using FluentAssertions;
using InvoiceFlowAI.App.WebView;
using Xunit;

namespace InvoiceFlowAI.App.Tests.WebView;

public sealed class WebViewAssetVerifierTests
{
    [Fact]
    public void Verify_returns_all_present_when_files_match()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
            File.WriteAllText(Path.Combine(dir, "app.js"), "console.log('hi')");
            var indexSha = Sha256OfFile(Path.Combine(dir, "index.html"));
            var jsSha = Sha256OfFile(Path.Combine(dir, "app.js"));
            var verifier = new WebViewAssetVerifier();
            var manifest = new WebViewAssetManifest(
                RootDirectory: dir,
                IndexHtmlPath: "index.html",
                Entries: new[]
                {
                    new WebViewAssetEntry("index.html", indexSha, 13),
                    new WebViewAssetEntry("app.js", jsSha, 18),
                });

            var report = verifier.Verify(manifest);

            report.AllPresent.Should().BeTrue();
            report.Missing.Should().BeEmpty();
            report.Mismatches.Should().BeEmpty();
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Verify_reports_missing_file()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
            var verifier = new WebViewAssetVerifier();
            var manifest = new WebViewAssetManifest(
                RootDirectory: dir,
                IndexHtmlPath: "index.html",
                Entries: new[] { new WebViewAssetEntry("missing.js", "abc", 0) });

            var report = verifier.Verify(manifest);

            report.AllPresent.Should().BeFalse();
            report.Missing.Should().ContainSingle().Which.RelativePath.Should().Be("missing.js");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Verify_reports_hash_mismatch()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
            File.WriteAllText(Path.Combine(dir, "app.js"), "original");
            var verifier = new WebViewAssetVerifier();
            var manifest = new WebViewAssetManifest(
                RootDirectory: dir,
                IndexHtmlPath: "index.html",
                Entries: new[] { new WebViewAssetEntry("app.js", "tampered_hash_value", 8) });

            var report = verifier.Verify(manifest);

            report.AllPresent.Should().BeFalse();
            report.Mismatches.Should().ContainSingle();
            report.Mismatches[0].RelativePath.Should().Be("app.js");
            report.Mismatches[0].ExpectedSha256.Should().Be("tampered_hash_value");
            report.Mismatches[0].ActualSha256.Should().NotBe("tampered_hash_value");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Verify_reports_missing_index_html()
    {
        var dir = NewTempDir();
        try
        {
            var verifier = new WebViewAssetVerifier();
            var manifest = new WebViewAssetManifest(
                RootDirectory: dir,
                IndexHtmlPath: "index.html",
                Entries: Array.Empty<WebViewAssetEntry>());

            var report = verifier.Verify(manifest);

            report.AllPresent.Should().BeFalse();
            report.Missing.Should().ContainSingle().Which.RelativePath.Should().Be("index.html");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void ComputeSha256_returns_lowercase_hex()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "file.txt");
            File.WriteAllText(path, "hi");
            var verifier = new WebViewAssetVerifier();

            var hash = verifier.ComputeSha256(path);

            hash.Should().MatchRegex("^[0-9a-f]{64}$");
        }
        finally { TryDelete(dir); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"invoiceflow-webview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
