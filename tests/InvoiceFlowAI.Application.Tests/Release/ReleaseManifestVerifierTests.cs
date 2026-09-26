// Verifies ReleaseManifestVerifier (Task 11):
//   * All assets present + correct size + correct hash → AllPresent=true
//   * Missing file → AssetMissing
//   * Wrong size → SizeMismatch
//   * Wrong hash → HashMismatch
//   * Path traversal rejected
//   * Absolute path rejected
//   * Empty path / null byte rejected
//   * PE architecture is checked when kind=native/exe/dll (adapter)
//   * ModelManifest verification reuses the asset-level rules
//   * ModelManifest null is ModelManifestMissing
//   * Adapter routes the kind so PE checks are skipped for non-native entries

using FluentAssertions;
using InvoiceFlowAI.Application.Release;
using InvoiceFlowAI.Contracts.Release;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Release;

public sealed class ReleaseManifestVerifierTests
{
    private readonly IReleaseManifestVerifier _verifier = new ReleaseManifestVerifier();

    [Fact]
    public void All_assets_present_and_matching_returns_clean_report()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(Path.Combine(dir, "asset.bin"), bytes);
            var manifest = NewManifest(new ReleaseManifestAsset("asset.bin", bytes.LongLength, Sha256Hex(bytes)));

            var report = _verifier.Verify(manifest, dir);

            report.AllPresent.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Missing_file_reports_AssetMissing()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = NewManifest(new ReleaseManifestAsset("missing.exe", 0, "0".PadRight(64, '0')));

            var report = _verifier.Verify(manifest, dir);

