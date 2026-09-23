// Signing manifest (design §11 / Task 11). Records which release
// assets must be Authenticode-signed before shipping. The verifier
// checks each entry by reading the WIN_CERTIFICATE blob from the
// PE file — any unmanaged exe/dll without one is "unsigned".

namespace InvoiceFlowAI.Application.Release;

public sealed record SignedAssetManifest(
    string SchemaVersion,
    string SigningSubject,
    IReadOnlyList<string> SignedRelativePaths);

public sealed record SignedAssetReport(
    bool AllSigned,
    IReadOnlyList<SignedAssetIssue> Issues);

public sealed record SignedAssetIssue(
    string RelativePath,
    string Code,
    string Message);