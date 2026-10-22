// Verifies SignedAssetVerifier:
//   * Signed manifest with unsigned PE → NotSigned
//   * Signed manifest with missing file → AssetMissing
//   * Manifest with no assets → clean report

using FluentAssertions;
using InvoiceFlowAI.Application.Release;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Release;

public sealed class SignedAssetVerifierTests
{
    private readonly ISignedAssetVerifier _signer = new SignedAssetVerifier();

    [Fact]
    public void Unsigned_PE_reports_NotSigned()
    {
        var dir = NewTempDir();
        try
        {
            // Build a minimal PE that has an MZ + PE header but no
            // certificate table. Enough to satisfy "is it a PE" but
            // not enough to be a real executable.
            var pe = BuildMinimalPe();
            var path = Path.Combine(dir, "app.exe");
            File.WriteAllBytes(path, pe);
            var manifest = new SignedAssetManifest(
                "1.0", "CN=Acme", new[] { "app.exe" });

            var report = _signer.Verify(manifest, dir);

            report.AllSigned.Should().BeFalse();
            report.Issues.Should().Contain(i => i.Code == "NotSigned");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Missing_signed_asset_reports_AssetMissing()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = new SignedAssetManifest(
                "1.0", "CN=Acme", new[] { "missing.exe" });

            var report = _signer.Verify(manifest, dir);

            report.Issues.Should().Contain(i => i.Code == "AssetMissing");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Empty_manifest_is_clean()
    {
        var dir = NewTempDir();
        try
        {
            var manifest = new SignedAssetManifest("1.0", "CN=Acme", Array.Empty<string>());

            var report = _signer.Verify(manifest, dir);

            report.AllSigned.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally { TryDelete(dir); }
    }

    private static byte[] BuildMinimalPe()
    {
        // DOS header (64 bytes) pointing at PE offset 0x40,
        // then PE signature + COFF header + an empty optional header,
        // then 16 data directories all zeroed.
        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        // DOS header
        writer.Write((byte)'M'); writer.Write((byte)'Z');
        writer.Seek(60, SeekOrigin.Begin); // e_lfanew
        writer.Write((uint)0x40);
        // PE signature
        writer.Seek(0x40, SeekOrigin.Begin);
        writer.Write((byte)'P'); writer.Write((byte)'E'); writer.Write((ushort)0);
        // COFF header (20 bytes)
        writer.Write((ushort)0x8664); // AMD64
        writer.Write((ushort)0);      // NumberOfSections
        writer.Write((uint)0);        // TimeDateStamp
        writer.Write((uint)0);        // PointerToSymbolTable
        writer.Write((uint)0);        // NumberOfSymbols
        writer.Write((ushort)0);      // SizeOfOptionalHeader — 0 means no data dirs
        writer.Write((ushort)0);      // Characteristics
        // Total = 0x40 + 4 + 20 + 0 = 84 bytes, no opt header → no cert table.
        return ms.ToArray();
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"invoiceflow-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
