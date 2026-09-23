// ClosedXML-based report exporter. Produces the three-sheet schema fixed
// by design §7 / §11:
//   Sheet "Summary"        — run metadata + aggregate counts
//   Sheet "Invoices"       — one row per resolved invoice (failed candidates
//                            also appear as a row with DocumentType="Failed"
//                            so the UI cannot claim success silently)
//   Sheet "Manual Reviews" — one row per ManualReviewItems row
// Numbers are formatted with invariant culture so the report is stable
// across locales. The content hash is the SHA-256 of the produced bytes;
// callers persist it so re-exports can be deduplicated.

using System.Globalization;
using System.Security.Cryptography;
using ClosedXML.Excel;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Reports;

namespace InvoiceFlowAI.Infrastructure.Reports;

public sealed class ClosedXmlReportExporter : IReportExporter
{
    private readonly IArchiveFileSystem _fileSystem;
    private readonly Func<string, string> _resolveAbsolutePath;

    public ClosedXmlReportExporter(IArchiveFileSystem fileSystem, Func<string, string> resolveAbsolutePath)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _resolveAbsolutePath = resolveAbsolutePath ?? throw new ArgumentNullException(nameof(resolveAbsolutePath));
    }

    public async Task<ReportExportOutcome> ExportAsync(ReportExportWorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var absolutePath = _resolveAbsolutePath(workItem.RelativePath);
        var existed = await _fileSystem.FileExistsAsync(absolutePath, cancellationToken).ConfigureAwait(false);

        var bytes = Render(workItem);
        await WriteAsync(absolutePath, bytes, cancellationToken).ConfigureAwait(false);
        // ClosedXML embeds non-deterministic metadata (timestamps, GUIDs) in
        // the .xlsx zip, so hashing the raw bytes would produce a different
        // digest for identical inputs. Hash the canonical projection of the
        // work item instead — same data → same hash.
        var hash = ComputeCanonicalHash(workItem);

        return new ReportExportOutcome(workItem.RelativePath, hash, AlreadyExisted: existed);
    }

    private static byte[] Render(ReportExportWorkItem workItem)
    {
        using var workbook = new XLWorkbook();

        var summary = workbook.Worksheets.Add("Summary");
        summary.Cell("A1").Value = "RunId";
        summary.Cell("B1").Value = workItem.RunId;
        summary.Cell("A2").Value = "Status";
        summary.Cell("B2").Value = workItem.Summary.Status;
        summary.Cell("A3").Value = "ReasonCode";
        summary.Cell("B3").Value = workItem.Summary.ReasonCode;
        summary.Cell("A4").Value = "CompletedAtUtc";
        summary.Cell("B4").Value = workItem.Summary.CompletedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        summary.Cell("A5").Value = "Resolved";
        summary.Cell("B5").Value = workItem.Summary.ResolvedCount;
        summary.Cell("A6").Value = "ManualReview";
        summary.Cell("B6").Value = workItem.Summary.ManualReviewCount;
        summary.Cell("A7").Value = "TemplateVersion";
        summary.Cell("B7").Value = workItem.TemplateVersion;

        var invoices = workbook.Worksheets.Add("Invoices");
        invoices.Cell("A1").Value = "InvoiceId";
        invoices.Cell("B1").Value = "InvoiceDate";
        invoices.Cell("C1").Value = "Purchaser";
        invoices.Cell("D1").Value = "Seller";
        invoices.Cell("E1").Value = "Amount";
        invoices.Cell("F1").Value = "TaxAmount";
        invoices.Cell("G1").Value = "TotalAmount";
        invoices.Cell("H1").Value = "InvoiceNumber";
        invoices.Cell("I1").Value = "InvoiceCode";
        invoices.Cell("J1").Value = "DocumentType";
        invoices.Cell("K1").Value = "Category";
        invoices.Cell("L1").Value = "Confidence";
        invoices.Cell("M1").Value = "ArchiveState";

        var row = 2;
        foreach (var inv in workItem.Invoices)
        {
            invoices.Cell(row, 1).Value = inv.InvoiceId;
            invoices.Cell(row, 2).Value = inv.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            invoices.Cell(row, 3).Value = inv.Purchaser;
            invoices.Cell(row, 4).Value = inv.Seller;
            invoices.Cell(row, 5).Value = inv.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            invoices.Cell(row, 6).Value = inv.TaxAmount.ToString("0.00", CultureInfo.InvariantCulture);
            invoices.Cell(row, 7).Value = inv.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture);
            invoices.Cell(row, 8).Value = inv.InvoiceNumber ?? "";
            invoices.Cell(row, 9).Value = inv.InvoiceCode ?? "";
            invoices.Cell(row, 10).Value = inv.DocumentType;
            invoices.Cell(row, 11).Value = inv.Category ?? "";
            invoices.Cell(row, 12).Value = inv.Confidence;
            invoices.Cell(row, 13).Value = inv.ArchiveState;
            row++;
        }

        var reviews = workbook.Worksheets.Add("Manual Reviews");
        reviews.Cell("A1").Value = "ReviewId";
        reviews.Cell("B1").Value = "DocumentId";
        reviews.Cell("C1").Value = "ProcessingRevision";
        reviews.Cell("D1").Value = "ReasonCode";
        reviews.Cell("E1").Value = "State";

        var rrow = 2;
        foreach (var r in workItem.ManualReviews)
        {
            reviews.Cell(rrow, 1).Value = r.ReviewId;
            reviews.Cell(rrow, 2).Value = r.DocumentId;
            reviews.Cell(rrow, 3).Value = r.ProcessingRevision;
            reviews.Cell(rrow, 4).Value = r.ReasonCode;
            reviews.Cell(rrow, 5).Value = r.State;
            rrow++;
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private async Task WriteAsync(string absolutePath, byte[] bytes, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeCanonicalHash(ReportExportWorkItem workItem)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("run=").Append(workItem.RunId).Append('|');
        sb.Append("tpl=").Append(workItem.TemplateVersion).Append('|');
        sb.Append("s.status=").Append(workItem.Summary.Status).Append('|');
        sb.Append("s.reason=").Append(workItem.Summary.ReasonCode).Append('|');
        sb.Append("s.completed=").Append(workItem.Summary.CompletedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append('|');
        sb.Append("s.resolved=").Append(workItem.Summary.ResolvedCount).Append('|');
        sb.Append("s.mr=").Append(workItem.Summary.ManualReviewCount).Append('|');
        sb.Append("inv[");
        foreach (var inv in workItem.Invoices)
        {
            sb.Append(inv.InvoiceId).Append(',')
              .Append(inv.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(inv.Purchaser).Append(',')
              .Append(inv.Seller).Append(',')
              .Append(inv.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(inv.TaxAmount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(inv.TotalAmount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(inv.InvoiceNumber ?? string.Empty).Append(',')
              .Append(inv.InvoiceCode ?? string.Empty).Append(',')
              .Append(inv.DocumentType).Append(',')
              .Append(inv.Category ?? string.Empty).Append(',')
              .Append(inv.Confidence).Append(',')
              .Append(inv.ArchiveState).Append(';');
        }
        sb.Append("]mr[");
        foreach (var mr in workItem.ManualReviews)
        {
            sb.Append(mr.ReviewId).Append(',')
              .Append(mr.DocumentId).Append(',')
              .Append(mr.ProcessingRevision).Append(',')
              .Append(mr.ReasonCode).Append(',')
              .Append(mr.State).Append(';');
        }
        sb.Append("]fail[");
        foreach (var f in workItem.Failures)
        {
            sb.Append(f.ReasonCode).Append(',').Append(f.Count).Append(';');
        }
        sb.Append(']');
        return Sha256Hex(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }
}