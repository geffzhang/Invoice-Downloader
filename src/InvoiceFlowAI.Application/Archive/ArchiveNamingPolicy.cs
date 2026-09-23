// Archive naming policy (design §3 / Task 10). Each archived invoice
// gets a deterministic, human-readable file name that combines the
// document type, the canonical date, the invoice number, and a short
// content hash. The same inputs always produce the same name, so
// re-running a run does not create duplicate files on disk.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Archive;

public interface IArchiveNamingPolicy
{
    string BuildRelativePath(InvoiceDocument invoice, string runId);
    string BuildContentHash(byte[] bytes);
}

public sealed class ArchiveNamingPolicy : IArchiveNamingPolicy
{
    public const int HashPrefixLength = 8;

    public string BuildRelativePath(InvoiceDocument invoice, string runId)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var date = invoice.InvoiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated";
        var number = Sanitize(invoice.InvoiceNumber ?? "no-number");
        var docType = invoice.DocumentType.ToString();
        var prefix = !string.IsNullOrEmpty(invoice.ContentHash) && invoice.ContentHash.Length >= HashPrefixLength
            ? invoice.ContentHash[..HashPrefixLength]
            : HashOf(invoice);
        return $"archive/{runId}/{date}_{docType}_{number}_{prefix}.pdf";
    }

    public string BuildContentHash(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return HashOf(bytes);
    }

    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') sb.Append(ch);
        }
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    private static string HashOf(object input)
    {
        var bytes = input is byte[] b ? b : Encoding.UTF8.GetBytes(input?.ToString() ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..HashPrefixLength];
    }
}
