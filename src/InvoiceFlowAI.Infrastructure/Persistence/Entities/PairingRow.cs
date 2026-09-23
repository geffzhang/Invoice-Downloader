// Persistence entity for an invoice + companion pairing.

namespace InvoiceFlowAI.Infrastructure.Persistence.Entities;

public sealed class PairingRow
{
    public string PairingId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string InvoiceDocumentId { get; set; } = string.Empty;
    public int InvoiceProcessingRevision { get; set; }
    public string CompanionDocumentIdsJson { get; set; } = "[]";
    public string CompanionProcessingRevisionsJson { get; set; } = "[]";
    public string Score { get; set; } = "0";
    public string State { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}