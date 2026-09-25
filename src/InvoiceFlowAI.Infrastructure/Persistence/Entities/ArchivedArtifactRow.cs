// Persistence entity for an archived artifact. Two-phase commit:
//   1. Prepared  - temporary file written but hash not yet verified
//   2. Committed - file atomically renamed into the output tree, hash matches
//
// The recovery coordinator scans Prepared rows after a crash.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class ArchivedArtifactRow
{
    public string ArtifactId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public int ProcessingRevision { get; set; }
    public string Role { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string? TempFilePath { get; set; }
    public string? FinalFilePath { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? SourceFileName { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string State { get; set; } = "Prepared";
    public bool AlreadyExisted { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
}