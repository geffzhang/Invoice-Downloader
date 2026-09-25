// Windows release acceptance tests (Task 12). Exercises the full
// release-verification chain (manifest + backend + signing) end
// to end on a synthetic publish output. The tests run on any
// platform — they don't require a real Windows installer — so the
// "clean Windows 11 x64 hardware install" gate stays as the final
// external check (see docs/superpowers/specs/external-validation.md).
//
// What these tests cover:
//   * Round-trip: build a fake publish dir, emit a manifest+adapter,
//     all three verifiers return clean.
//   * Drift: tamper with the manifest (missing file, wrong hash,
//     wrong size) — the corresponding Issue.Code fires.
//   * Unsigned exe is detected by SignedAssetVerifier.
//   * Path traversal in any verifier's manifest is rejected.

using FluentAssertions;
using InvoiceFlowAI.Application.Release;
using InvoiceFlowAI.Contracts.Release;
using Xunit;

namespace InvoiceFlowAI.App.Tests.EndToEnd;

public sealed class WindowsReleaseTests
{
    private readonly IReleaseManifestVerifier _release = new ReleaseManifestVerifier();
    private readonly IWebViewBackendVerifier _backend = new WebViewBackendVerifier();
    private readonly ISignedAssetVerifier _signed = new SignedAssetVerifier();

    [Fact]
    public void Clean_fake_publish_passes_release_verification()
    {
        var dir = NewTempPublishDir();
        try
        {
            WritePeShapedFile(Path.Combine(dir, "InvoiceFlowAI.exe"), 2048);
            WritePeShapedFile(Path.Combine(dir, "WebView2Loader.dll"), 200 * 1024);

            var appExeBytes = File.ReadAllBytes(Path.Combine(dir, "InvoiceFlowAI.exe"));
            var backendBytes = File.ReadAllBytes(Path.Combine(dir, "WebView2Loader.dll"));

            var manifest = new ReleaseManifest(
                1, "2026.09.23.0", "git-revision", "win-x64", "Release", false,
                new ReleaseManifestWebView2("1.0.4255-prerelease", "120.0.6099.71"),
                new ReleaseManifestPlaywright("1.62.0", "120.0.6099.71"),
                new[]
                {
                    new ReleaseManifestAsset("InvoiceFlowAI.exe", appExeBytes.LongLength,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(appExeBytes)).ToLowerInvariant()),
                    new ReleaseManifestAsset("WebView2Loader.dll", backendBytes.LongLength,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(backendBytes)).ToLowerInvariant()),
                },
                "models/manifest.json", "browsers/manifest.json", "licenses/THIRD-PARTY-NOTICES.txt",
                "fp");

