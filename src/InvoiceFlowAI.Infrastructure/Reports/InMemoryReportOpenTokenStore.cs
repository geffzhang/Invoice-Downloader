// In-memory ReportOpenTokenStore. The tokens are session-local and short-
// lived (a few minutes TTL) so the implementation only needs a thread-safe
// dictionary. The store is replaced by a persistent store if the
// application ever needs to survive a UI process restart while a token
// is in flight — today that scenario is not in scope.

using System.Collections.Concurrent;
using InvoiceFlowAI.Application.Reports;

namespace InvoiceFlowAI.Infrastructure.Reports;

public sealed class InMemoryReportOpenTokenStore : IReportOpenTokenStore
{
    private readonly ConcurrentDictionary<string, ReportOpenTokenRecord> _tokens = new();

    public Task<ReportOpenTokenRecord> IssueAsync(
        string runId,
        string relativePath,
        string contentHash,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ReportOpenTokenRecord(
            TokenId: Guid.NewGuid().ToString("N"),
            RunId: runId,
            RelativePath: relativePath,
            ContentHash: contentHash,
            IssuedAtUtc: now,
            ExpiresAtUtc: now.Add(ttl),
            AlreadyConsumed: false);
        _tokens[record.TokenId] = record;
        return Task.FromResult(record);
    }

    public Task<ReportOpenTokenRecord?> TryConsumeAsync(string tokenId, CancellationToken cancellationToken)
    {
        if (!_tokens.TryGetValue(tokenId, out var existing))
        {
            return Task.FromResult<ReportOpenTokenRecord?>(null);
        }
        if (existing.AlreadyConsumed)
        {
            // Replay: return the existing record unchanged so the caller
            // sees AlreadyConsumed=true and rejects. Do NOT overwrite —
            // a second call must also see AlreadyConsumed=true.
            return Task.FromResult<ReportOpenTokenRecord?>(existing);
        }
        // First consume: return the pre-consume snapshot (AlreadyConsumed=false)
        // so the caller can treat this as a valid resolve, then atomically mark
        // the stored record as consumed. A subsequent call will see
        // AlreadyConsumed=true and be treated as a replay.
        var consumed = existing with { AlreadyConsumed = true };
        _tokens[tokenId] = consumed;
        return Task.FromResult<ReportOpenTokenRecord?>(existing);
    }

    public Task<ReportOpenTokenRecord?> PeekAsync(string tokenId, CancellationToken cancellationToken)
    {
        _tokens.TryGetValue(tokenId, out var existing);
        return Task.FromResult<ReportOpenTokenRecord?>(existing);
    }
}