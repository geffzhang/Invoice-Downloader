// Persistence entity for the legacy-importer one-shot status. The first
// import writes this row inside the same UoW as the migrated accounts /
// settings; subsequent startups read this row and never re-open the
// legacy settings file.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class LegacyImportStateRow
{
    public string ImportId { get; set; } = "default";
    public int ImportVersion { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public DateTimeOffset ImportedAtUtc { get; set; }
    public bool SecretReentryRequired { get; set; }
}