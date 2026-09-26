// Persistence entity for the cross-run stable document identity. A document
// is unique per (SourceKind, SourceLocator); when this row already exists,
// processing attaches a new DocumentProcessing revision instead of inserting
// a duplicate Documents row.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class DocumentSourceRow
{
    public string DocumentId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string? SourceMessageUid { get; set; }
    public string? SourceFileName { get; set; }
    public string? SourceLocator { get; set; }
    public string? ProviderGroupKey { get; set; }
    public string? ContentHash { get; set; }
    public string? MimeType { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}