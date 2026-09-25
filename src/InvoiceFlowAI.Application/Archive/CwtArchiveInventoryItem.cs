namespace InvoiceFlowAI.Application.Archive;

public enum CwtArchiveInventoryItemKind
{
    ArchivedArtifact,
    LegacyFile,
}

public sealed record CwtArchiveInventoryItem(
    string InventoryId,
    string? ArtifactId,
    string? DocumentId,
    int? ProcessingRevision,
    string SourceFileName,
    string RelativePath,
    string AbsolutePath,
    string ContentHash,
    CwtArchiveInventoryItemKind Kind,
    ArchiveArtifactState? ArtifactState,
    LegacyArchiveInventoryState? LegacyState,
    string? ArtifactRunId = null);