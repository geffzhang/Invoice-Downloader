namespace InvoiceFlowAI.Contracts.Release;

public sealed record ReleaseManifestPlaywright(
    string PackageVersion,
    string ChromiumRevision);