            report.AllPresent.Should().BeFalse();
            report.Issues.Should().Contain(i => i.Code == "AssetMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Wrong_size_reports_SizeMismatch()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(dir, "app.exe"), bytes);
            var manifest = NewManifest(new ReleaseManifestAsset("app.exe", 99, Sha256Hex(bytes)));

            var report = _verifier.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "SizeMismatch");
            report.Issues.Should().NotContain(i => i.Code == "HashMismatch");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Wrong_hash_reports_HashMismatch()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(dir, "data.txt"), bytes);
            var manifest = NewManifest(new ReleaseManifestAsset("data.txt", 3, "0".PadRight(64, '0')));

            var report = _verifier.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "HashMismatch");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Path_traversal_is_rejected()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = NewManifest(new ReleaseManifestAsset("..\\..\\Windows\\System32\\cmd.exe", 0, "0".PadRight(64, '0')));

            var report = _verifier.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "PathTraversalDetected");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Absolute_path_is_rejected()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = NewManifest(new ReleaseManifestAsset("C:\\Windows\\System32\\cmd.exe", 0, "0".PadRight(64, '0')));

            var report = _verifier.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "PathAbsoluteRejected");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Empty_path_is_rejected()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = NewManifest(new ReleaseManifestAsset("", 0, "0".PadRight(64, '0')));

            var report = _verifier.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "PathInvalid");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Model_manifest_verification_reuses_asset_rules()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = new ModelManifest(1, "DeepSeek", new List<ModelManifestAsset>
            {
                new("missing.bin", 0, "0".PadRight(64, '0'), "model", "rev-1", null),
            }, "fp");

            var report = _verifier.VerifyModel(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "AssetMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Null_model_manifest_reports_ModelManifestMissing()
    {
        var dir = NewTempDir();
        try
        {
            var report = _verifier.VerifyModel(null, dir);

            report.Issues.Should().ContainSingle();
            report.Issues[0].Code.Should().Be("ModelManifestMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Empty_model_manifest_reports_missing_required_chinese_v6_tiny_assets()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = new ModelManifest(1, "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny", [], "fixture");

            var report = _verifier.VerifyModel(manifest, dir);

            report.AllPresent.Should().BeFalse();
            report.Issues.Should().ContainSingle(issue =>
                issue.RelativePath == "ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll"
                && issue.Code == "ModelAssetMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Complete_chinese_v6_tiny_manifest_verifies_synthetic_assets()
    {
        var dir = NewTempDir();
        try
        {
            var assets = WriteSyntheticModelBundle(dir, typeof(ReleaseManifestVerifierTests).Assembly.Location);
            var manifest = new ModelManifest(1, "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny", assets, "fixture");

            var report = _verifier.VerifyModel(manifest, dir);

            report.AllPresent.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Model_manifest_rejects_wrong_size_and_hash_for_model_bundle()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "ocr", "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(path, bytes);
            var assets = WriteSyntheticModelBundle(dir, typeof(ReleaseManifestVerifierTests).Assembly.Location)
                .Select(asset => asset.RelativePath == "ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll"
                    ? asset with { Length = 99, Sha256 = "0".PadRight(64, '0') }
                    : asset)
                .ToArray();
            var manifest = new ModelManifest(1, "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny", assets, "fixture");

            var report = _verifier.VerifyModel(manifest, dir);

            report.Issues.Should().Contain(issue => issue.RelativePath == "ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll" && issue.Code == "SizeMismatch");
            report.Issues.Should().Contain(issue => issue.RelativePath == "ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll" && issue.Code == "HashMismatch");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Model_manifest_rejects_non_managed_or_wrong_architecture_bundle()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "ocr", "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = CreatePeImage(machine: 0x8664);
            File.WriteAllBytes(path, bytes);
            var manifest = new ModelManifest(1, "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny", [
                new ModelManifestAsset("ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll", bytes.LongLength,
                    Sha256Hex(bytes), "model-bundle", "1.0.0", null),
            ], "fixture");

            var report = _verifier.VerifyModel(manifest, dir);

            report.Issues.Should().Contain(issue => issue.Code == "ModelArchitectureMismatch");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Adapter_routes_native_kind_to_PE_check()
    {
        var dir = NewTempDir();
        try
        {
            // Non-PE bytes for a "native" asset → must report PeSignatureMissing.
            var bytes = new byte[] { 0, 0, 0, 0 };
            File.WriteAllBytes(Path.Combine(dir, "not-pe.dll"), bytes);
            var manifest = NewManifest(new ReleaseManifestAsset("not-pe.dll", 4, Sha256Hex(bytes)));
            var adapters = new List<ReleaseAssetAdapter> { new("not-pe.dll", 4, Sha256Hex(bytes), "dll") };

            var report = _verifier.VerifyFromAdapter(manifest, dir, adapters);

            report.Issues.Should().Contain(i => i.Code == "PeSignatureMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Adapter_data_kind_skips_PE_check()
    {
        var dir = NewTempDir();
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), bytes);
            var manifest = NewManifest(new ReleaseManifestAsset("data.bin", 3, Sha256Hex(bytes)));
            var adapters = new List<ReleaseAssetAdapter> { new("data.bin", 3, Sha256Hex(bytes), "data") };

            var report = _verifier.VerifyFromAdapter(manifest, dir, adapters);

            report.AllPresent.Should().BeTrue();
        }
        finally { TryDelete(dir); }
    }

    private static ReleaseManifest NewManifest(params ReleaseManifestAsset[] assets) =>
        new(1, "2026.09.23.0", "fixture", "win-x64", "Release", false,
            new ReleaseManifestWebView2("1.0.4255-prerelease", "fixture"),
            new ReleaseManifestPlaywright("1.62.0", "fixture"),
            assets,
            "models/manifest.json",
            "browsers/manifest.json",
            "licenses/THIRD-PARTY-NOTICES.txt",
            "fixture-manifest-sha256");

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"invoiceflow-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static ModelManifestAsset[] WriteSyntheticModelBundle(string root, string assemblyPath)
    {
        const string relativePath = "ocr/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.dll";
        var bytes = File.ReadAllBytes(assemblyPath);
        var outputPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, bytes);
        return [new ModelManifestAsset(relativePath, bytes.LongLength, Sha256Hex(bytes), "model-bundle", "1.0.0", null)];
    }

    private static byte[] CreatePeImage(ushort machine)
    {
        var bytes = new byte[0x100];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}