            var adapters = new List<ReleaseAssetAdapter>
            {
                new("InvoiceFlowAI.exe", appExeBytes.LongLength,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(appExeBytes)).ToLowerInvariant(),
                    "exe"),
                new("WebView2Loader.dll", backendBytes.LongLength,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(backendBytes)).ToLowerInvariant(),
                    "dll"),
            };

            var releaseReport = _release.VerifyFromAdapter(manifest, dir, adapters);
            releaseReport.AllPresent.Should().BeTrue(releaseReport.Issues.Count == 0
                ? "all assets match"
                : "issues: " + string.Join(",", releaseReport.Issues.Select(i => i.Code)));

            var backendReport = _backend.Verify(
                new WebViewBackendManifest("1.0", "WebView2", "WebView2Loader.dll",
                    ExpectedRuntimeVersion: "120.0.6099.71",
                    ExpectedChromiumRevision: "120.0.6099.71"),
                dir);
            backendReport.AllPresent.Should().BeTrue();

            // SignedAssetVerifier requires real Authenticode. The synthetic
            // PE we wrote has no WIN_CERTIFICATE table, so this verifier
            // will report NotSigned. That's the *expected* behaviour for
            // an unsigned build; the production MSI is signed in CI.
            var signingReport = _signed.Verify(
                new SignedAssetManifest("1.0", "CN=InvoiceFlowAI",
                    new[] { "InvoiceFlowAI.exe", "WebView2Loader.dll" }),
                dir);
            signingReport.AllSigned.Should().BeFalse();
            signingReport.Issues.Should().AllSatisfy(i => i.Code.Should().Be("NotSigned"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Tampered_manifest_drives_corresponding_Issue_Code()
    {
        var dir = NewTempPublishDir();
        try
        {
            WritePeShapedFile(Path.Combine(dir, "InvoiceFlowAI.exe"), 1024);
            var realBytes = File.ReadAllBytes(Path.Combine(dir, "InvoiceFlowAI.exe"));
            var realHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(realBytes)).ToLowerInvariant();

            var manifest = new ReleaseManifest(
                1, "2026.09.23.0", "git", "win-x64", "Release", false,
                new ReleaseManifestWebView2("p", "r"),
                new ReleaseManifestPlaywright("p", "r"),
                new[]
                {
                    // Wrong size, wrong hash, missing file — all in one manifest.
                    new ReleaseManifestAsset("InvoiceFlowAI.exe", 9999, "0".PadRight(64, '0')),
                    new ReleaseManifestAsset("missing.dll", 0, realHash),
                },
                "m", "b", "l", "fp");

            var report = _release.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.RelativePath == "InvoiceFlowAI.exe" && i.Code == "SizeMismatch");
            report.Issues.Should().Contain(i => i.RelativePath == "InvoiceFlowAI.exe" && i.Code == "HashMismatch");
            report.Issues.Should().Contain(i => i.RelativePath == "missing.dll" && i.Code == "AssetMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Path_traversal_in_manifest_is_rejected_even_with_adapter()
    {
        var dir = NewTempPublishDir();
        try
        {
            var manifest = new ReleaseManifest(
                1, "v", "git", "win-x64", "Release", false,
                new ReleaseManifestWebView2("p", "r"),
                new ReleaseManifestPlaywright("p", "r"),
                Array.Empty<ReleaseManifestAsset>(),
                "m", "b", "l", "fp");
            var adapters = new List<ReleaseAssetAdapter>
            {
                new(@"..\..\Windows\System32\cmd.exe", 0, "0".PadRight(64, '0'), "exe"),
            };

            var report = _release.VerifyFromAdapter(manifest, dir, adapters);

            report.Issues.Should().ContainSingle();
            report.Issues[0].Code.Should().Be("PathTraversalDetected");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void WebView_backend_missing_blocks_release()
    {
        var dir = NewTempPublishDir();
        try
        {
            var report = _backend.Verify(
                new WebViewBackendManifest("1.0", "WebView2", "WebView2Loader.dll", "r", "r"),
                dir);

            report.AllPresent.Should().BeFalse();
            report.Issues.Should().Contain(i => i.Code == "BackendFileMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void No_python_runtime_release_audit_rejects_interpreter_and_requires_dotnet_app_and_worker()
    {
        var dir = NewTempPublishDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "InvoiceFlowAI.exe"), "synthetic app");
            File.WriteAllText(Path.Combine(dir, "InvoiceFlowAI.UrlRecovery.Worker.exe"), "synthetic worker");

            var clean = NoPythonRuntimeReleaseAudit.Verify(dir);
            clean.Issues.Should().BeEmpty();

            var pythonRoot = Path.Combine(dir, ".venv", "Scripts");
            Directory.CreateDirectory(pythonRoot);
            File.WriteAllText(Path.Combine(pythonRoot, "python.exe"), "synthetic interpreter");
            File.WriteAllText(Path.Combine(dir, "legacy-runtime.py"), "synthetic source");
            File.WriteAllText(Path.Combine(dir, "python312.dll"), "synthetic runtime");
            File.WriteAllText(Path.Combine(dir, "libpython3.12.dll"), "synthetic runtime");
            File.WriteAllText(Path.Combine(dir, "python312._pth"), "synthetic runtime config");
            File.WriteAllText(Path.Combine(dir, "Python.Runtime.dll"), "synthetic interop");
            File.WriteAllText(Path.Combine(dir, "PyInstaller-loader.exe"), "synthetic bootloader");

            var report = NoPythonRuntimeReleaseAudit.Verify(dir);

            report.Issues.Should().HaveCount(8);
            report.Issues.Should().OnlyContain(issue => issue.Code == "PythonRuntimeArtifact");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Configured_publish_tree_passes_no_python_runtime_release_audit()
    {
        var publishRoot = Environment.GetEnvironmentVariable("INVOICEFLOWAI_PUBLISH_ROOT");
        if (string.IsNullOrWhiteSpace(publishRoot)) return;

        var report = NoPythonRuntimeReleaseAudit.Verify(publishRoot);

        report.Issues.Should().BeEmpty(string.Join(", ", report.Issues.Select(issue =>
            $"{issue.Code}:{issue.RelativePath}")));
    }

    private static string NewTempPublishDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"invoiceflow-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePeShapedFile(string path, int size)
    {
        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write((byte)'M'); writer.Write((byte)'Z');
        writer.Seek(60, SeekOrigin.Begin);
        writer.Write((uint)0x40);
        writer.Seek(0x40, SeekOrigin.Begin);
        writer.Write((byte)'P'); writer.Write((byte)'E'); writer.Write((ushort)0);
        writer.Write((ushort)0x8664); writer.Write((ushort)0);
        writer.Write((uint)0); writer.Write((uint)0); writer.Write((uint)0);
        writer.Write((ushort)0); writer.Write((ushort)0);
        var header = ms.ToArray();
        var padded = new byte[Math.Max(size, header.Length)];
        Array.Copy(header, padded, header.Length);
        File.WriteAllBytes(path, padded);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}