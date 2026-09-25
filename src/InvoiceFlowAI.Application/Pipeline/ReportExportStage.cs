using System.Globalization;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Pipeline;

public sealed class ReportExportStage : IReportExportStage
{
    private const string TemplateVersion = "1.0.0";
    private readonly IReportExporter _exporter;
    private readonly ITerminalDecisionService _terminalDecisionService;
    private readonly TimeProvider _timeProvider;

    public ReportExportStage(
        IReportExporter exporter,
        ITerminalDecisionService terminalDecisionService,
        TimeProvider timeProvider)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _terminalDecisionService = terminalDecisionService ?? throw new ArgumentNullException(nameof(terminalDecisionService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<RunSummary> ExecuteAsync(PipelineItem<ArchiveBatch> input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(input.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.OutputRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var batch = input.Payload ?? throw new ArgumentNullException(nameof(input.Payload));
        var decision = _terminalDecisionService.Decide(
            input.RunId,
            batch.Results,
            runFailure: null,
            cancellationRequested: false,
            allCandidatesArrived: true,
            batch.Failures,
            _timeProvider.GetUtcNow());
        var artifactStates = batch.Artifacts
            .GroupBy(item => item.DocumentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.FirstOrDefault(item => item.State == ArchiveArtifactState.Committed) ?? group.First(),
                StringComparer.Ordinal);
        var invoices = batch.Results
            .Where(result => result.Invoice is not null || IsFailedCandidate(result.Status))
            .Select(result => ToInvoiceRow(result, artifactStates))
            .ToArray();
        var manualReviews = batch.Results
            .Where(result => result.Status == CandidateStatus.ManualReview)
            .Select(result =>
            {
                var documentId = result.Candidate.DocumentId.Value;
                return new ReportManualReviewRow(
                    $"review-{documentId}-{result.Candidate.ProcessingRevision}",
                    documentId,
                    result.Candidate.ProcessingRevision,
                    result.Failure?.ReasonCode ?? "MANUAL_REVIEW",
                    "Open")
                {
                    RelativePath = artifactStates.TryGetValue(documentId, out var artifact)
                        ? artifact.RelativePath
                        : null,
                };
            })
            .ToArray();
        var relativePath = $"reports/{input.RunId}/report.xlsx";
        var outcome = await _exporter.ExportAsync(new ReportExportWorkItem(
            input.RunId,
            relativePath,
            invoices,
            manualReviews,
            decision.Summary.FailureReasonCounts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ReportFailureRow(pair.Key, pair.Value))
                .ToArray(),
            ToReportSummary(decision.Summary),
            TemplateVersion)
        {
            OutputRoot = input.OutputRoot,
        }, cancellationToken).ConfigureAwait(false);

        return decision.Summary with
        {
            ReportPath = outcome.RelativePath,
            ReportContentHash = outcome.ContentHash,
        };
    }

    private static ReportInvoiceRow ToInvoiceRow(
        CandidateProcessResult result,
        IReadOnlyDictionary<string, ArchiveArtifactOutcome> artifactStates)
    {
        var documentId = result.Candidate.DocumentId.Value;
        var archiveState = artifactStates.TryGetValue(documentId, out var artifact)
            ? artifact.State.ToString()
            : "NotArchived";
        if (result.Invoice is not { } invoice)
        {
            return new ReportInvoiceRow(
                documentId,
                null,
                string.Empty,
                string.Empty,
                0m,
                0m,
                0m,
                null,
                null,
                "Failed",
                result.Failure?.ReasonCode,
                string.Empty,
                archiveState)
            {
                CandidateStatus = result.Status.ToString(),
                RelativePath = artifact?.RelativePath,
            };
        }

        return new ReportInvoiceRow(
            documentId,
            invoice.InvoiceDate ?? DateOnly.MinValue,
            invoice.Purchaser,
            invoice.Seller,
            invoice.Amount ?? 0m,
            invoice.TaxAmount ?? 0m,
            invoice.TotalAmount ?? 0m,
            invoice.InvoiceNumber,
            invoice.InvoiceCode,
            invoice.DocumentType.ToString(),
            invoice.Category,
            invoice.Confidence.ToString(CultureInfo.InvariantCulture),
            archiveState)
        {
            CandidateStatus = result.Status.ToString(),
            RelativePath = artifact?.RelativePath,
        };
    }

    private static bool IsFailedCandidate(CandidateStatus status) =>
        status is CandidateStatus.Unresolved
            or CandidateStatus.Cancelled
            or CandidateStatus.QuotaExhausted
            or CandidateStatus.AuthFailed
            or CandidateStatus.Timeout;

    private static ReportSummaryRow ToReportSummary(RunSummary summary) => new(
        summary.RunId,
        summary.TerminalStatus.ToString(),
        summary.TerminalReasonCode,
        summary.ResolvedCount,
        summary.DuplicateCount,
        summary.RetainedCount,
        summary.ManualReviewCount,
        summary.UnresolvedCount,
        summary.CancelledCount,
        summary.QuotaExhaustedCount,
        summary.AuthFailedCount,
        summary.TimeoutCount,
        summary.CompletedAtUtc);
}