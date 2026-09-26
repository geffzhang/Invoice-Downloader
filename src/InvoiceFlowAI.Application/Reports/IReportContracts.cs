// Application-layer report interfaces. Split out from the implementation
// so the test project can mock them without dragging in the full service.

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Reports;

namespace InvoiceFlowAI.Application.Reports;

public interface IReportApplicationService
{
    Task<ReportExportRpcResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken);
    Task<ReportOpenToken> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken);
    Task<ReportOpenResolution> ResolveTokenAsync(string tokenId, CancellationToken cancellationToken);
}

public sealed record ReportOpenResolution(
    string TokenId,
    string RunId,
    string RelativePath,
    string ContentHash,
    bool Valid,
    string? ReasonCode);

public interface IReportExporter
{
    Task<ReportExportOutcome> ExportAsync(ReportExportWorkItem workItem, CancellationToken cancellationToken);
}

public sealed record ReportExportWorkItem(
    string RunId,
    string RelativePath,
    IReadOnlyList<ReportInvoiceRow> Invoices,
    IReadOnlyList<ReportManualReviewRow> ManualReviews,
    IReadOnlyList<ReportFailureRow> Failures,
    ReportSummaryRow Summary,
    string TemplateVersion)
{
    public string? OutputRoot { get; init; }
}

public sealed record ReportInvoiceRow(
    string InvoiceId,
    DateOnly? InvoiceDate,
    string Purchaser,
    string Seller,
    decimal Amount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? InvoiceNumber,
    string? InvoiceCode,
    string DocumentType,
    string? Category,
    string Confidence,
    string ArchiveState)
{
    public string CandidateStatus { get; init; } = string.Empty;
    public string? RelativePath { get; init; }
}

public sealed record ReportManualReviewRow(
    string ReviewId,
    string DocumentId,
    int ProcessingRevision,
    string ReasonCode,
    string State)
{
    public string? RelativePath { get; init; }
}

public sealed record ReportFailureRow(
    string ReasonCode,
    int Count);

public sealed record ReportSummaryRow(
    string RunId,
    string Status,
    string ReasonCode,
    int ResolvedCount,
    int DuplicateCount,
    int RetainedCount,
    int ManualReviewCount,
    int UnresolvedCount,
    int CancelledCount,
    int QuotaExhaustedCount,
    int AuthFailedCount,
    int TimeoutCount,
    DateTimeOffset CompletedAtUtc);

public sealed record ReportExportOutcome(
    string RelativePath,
    string ContentHash,
    bool AlreadyExisted);

public interface IReportRunDataSource
{
    Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken);
}

public sealed record ReportRunData(
    string RunId,
    string Status,
    string ReasonCode,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ReportInvoiceRow> Invoices,
    IReadOnlyList<ReportManualReviewRow> ManualReviews,
    IReadOnlyDictionary<string, int> FailureReasonCounts,
    string? ExistingRelativePath,
    string? ExistingContentHash,
    ReportCandidateCounts CandidateCounts)
{
    public string? OutputRoot { get; init; }
    public DateOnly DateFrom { get; init; }
    public DateOnly DateToExclusive { get; init; }
}

public sealed record ReportCandidateCounts(
    int ResolvedCount,
    int DuplicateCount,
    int RetainedCount,
    int ManualReviewCount,
    int UnresolvedCount,
    int CancelledCount,
    int QuotaExhaustedCount,
    int AuthFailedCount,
    int TimeoutCount);

public interface IReportPathStore
{
    Task SaveAsync(string runId, string relativePath, string contentHash, IUnitOfWork transaction, CancellationToken cancellationToken);
    Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken);
}