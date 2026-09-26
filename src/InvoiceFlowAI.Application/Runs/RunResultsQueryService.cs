using System.Globalization;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Runs;

public sealed class RunResultsQueryService
{
    private readonly IReportRunDataSource _dataSource;
    private readonly IDesktopRunService _runs;

    public RunResultsQueryService(IReportRunDataSource dataSource, IDesktopRunService runs)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    public async Task<RunResultsSnapshot> GetAsync(string? runId, CancellationToken cancellationToken)
    {
        var requestedRunId = runId;
        if (string.IsNullOrWhiteSpace(requestedRunId))
        {
            requestedRunId = (await _runs.GetContextAsync(cancellationToken).ConfigureAwait(false)).RunId;
        }
        if (string.IsNullOrWhiteSpace(requestedRunId))
        {
            throw new DesktopRunException(RpcErrorCodes.RunNotFound, "Run was not found.");
        }

        var data = await _dataSource.LoadAsync(requestedRunId, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            throw new DesktopRunException(RpcErrorCodes.RunNotFound, "Run results are not available.");
        }

        var counts = data.CandidateCounts;
        var processingErrorCount = counts.UnresolvedCount
            + counts.CancelledCount
            + counts.QuotaExhaustedCount
            + counts.AuthFailedCount
            + counts.TimeoutCount;
        var pendingCount = counts.ManualReviewCount + counts.RetainedCount + processingErrorCount;
        var resultBreakdown = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["resolved"] = counts.ResolvedCount,
            ["duplicate"] = counts.DuplicateCount,
            ["retained"] = counts.RetainedCount,
            ["manual_review"] = counts.ManualReviewCount,
            ["unresolved"] = counts.UnresolvedCount,
            ["cancelled"] = counts.CancelledCount,
            ["quota_exhausted"] = counts.QuotaExhaustedCount,
            ["auth_failed"] = counts.AuthFailedCount,
            ["timeout"] = counts.TimeoutCount,
        };
        var summary = new Dictionary<string, int>(resultBreakdown, StringComparer.Ordinal)
        {
            ["success_count"] = counts.ResolvedCount,
            ["error_count"] = pendingCount,
            ["manual_check_count"] = counts.ManualReviewCount,
            ["retention_count"] = counts.RetainedCount,
            ["processing_error_count"] = processingErrorCount,
            ["total_count"] = resultBreakdown.Values.Sum(),
        };
        var resolvedInvoices = data.Invoices
            .Where(row => string.Equals(row.CandidateStatus, "Resolved", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(row.RelativePath))
            .ToArray();
        var categories = resolvedInvoices
            .GroupBy(row => string.IsNullOrWhiteSpace(row.Category) ? "other" : row.Category!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var reasonCounts = new Dictionary<string, int>(data.FailureReasonCounts, StringComparer.Ordinal);
        foreach (var reviewGroup in data.ManualReviews.GroupBy(row => row.ReasonCode, StringComparer.Ordinal))
        {
            reasonCounts[reviewGroup.Key] = Math.Max(reasonCounts.GetValueOrDefault(reviewGroup.Key), reviewGroup.Count());
        }
        var groupedErrors = reasonCounts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var reviews = data.ManualReviews
                    .Where(row => string.Equals(row.ReasonCode, pair.Key, StringComparison.Ordinal))
                    .Select(row => new RunErrorInvoiceResult(
                        null,
                        row.ReasonCode,
                        row.State,
                        null,
                        row.RelativePath))
                    .ToArray();
                return new RunGroupedErrorResult(pair.Key, pair.Key, pair.Value, reviews);
            })
            .ToArray();
        var dateRange = FormatDateRange(data.DateFrom, data.DateToExclusive);
        var manualCheckPath = counts.ManualReviewCount > 0 && !string.IsNullOrWhiteSpace(data.OutputRoot)
            ? Path.Combine(data.OutputRoot, "archive", data.RunId, "review")
            : null;

        return new RunResultsSnapshot(
            categories,
            resolvedInvoices.Select(row => new RunInvoiceResult(
                row.InvoiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                row.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                row.Seller,
                row.Category,
                row.RelativePath)).ToArray(),
            groupedErrors.SelectMany(group => group.Items).ToArray(),
            groupedErrors,
            manualCheckPath,
            data.OutputRoot,
            summary,
            BuildIdentity: null,
            RawDateRange: dateRange,
            ImapQueryRange: dateRange,
            resultBreakdown,
            reasonCounts,
            counts.QuotaExhaustedCount > 0,
            counts.QuotaExhaustedCount > 0 ? "Some candidates exhausted provider quota." : null,
            data.ExistingRelativePath);
    }

    private static string? FormatDateRange(DateOnly start, DateOnly endExclusive)
    {
        if (start == default || endExclusive <= start) return null;
        var endInclusive = endExclusive.AddDays(-1);
        return $"{start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} - {endInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }
}