// Release manifest verifier (design §11 / Task 11). Consumes the
// wire-shape DTOs from InvoiceFlowAI.Contracts.Release and walks
// every asset entry to check size + SHA-256 + PE architecture.
// Any entry whose RelativePath resolves outside the configured
// publish root is rejected with PathTraversalDetected; absolute
// paths are rejected outright. The verifier never reads outside
// the root directory.

using System.Security.Cryptography;
using ContractsRelease = InvoiceFlowAI.Contracts.Release;

namespace InvoiceFlowAI.Application.Release;

public interface IReleaseManifestVerifier
{
    ReleaseVerificationReport Verify(ContractsRelease.ReleaseManifest manifest, string publishRoot);
    ReleaseVerificationReport VerifyModel(ContractsRelease.ModelManifest? manifest, string modelsRoot);
    ReleaseVerificationReport VerifyFromAdapter(ContractsRelease.ReleaseManifest manifest, string publishRoot,
        IReadOnlyList<ContractsRelease.ReleaseAssetAdapter> adapters);
}

public sealed record ReleaseVerificationReport(
    bool AllPresent,
    IReadOnlyList<ReleaseAssetIssue> Issues);

public sealed record ReleaseAssetIssue(
    string RelativePath,
    string Code,
    string Message);

public sealed class ReleaseManifestVerifier : IReleaseManifestVerifier
{
    public ReleaseVerificationReport Verify(ContractsRelease.ReleaseManifest manifest, string publishRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(publishRoot);

        var issues = new List<ReleaseAssetIssue>();
        // The wire-format ReleaseManifest doesn't carry Kind, so we don't
        // run PE-arch checks here. Use VerifyFromAdapter for kind-aware
        // verification.
        foreach (var asset in manifest.Assets)
        {
            CheckOne(publishRoot, asset.RelativePath, asset.Length, asset.Sha256, "data", issues);
        }
        return new ReleaseVerificationReport(issues.Count == 0, issues);
    }

    public ReleaseVerificationReport VerifyModel(ContractsRelease.ModelManifest? manifest, string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelsRoot);
        if (manifest is null)
        {
            return new ReleaseVerificationReport(false, new[]
            {
                new ReleaseAssetIssue("(model-manifest)", "ModelManifestMissing",
                    "model manifest not provided"),
            });
        }
        var issues = new List<ReleaseAssetIssue>();
        foreach (var asset in manifest.Assets)
        {
            CheckOne(modelsRoot, asset.RelativePath, asset.Length, asset.Sha256, asset.Kind, issues);
        }
        return new ReleaseVerificationReport(issues.Count == 0, issues);
    }

    public ReleaseVerificationReport VerifyFromAdapter(ContractsRelease.ReleaseManifest manifest, string publishRoot,
        IReadOnlyList<ContractsRelease.ReleaseAssetAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(publishRoot);

        var issues = new List<ReleaseAssetIssue>();
        foreach (var adapter in adapters)
        {
            CheckOne(publishRoot, adapter.RelativePath, adapter.Length, adapter.Sha256,
                adapter.Kind ?? "data", issues);
        }
        return new ReleaseVerificationReport(issues.Count == 0, issues);
    }

    private static void CheckOne(string root, string relativePath, long expectedSize, string expectedSha256,
        string kind, List<ReleaseAssetIssue> issues)
    {
        if (!TryResolve(root, relativePath, out var fullPath, out var pathIssue))
        {
            issues.Add(new ReleaseAssetIssue(relativePath, pathIssue!, "path rejected"));
            return;
        }
        if (!File.Exists(fullPath))
        {
            issues.Add(new ReleaseAssetIssue(relativePath, "AssetMissing", $"file not found: {fullPath}"));
            return;
        }
        var info = new FileInfo(fullPath);
        if (info.Length != expectedSize)
        {
            issues.Add(new ReleaseAssetIssue(relativePath, "SizeMismatch",
                $"expected {expectedSize} bytes, got {info.Length}"));
        }
        using var stream = File.OpenRead(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ReleaseAssetIssue(relativePath, "HashMismatch",
                $"expected {expectedSha256}, got {actualHash}"));
        }
        if (kind is "native" or "exe" or "dll")
        {
            CheckPeArchitecture(fullPath, relativePath, issues);
        }
    }

    private static bool TryResolve(string root, string relativePath, out string fullPath, out string? issue)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Contains('\0'))
        {
            fullPath = "";
            issue = "PathInvalid";
            return false;
        }
        if (Path.IsPathRooted(relativePath))
        {
            fullPath = "";
            issue = "PathAbsoluteRejected";
            return false;
        }
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = combined;
            issue = "PathTraversalDetected";
            return false;
        }
        fullPath = combined;
        issue = null;
        return true;
    }

    private static void CheckPeArchitecture(string path, string relativePath, List<ReleaseAssetIssue> issues)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) != 2 || header[0] != 'M' || header[1] != 'Z')
            {
                issues.Add(new ReleaseAssetIssue(relativePath, "PeSignatureMissing",
                    "file is not a PE (MZ) image"));
                return;
            }
            stream.Position = 0x3C;
            Span<byte> peOffsetBytes = stackalloc byte[4];
            if (stream.Read(peOffsetBytes) != 4) return;
            var peOffset = BitConverter.ToInt32(peOffsetBytes);
            if (peOffset <= 0 || peOffset + 6 > stream.Length) return;
            stream.Position = peOffset;
            Span<byte> peSig = stackalloc byte[4];
            if (stream.Read(peSig) != 4 || peSig[0] != 'P' || peSig[1] != 'E' || peSig[2] != 0 || peSig[3] != 0) return;
            Span<byte> machine = stackalloc byte[2];
            if (stream.Read(machine) != 2) return;
            var arch = BitConverter.ToUInt16(machine);
            if (arch != 0x8664 && arch != 0xAA64 && arch != 0x14C)
            {
                issues.Add(new ReleaseAssetIssue(relativePath, "PeArchitectureUnknown",
                    $"PE machine type 0x{arch:X4} not recognised"));
            }
        }
        catch (Exception ex)
        {
            issues.Add(new ReleaseAssetIssue(relativePath, "PeReadFailed", ex.Message));
        }
    }
}