namespace InvoiceFlowAI.Application.Archive;

public enum LegacyArchiveInventoryState
{
    Discovered,
    Prepared,
    Review,
    RecoveryRequired,
}

public sealed record LegacyArchiveInventorySnapshot(
    string InventoryId,
    string RootKey,
    string OriginalRelativePath,
    string CurrentRelativePath,
    string SourceFileName,
    string ContentHash,
    LegacyArchiveInventoryState State,
    string? ReviewRunId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);