// Default implementation of IReportApplicationService.
//
//   * Export: builds a ReportExportWorkItem from the run data source, hands
//     it to the IReportExporter, then persists the relative path + content
//     hash on the run row via IReportPathStore. A second call with the same
//     run re-emits if the underlying data changed but returns the existing
//     content hash if not.
//
//   * Open: Issues a one-time ReportOpenTokenRecord bound to the run +
//     relative path + content hash. The token must be consumed via
//     ResolveTokenAsync before its TTL elapses; consumed tokens cannot be
//     replayed and expired tokens cannot be resolved.
//
// The service never accepts an absolute path. RelativePath is relative to
// the application's configured output root (resolution happens at the
// boundary, never inside this service).

using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;

namespace InvoiceFlowAI.Application.Reports;

public sealed class ReportApplicationService : IReportApplicationService
{
    private const string CurrentTemplateVersion = "1.0.0";
    private static readonly TimeSpan DefaultTokenTtl = TimeSpan.FromMinutes(5);

    private readonly IReportExporter _exporter;
    private readonly IReportPathStore _pathStore;
    private readonly IReportRunDataSource _dataSource;
    private readonly IReportOpenTokenStore _tokenStore;
    private readonly IUnitOfWorkFactory _uowFactory;
    private readonly TimeSpan _tokenTtl;

    public ReportApplicationService(
        IReportExporter exporter,
        IReportPathStore pathStore,
        IReportRunDataSource dataSource,
        IReportOpenTokenStore tokenStore,
        IUnitOfWorkFactory uowFactory,
        TimeSpan? tokenTtl = null)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _pathStore = pathStore ?? throw new ArgumentNullException(nameof(pathStore));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _uowFactory = uowFactory ?? throw new ArgumentNullException(nameof(uowFactory));
        _tokenTtl = tokenTtl ?? DefaultTokenTtl;
    }

    public async Task<ReportExportRpcResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.RunId);

        var data = await _dataSource.LoadAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            throw new InvalidOperationException($"Run '{request.RunId}' has no reportable data.");
        }

        var relativePath = $"reports/{request.RunId}/report.xlsx";
        var workItem = new ReportExportWorkItem(
            RunId: data.RunId,
            RelativePath: relativePath,
            Invoices: data.Invoices,
            ManualReviews: data.ManualReviews,
            Failures: data.FailureReasonCounts
                .Select(kv => new ReportFailureRow(kv.Key, kv.Value))
                .ToList(),
            Summary: new ReportSummaryRow(
                RunId: data.RunId,
                Status: data.Status,
                ReasonCode: data.ReasonCode,
                ResolvedCount: data.Invoices.Count,
                DuplicateCount: 0,
                RetainedCount: 0,
                ManualReviewCount: data.ManualReviews.Count,
                UnresolvedCount: 0,
                CancelledCount: 0,
                QuotaExhaustedCount: 0,
                AuthFailedCount: 0,
                TimeoutCount: 0,
                CompletedAtUtc: data.CompletedAtUtc),
            TemplateVersion: CurrentTemplateVersion);

        var outcome = await _exporter.ExportAsync(workItem, cancellationToken).ConfigureAwait(false);

        await using (var uow = await _uowFactory.BeginAsync(TransactionPurpose.SettingsUpdate, cancellationToken).ConfigureAwait(false))
        {
            await _pathStore.SaveAsync(data.RunId, outcome.RelativePath, outcome.ContentHash, uow, cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ReportExportRpcResult(
            RunId: data.RunId,
            ReportPath: outcome.RelativePath,
            ContentHash: outcome.ContentHash,
            InvoiceRowCount: data.Invoices.Count,
            ManualReviewRowCount: data.ManualReviews.Count,
            TemplateVersion: CurrentTemplateVersion,
            AlreadyExisted: outcome.AlreadyExisted);
    }

    public async Task<ReportOpenToken> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.RunId);
        ArgumentException.ThrowIfNullOrEmpty(request.ReportPath);
        ArgumentException.ThrowIfNullOrEmpty(request.ContentHash);

        if (Path.IsPathRooted(request.ReportPath))
        {
            throw new ArgumentException(
                "ReportPath must be relative — absolute paths are rejected.",
                nameof(request));
        }

        var record = await _tokenStore.IssueAsync(
            request.RunId,
            request.ReportPath,
            request.ContentHash,
            _tokenTtl,
            cancellationToken).ConfigureAwait(false);

        return new ReportOpenToken(
            TokenId: record.TokenId,
            RunId: record.RunId,
            RelativePath: record.RelativePath,
            ContentHash: record.ContentHash,
            IssuedAtUtc: record.IssuedAtUtc,
            ExpiresAtUtc: record.ExpiresAtUtc,
            AlreadyConsumed: false);
    }

    public async Task<ReportOpenResolution> ResolveTokenAsync(string tokenId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenId);

        var record = await _tokenStore.TryConsumeAsync(tokenId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new ReportOpenResolution(tokenId, "", "", "", false, RpcErrorCodes.WebAssetInvalid);
        }
        if (record.AlreadyConsumed)
        {
            return new ReportOpenResolution(tokenId, record.RunId, record.RelativePath, record.ContentHash, false, RpcErrorCodes.WebAssetInvalid);
        }
        if (DateTimeOffset.UtcNow > record.ExpiresAtUtc)
        {
            return new ReportOpenResolution(tokenId, record.RunId, record.RelativePath, record.ContentHash, false, RpcErrorCodes.WebAssetInvalid);
        }

        return new ReportOpenResolution(tokenId, record.RunId, record.RelativePath, record.ContentHash, true, null);
    }
}