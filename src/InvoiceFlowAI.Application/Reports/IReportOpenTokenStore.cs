// Application abstraction for the in-memory one-time open-token store.
// Tokens are issued by ReportApplicationService when the UI requests a
// report.open RPC and consumed once when the WebView resolves the token
// to a real file path. Tokens expire after the configured TTL.

namespace InvoiceFlowAI.Application.Reports;

public sealed record ReportOpenTokenRecord(
    string TokenId,
    string RunId,
    string RelativePath,
    string ContentHash,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool AlreadyConsumed);

public interface IReportOpenTokenStore
{
    Task<ReportOpenTokenRecord> IssueAsync(
        string runId,
        string relativePath,
        string contentHash,
        TimeSpan ttl,
        CancellationToken cancellationToken);

    Task<ReportOpenTokenRecord?> TryConsumeAsync(string tokenId, CancellationToken cancellationToken);

    Task<ReportOpenTokenRecord?> PeekAsync(string tokenId, CancellationToken cancellationToken);
}