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
    ArchivePairPaths BuildPairRelativePaths(
        InvoiceDocument invoice,
        string invoiceSourceName,
        InvoiceDocument companion,
        string companionSourceName,
        string runId,
        int pairIndex,
        string family);
    string BuildReviewRelativePath(string runId, string documentId, string sourceName, string reasonCode);
    string BuildRetainedRelativePath(string runId, string documentId, string sourceName);
    string BuildContentHash(byte[] bytes);
}

public sealed record ArchivePairPaths(string InvoiceRelativePath, string CompanionRelativePath);

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

    public ArchivePairPaths BuildPairRelativePaths(
        InvoiceDocument invoice,
        string invoiceSourceName,
        InvoiceDocument companion,
        string companionSourceName,
        string runId,
        int pairIndex,
        string family)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentException.ThrowIfNullOrEmpty(invoiceSourceName);
        ArgumentException.ThrowIfNullOrEmpty(companionSourceName);
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pairIndex);
        ArgumentException.ThrowIfNullOrEmpty(family);

        var index = pairIndex.ToString("D2", CultureInfo.InvariantCulture);
        if (string.Equals(family, "ride", StringComparison.OrdinalIgnoreCase))
        {
            var date = invoice.InvoiceDate?.ToString("MMdd", CultureInfo.InvariantCulture) ?? string.Empty;
            var platform = IsGaodeRide(invoiceSourceName, companionSourceName) ? "高德" : "滴滴";
            var amount = FormatAmount(companion.Amount);
            return new ArchivePairPaths(
                $"archive/{runId}/{date}-{platform}-{index}-发票_{amount}元{SafeExtension(invoiceSourceName)}",
                $"archive/{runId}/{date}-{platform}-{index}-行程单_{amount}元{SafeExtension(companionSourceName)}");
        }

        if (string.Equals(family, "hotel", StringComparison.OrdinalIgnoreCase))
        {
            var date = invoice.InvoiceDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;
            var amount = FormatAmount(invoice.Amount);
            return new ArchivePairPaths(
                $"archive/{runId}/{date}-住宿-{index}-发票_{amount}元{SafeExtension(invoiceSourceName)}",
                $"archive/{runId}/{date}-住宿-{index}-水单_{amount}元{SafeExtension(companionSourceName)}");
        }

        throw new ArgumentOutOfRangeException(nameof(family), family, "Only ride and hotel pairs have paired archive names.");
    }

    public string BuildReviewRelativePath(string runId, string documentId, string sourceName, string reasonCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentException.ThrowIfNullOrEmpty(reasonCode);
        var document = SanitizeSegment(documentId);
        var reason = SanitizeSegment(reasonCode);
        var suffix = HashOf(documentId);
        return $"archive/{runId}/review/{document}_{suffix}_{reason}{SafeExtension(sourceName)}";
    }

    public string BuildRetainedRelativePath(string runId, string documentId, string sourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        var document = SanitizeSegment(documentId);
        var suffix = HashOf(documentId);
        return $"archive/{runId}/retained/{document}_{suffix}{SafeExtension(sourceName)}";
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

    private static string SanitizeSegment(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
        }
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    private static string SafeExtension(string sourceName)
    {
        var extension = Path.GetExtension(sourceName);
        if (extension.Length is < 2 or > 12 || extension.Skip(1).Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            return ".bin";
        }
        return extension.ToLowerInvariant();
    }

    private static string FormatAmount(decimal? value)
        => value?.ToString("F2", CultureInfo.InvariantCulture) ?? "unknown";

    private static bool IsGaodeRide(string invoiceSourceName, string companionSourceName)
    {
        var sourceNames = $"{invoiceSourceName} {companionSourceName}";
        return sourceNames.Contains("高德", StringComparison.Ordinal)
            || sourceNames.Contains("约车", StringComparison.Ordinal)
            || sourceNames.Contains("盛智", StringComparison.Ordinal);
    }

    private static string HashOf(object input)
    {
        var bytes = input is byte[] b ? b : Encoding.UTF8.GetBytes(input?.ToString() ?? "");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant()[..HashPrefixLength];
    }
}
