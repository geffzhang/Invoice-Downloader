namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class LegacyArchiveInventoryRow
{
    public string InventoryId { get; set; } = string.Empty;
    public string RootKey { get; set; } = string.Empty;
    public string OriginalRelativePath { get; set; } = string.Empty;
    public string CurrentRelativePath { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string State { get; set; } = "Discovered";
    public string? ReviewRunId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}