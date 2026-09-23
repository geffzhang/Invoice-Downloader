// Verifies ReportApplicationService (design §7):
//   * Open issues a token bound to run/path/hash with the configured TTL
//   * ResolveTokenAsync returns Valid=true on first resolve within TTL
//   * ResolveTokenAsync rejects a second resolve (replay protection)
//   * ResolveTokenAsync rejects an expired token
//   * ResolveTokenAsync returns invalid for unknown token ids
//   * Open rejects absolute paths (security boundary)

using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Reports;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Reports;

public sealed class ReportApplicationServiceTests
{
    [Fact]
    public async Task Open_issues_token_with_configured_ttl()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));

        var token = await harness.Service.OpenAsync(NewRequest(), CancellationToken.None);

        token.ExpiresAtUtc.Should().BeCloseTo(token.IssuedAtUtc.AddMinutes(5), TimeSpan.FromSeconds(1));
        token.AlreadyConsumed.Should().BeFalse();
        token.RunId.Should().Be("run-1");
        token.RelativePath.Should().Be("reports/run-1/report.xlsx");
        token.ContentHash.Should().Be("hash-1");
        token.TokenId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResolveTokenAsync_returns_valid_for_first_resolve_within_ttl()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));
        var token = await harness.Service.OpenAsync(NewRequest(), CancellationToken.None);

        var resolution = await harness.Service.ResolveTokenAsync(token.TokenId, CancellationToken.None);

        resolution.Valid.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
        resolution.RunId.Should().Be("run-1");
        resolution.ContentHash.Should().Be("hash-1");
    }

    [Fact]
    public async Task ResolveTokenAsync_rejects_replay()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));
        var token = await harness.Service.OpenAsync(NewRequest(), CancellationToken.None);

        var first = await harness.Service.ResolveTokenAsync(token.TokenId, CancellationToken.None);
        var second = await harness.Service.ResolveTokenAsync(token.TokenId, CancellationToken.None);

        first.Valid.Should().BeTrue();
        second.Valid.Should().BeFalse();
        second.ReasonCode.Should().Be(RpcErrorCodes.WebAssetInvalid);
    }

    [Fact]
    public async Task ResolveTokenAsync_rejects_expired_token()
    {
        var harness = new Harness(ttl: TimeSpan.FromMilliseconds(1));
        var token = await harness.Service.OpenAsync(NewRequest(), CancellationToken.None);
        await Task.Delay(50);

        var resolution = await harness.Service.ResolveTokenAsync(token.TokenId, CancellationToken.None);

        resolution.Valid.Should().BeFalse();
        resolution.ReasonCode.Should().Be(RpcErrorCodes.WebAssetInvalid);
    }

    [Fact]
    public async Task ResolveTokenAsync_returns_invalid_for_unknown_token_id()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));

        var resolution = await harness.Service.ResolveTokenAsync("does-not-exist", CancellationToken.None);

        resolution.Valid.Should().BeFalse();
        resolution.ReasonCode.Should().Be(RpcErrorCodes.WebAssetInvalid);
    }

    [Fact]
    public async Task Open_rejects_absolute_path()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));
        var request = new ReportOpenRequest("run-1", "C:/absolute/report.xlsx", "hash-1");

        var act = () => harness.Service.OpenAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*absolute*");
    }

    [Fact]
    public async Task Export_persists_relative_path_and_hash_via_path_store()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));
        var data = NewRunData();
        harness.DataSource.Setup(data);

        var result = await harness.Service.ExportAsync(new ReportExportRequest("run-1"), CancellationToken.None);

        result.RunId.Should().Be("run-1");
        result.ReportPath.Should().Be("reports/run-1/report.xlsx");
        result.ContentHash.Should().NotBeNullOrEmpty();
        harness.PathStore.LastSavedRunId.Should().Be("run-1");
        harness.PathStore.LastSavedHash.Should().Be(result.ContentHash);
    }

    [Fact]
    public async Task Export_throws_when_run_has_no_data()
    {
        var harness = new Harness(ttl: TimeSpan.FromMinutes(5));
        harness.DataSource.Setup(null);

        var act = () => harness.Service.ExportAsync(new ReportExportRequest("missing"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ReportOpenRequest NewRequest() => new("run-1", "reports/run-1/report.xlsx", "hash-1");

    private static ReportRunData NewRunData() => new(
        RunId: "run-1",
        Status: "Completed",
        ReasonCode: "RUN_COMPLETED",
        CompletedAtUtc: new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        Invoices: new List<ReportInvoiceRow>
        {
            new("inv-1", new DateOnly(2026, 9, 15), "ACME", "Air Berlin", 100m, 13m, 113m,
                "INV-001", "CODE-001", "FlightInvoice", "transport", "0.95", "Committed"),
        }.AsReadOnly(),
        ManualReviews: new List<ReportManualReviewRow>().AsReadOnly(),
        FailureReasonCounts: new Dictionary<string, int>(),
        ExistingRelativePath: null,
        ExistingContentHash: null);

    private sealed class Harness
    {
        public Harness(TimeSpan ttl)
        {
            DataSource = new FakeDataSource();
            PathStore = new FakePathStore();
            Exporter = new FakeExporter();
            TokenStore = new FakeTokenStore();
            UowFactory = new FakeUowFactory();
            Service = new ReportApplicationService(
                Exporter, PathStore, DataSource, TokenStore, UowFactory, ttl);
        }

        public ReportApplicationService Service { get; }
        public FakeDataSource DataSource { get; }
        public FakePathStore PathStore { get; }
        public FakeExporter Exporter { get; }
        public FakeTokenStore TokenStore { get; }
        public FakeUowFactory UowFactory { get; }
    }

    private sealed class FakeDataSource : IReportRunDataSource
    {
        private ReportRunData? _data;
        public void Setup(ReportRunData? data) => _data = data;
        public Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult(_data?.RunId == runId ? _data : null);
    }

    private sealed class FakePathStore : IReportPathStore
    {
        public string? LastSavedRunId { get; private set; }
        public string? LastSavedHash { get; private set; }
        public Task SaveAsync(string runId, string relativePath, string contentHash, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            LastSavedRunId = runId;
            LastSavedHash = contentHash;
            return Task.CompletedTask;
        }
        public Task<ReportRunData?> LoadAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<ReportRunData?>(null);
    }

    private sealed class FakeExporter : IReportExporter
    {
        public Task<ReportExportOutcome> ExportAsync(ReportExportWorkItem workItem, CancellationToken cancellationToken)
            => Task.FromResult(new ReportExportOutcome(
                RelativePath: workItem.RelativePath,
                ContentHash: "hash-exported",
                AlreadyExisted: false));
    }

    private sealed class FakeTokenStore : IReportOpenTokenStore
    {
        private readonly Dictionary<string, ReportOpenTokenRecord> _tokens = new();
        public Task<ReportOpenTokenRecord> IssueAsync(string runId, string relativePath, string contentHash, TimeSpan ttl, CancellationToken cancellationToken)
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
                return Task.FromResult<ReportOpenTokenRecord?>(null);
            if (existing.AlreadyConsumed)
                return Task.FromResult<ReportOpenTokenRecord?>(existing);
            // First consume: return pre-consume snapshot, mark stored record.
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

    private sealed class FakeUowFactory : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
            => Task.FromResult<IUnitOfWork>(new FakeUow());
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public string TransactionId => "tx-fake";
        public TransactionPurpose Purpose => TransactionPurpose.SettingsUpdate;
        public bool IsCompleted => false;
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
