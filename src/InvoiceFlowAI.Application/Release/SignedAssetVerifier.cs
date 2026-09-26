// Signed-asset verifier (design §11 / Task 11). For each path in
// the SignedAssetManifest, looks up the file in publishRoot and
// checks that the PE carries an Authenticode certificate table.
// Without the table the asset is unsigned and must not ship.
//
// The check is conservative: any WIN_CERTIFICATE blob at the
// trailing end of the file counts as "signed". Decoding the cert
// chain is the OS loader's job — we only need to know whether the
// table exists.

using System.Security.Cryptography;

namespace InvoiceFlowAI.Application.Release;

public interface ISignedAssetVerifier
{
    SignedAssetReport Verify(SignedAssetManifest manifest, string publishRoot);
}

public sealed class SignedAssetVerifier : ISignedAssetVerifier
{
    public SignedAssetReport Verify(SignedAssetManifest manifest, string publishRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(publishRoot);

        var issues = new List<SignedAssetIssue>();
        foreach (var rel in manifest.SignedRelativePaths)
        {
            var full = Path.Combine(publishRoot, rel);
            if (!File.Exists(full))
            {
                issues.Add(new SignedAssetIssue(rel, "AssetMissing",
                    $"signed asset not found at {full}"));
                continue;
            }
            if (!HasCertificateTable(full))
            {
                issues.Add(new SignedAssetIssue(rel, "NotSigned",
                    "PE file has no WIN_CERTIFICATE table"));
            }
        }
        return new SignedAssetReport(issues.Count == 0, issues);
    }

    private static bool HasCertificateTable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) != 2 || header[0] != 'M' || header[1] != 'Z') return false;
            stream.Position = 0x3C;
            Span<byte> peOffsetBytes = stackalloc byte[4];
            if (stream.Read(peOffsetBytes) != 4) return false;
            var peOffset = BitConverter.ToInt32(peOffsetBytes);
            if (peOffset <= 0 || peOffset + 24 > stream.Length) return false;
            // COFF header is 24 bytes; data directories start at
            // peOffset + 24 + optionalHeaderSize. The certificate table
            // directory index is 4 (IMAGE_DIRECTORY_ENTRY_SECURITY).
            // We read the optional-header size field at peOffset + 20.
            stream.Position = peOffset + 20;
            Span<byte> optHdrSizeBytes = stackalloc byte[2];
            if (stream.Read(optHdrSizeBytes) != 2) return false;
            var optSize = BitConverter.ToUInt16(optHdrSizeBytes);
            // Skip COFF header (24) and full optional header to reach the
            // first data directory.
            var dirOffset = peOffset + 24 + optSize;
            if (dirOffset + 4 * 8 > stream.Length) return false;
            // Index 4 = certificate table (8-byte VirtualAddress + Size).
            stream.Position = dirOffset + 4 * 8;
            Span<byte> certDir = stackalloc byte[8];
            if (stream.Read(certDir) != 8) return false;
            var vaddr = BitConverter.ToUInt32(certDir[..4]);
            var size = BitConverter.ToUInt32(certDir.Slice(4, 4));
            return vaddr != 0 && size != 0;
        }
        catch
        {
            return false;
        }
    }
}