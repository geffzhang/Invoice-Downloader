using System.Globalization;
using System.Text.Json;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Reports;

public sealed class EfReportRunDataStore : IReportRunDataSource, IReportPathStore
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.Ordinal)
    {
        "Completed", "PartialSuccess", "NeedsManualReview", "Cancelled", "Failed",
    };

    private readonly InvoiceFlowDbContext _context;

    public EfReportRunDataStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var run = await _context.Runs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.RunId == runId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null || !TerminalStates.Contains(run.State) || string.IsNullOrWhiteSpace(run.SummaryJson))
        {
            return null;
        }

        var summary = JsonSerializer.Deserialize<RunSummary>(run.SummaryJson);
        if (summary is null || !string.Equals(summary.RunId, runId, StringComparison.Ordinal))
        {
            return null;
        }

        var invoiceRows = await (
            from invoice in _context.Invoices.AsNoTracking()
            join processing in _context.DocumentProcessing.AsNoTracking()
                on new { invoice.DocumentId, invoice.ProcessingRevision }
                equals new { processing.DocumentId, processing.ProcessingRevision }
            where processing.RunId == runId
            orderby processing.Sequence, invoice.InvoiceId
            select new { Invoice = invoice, Processing = processing })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var archivePaths = await _context.ArchivedArtifacts.AsNoTracking()
            .Where(row => row.RunId == runId && row.State == "Committed")
            .Select(row => new { row.DocumentId, row.ProcessingRevision, row.RelativePath })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var archivePathByDocument = archivePaths
            .GroupBy(row => (row.DocumentId, row.ProcessingRevision))
            .ToDictionary(group => group.Key, group => group.First().RelativePath);

        var invoices = invoiceRows.Select(row =>
        {
            var relativePath = archivePathByDocument.GetValueOrDefault(
                (row.Invoice.DocumentId, row.Invoice.ProcessingRevision));
            return new ReportInvoiceRow(
                row.Invoice.InvoiceId,
                row.Invoice.InvoiceDate,
                row.Invoice.Purchaser,
                row.Invoice.Seller,
                ParseMoney(row.Invoice.Amount),
                ParseMoney(row.Invoice.TaxAmount),
                ParseMoney(row.Invoice.TotalAmount),
                row.Invoice.InvoiceNumber,
                row.Invoice.InvoiceCode,
                row.Invoice.DocumentType,
                row.Invoice.Category,
                row.Invoice.Confidence,
                relativePath is null ? row.Invoice.ArchiveState : "Committed")
            {
                CandidateStatus = row.Processing.Status,
                RelativePath = relativePath,
            };
        }).ToArray();

        var manualReviews = await _context.ManualReviewItems.AsNoTracking()
            .Where(row => row.RunId == runId)
            .OrderBy(row => row.ReviewId)
            .Select(row => new ReportManualReviewRow(
                row.ReviewId,
                row.DocumentId,
                row.ProcessingRevision,
                row.ReasonCode,
                row.State))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        manualReviews = manualReviews.Select(row => row with
        {
            RelativePath = archivePathByDocument.GetValueOrDefault((row.DocumentId, row.ProcessingRevision)),
        }).ToList();

        return new ReportRunData(
            run.RunId,
            summary.TerminalStatus.ToString(),
            summary.TerminalReasonCode,
            summary.CompletedAtUtc,
            invoices,
            manualReviews,
            new Dictionary<string, int>(summary.FailureReasonCounts, StringComparer.Ordinal),
            summary.ReportPath,
            summary.ReportContentHash,
            new ReportCandidateCounts(
                summary.ResolvedCount,
                summary.DuplicateCount,
                summary.RetainedCount,
                summary.ManualReviewCount,
                summary.UnresolvedCount,
                summary.CancelledCount,
                summary.QuotaExhaustedCount,
                summary.AuthFailedCount,
                summary.TimeoutCount))
        {
            OutputRoot = run.OutputRoot,
            DateFrom = run.DateFrom,
            DateToExclusive = run.DateToExclusive,
        };
    }

    public async Task SaveAsync(
        string runId,
        string relativePath,
        string contentHash,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateRelativePath(relativePath);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("Report path writes must use EfUnitOfWork.");
        }

        var run = await _context.Runs.SingleOrDefaultAsync(row => row.RunId == runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The report run was not found.");
        if (!TerminalStates.Contains(run.State) || string.IsNullOrWhiteSpace(run.SummaryJson))
        {
            throw new InvalidOperationException("The report run has no persisted terminal summary.");
        }

        var summary = JsonSerializer.Deserialize<RunSummary>(run.SummaryJson)
            ?? throw new InvalidOperationException("The persisted terminal summary is invalid.");
        run.SummaryJson = JsonSerializer.Serialize(summary with
        {
            ReportPath = relativePath,
            ReportContentHash = contentHash,
        });
    }

    private static decimal ParseMoney(string value) => decimal.Parse(
        value,
        NumberStyles.Number,
        CultureInfo.InvariantCulture);

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Report path must stay under the output root.", nameof(relativePath));
        }
    }
}
