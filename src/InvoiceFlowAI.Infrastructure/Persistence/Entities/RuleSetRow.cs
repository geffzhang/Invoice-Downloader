// Persistence entity for RuleSet versions. History is append-only; only
// the IsCurrent pointer changes. SourceJson stores the user-supplied JSON
// (rejected by validator before insert). NormalizedAstJson stores the
// canonical post-validation AST used for fingerprinting.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class RuleSetRow
{
    public string RuleSetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public int? ParentVersion { get; set; }
    public int? RollbackFromVersion { get; set; }
    public int? RollbackTargetVersion { get; set; }
    public string SourceJson { get; set; } = string.Empty;
    public string? NormalizedAstJson { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public string AstFingerprint { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}