using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Extraction;

public sealed class InvoiceNormalizer
{
    public InvoiceDocument Normalize(InvoiceDocument document, DocumentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(identity.Value))
        {
            throw new ArgumentException("Document identity must be non-empty.", nameof(identity));
        }

        var total = document.TotalAmount
            ?? (document.Amount.HasValue ? document.Amount.Value + (document.TaxAmount ?? 0m) : null);

        return document with
        {
            DocumentId = identity.Value,
            Identity = identity,
            Purchaser = NormalizeText(document.Purchaser),
            Seller = NormalizeText(document.Seller),
            InvoiceCode = NormalizeText(document.InvoiceCode),
            InvoiceNumber = NormalizeText(document.InvoiceNumber),
            Category = NormalizeText(document.Category),
            SourceFileName = NormalizeText(document.SourceFileName),
            ContentHash = NormalizeText(document.ContentHash),
            TotalAmount = total,
            Confidence = Math.Clamp(document.Confidence, 0m, 1m),
        };
    }

    private static string NormalizeText(string? value) => value?.Trim() ?? string.Empty;
}