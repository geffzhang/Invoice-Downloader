// Invoice deduplication (design §3 / Task 10). Two invoices are
// duplicates when their (number, code, total) match. Within a single
// run this catches re-sent attachments and re-parsed same-source
// files. Across runs, the archive ContentHash lookup is the canonical
// authority — but the run-level pass here keeps the candidate list
// short before archive writes.

using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Deduplication;

public interface IInvoiceDeduplicator
{
    DeduplicationResult Deduplicate(IReadOnlyList<InvoiceDocument> invoices);
    IReadOnlyList<InvoiceDocument> Coalesce(IReadOnlyList<InvoiceDocument> invoices);
}

public sealed record DeduplicationResult(
    IReadOnlyList<InvoiceDocument> Unique,
    IReadOnlyList<DeduplicationGroup> DuplicateGroups);

public sealed record DeduplicationGroup(
    string Fingerprint,
    IReadOnlyList<InvoiceDocument> Members);

public sealed class InvoiceDeduplicator : IInvoiceDeduplicator
{
    public IReadOnlyList<InvoiceDocument> Coalesce(IReadOnlyList<InvoiceDocument> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        return Deduplicate(invoices).Unique;
    }

    public DeduplicationResult Deduplicate(IReadOnlyList<InvoiceDocument> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        var groups = new Dictionary<string, List<InvoiceDocument>>(StringComparer.Ordinal);
        foreach (var inv in invoices)
        {
            var fingerprint = ComputeFingerprint(inv);
            if (!groups.TryGetValue(fingerprint, out var list))
            {
                list = new List<InvoiceDocument>();
                groups[fingerprint] = list;
            }
            list.Add(inv);
        }
        var unique = new List<InvoiceDocument>(groups.Count);
        var dups = new List<DeduplicationGroup>();
        foreach (var (fp, list) in groups)
        {
            unique.Add(list[0]);
            if (list.Count > 1)
            {
                dups.Add(new DeduplicationGroup(fp, list));
            }
        }
        return new DeduplicationResult(unique, dups);
    }

    private static string ComputeFingerprint(InvoiceDocument invoice)
    {
        var number = invoice.InvoiceNumber ?? "";
        var code = invoice.InvoiceCode ?? "";
        var total = (invoice.TotalAmount ?? 0m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var purchaser = invoice.Purchaser ?? "";
        var seller = invoice.Seller ?? "";
        return string.Join("|", number, code, total, purchaser, seller);
    }
}
