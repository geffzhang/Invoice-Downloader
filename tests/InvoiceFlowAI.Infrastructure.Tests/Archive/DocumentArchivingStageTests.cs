using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Archive;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class DocumentArchivingStageTests
{
    [Fact]
    public async Task Standalone_resolved_artifact_is_archived_without_mutating_source_path_or_bytes()
    {
        var source = CreateSource("standalone.pdf", "standalone source");
        var outputRoot = TempRoot;
        try
        {
            var stage = CreateStage(out var coordinator, out _, out _);
            var candidate = NewResult("doc-standalone", source, InvoiceDocumentType.AirTicket, CandidateStatus.Resolved);

            var batch = await stage.ExecuteAsync(new ArchiveStageRequest("run-standalone", outputRoot, NewBatch(candidate)), CancellationToken.None);

            batch.Results.Should().ContainSingle().Which.ArtifactPath.Should().Be(source);
            batch.Artifacts.Should().ContainSingle(outcome => outcome.DocumentId == "doc-standalone" && outcome.State == ArchiveArtifactState.Committed);
            coordinator.Requests.Should().ContainSingle();
            coordinator.Requests[0].SourceFilePath.Should().Be(source);
            coordinator.Requests[0].FinalFilePath.Should().StartWith(Path.GetFullPath(outputRoot));
            File.ReadAllText(source).Should().Be("standalone source");
        }
        finally
        {
            DeleteSource(source);
        }
    }

    [Fact]
    public async Task Pair_state_is_committed_only_after_both_archive_artifacts_commit()
    {
        var invoiceSource = CreateSource("ride-invoice.pdf", "invoice bytes");
        var itinerarySource = CreateSource("ride-itinerary.ofd", "itinerary bytes");
        try
        {
            var stage = CreateStage(out var coordinator, out var pairStore, out var reviewStore);
            var invoice = NewResult("ride-invoice", invoiceSource, InvoiceDocumentType.RideInvoice, CandidateStatus.Resolved);
            var itinerary = NewResult("ride-itinerary", itinerarySource, InvoiceDocumentType.RideItinerary, CandidateStatus.Resolved);
            var assignment = new PairingAssignment(
                NewPairingDocument(invoice, PairingRole.RideInvoice),
                NewPairingDocument(itinerary, PairingRole.RideItinerary),
                Score: 130);
            var pairing = new PairingResult([assignment], [], [], []);

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest("run-pair", TempRoot, new PairingBatch([pairing], [invoice, itinerary])),
                CancellationToken.None);

            coordinator.Requests.Should().HaveCount(2);
            pairStore.Records.Select(record => record.State).Should().Equal("Prepared", "Committed");
            reviewStore.Items.Should().BeEmpty();
            batch.Artifacts.Should().HaveCount(2).And.OnlyContain(outcome => outcome.State == ArchiveArtifactState.Committed);
            File.ReadAllText(invoiceSource).Should().Be("invoice bytes");
            File.ReadAllText(itinerarySource).Should().Be("itinerary bytes");
        }
        finally
        {
            DeleteSource(invoiceSource);
            DeleteSource(itinerarySource);
        }
    }

    [Fact]
    public async Task Resolved_candidate_without_source_returns_safe_failure_without_archive_path()
    {
        var stage = CreateStage(out var coordinator, out _, out _);
        var candidate = NewResult("doc-no-source", "", InvoiceDocumentType.AirTicket, CandidateStatus.Resolved);

        var batch = await stage.ExecuteAsync(new ArchiveStageRequest("run-no-source", TempRoot, NewBatch(candidate)), CancellationToken.None);

        batch.Results.Should().ContainSingle().Which.Failure!.ReasonCode.Should().Be("ARCHIVE_SOURCE_MISSING");
        batch.Artifacts.Should().BeEmpty();
        coordinator.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Source_hash_mismatch_is_rejected_before_archive_commit()
    {
        var source = CreateSource("hash-mismatch.pdf", "actual bytes");
        try
        {
            var stage = CreateStage(out var coordinator, out _, out _);
            var result = NewResult("doc-hash-mismatch", source, InvoiceDocumentType.AirTicket, CandidateStatus.Resolved);
            result = result with { Invoice = result.Invoice! with { ContentHash = new string('0', 64) } };

            var batch = await stage.ExecuteAsync(new ArchiveStageRequest("run-hash", TempRoot, NewBatch(result)), CancellationToken.None);

            batch.Results.Single().Failure!.ReasonCode.Should().Be("ARCHIVE_SOURCE_HASH_MISMATCH");
            batch.Artifacts.Should().BeEmpty();
            coordinator.Requests.Should().BeEmpty();
            File.ReadAllText(source).Should().Be("actual bytes");
        }
        finally
        {
            DeleteSource(source);
        }
    }

    [Fact]
    public async Task Provider_identity_mismatch_is_routed_to_review_not_regular_archive()
    {
        var source = CreateSource("provider-mismatch.pdf", "synthetic mismatched provider artifact");
        try
        {
            var stage = CreateStage(out var coordinator, out _, out var reviewStore);
            var result = NewResult("provider-mismatch", source, InvoiceDocumentType.AirTicket, CandidateStatus.Unresolved) with
            {
                Failure = new CandidateFailure(
                    "DIRECT_INVOICE_PDF_ENTITY_MISMATCH",
                    FailureScope.Candidate,
                    FailureCategory.Validation,
                    Retryable: false,
                    "The recovered invoice did not match its source identity."),
            };

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest("run-provider-mismatch", TempRoot, NewBatch(result)), CancellationToken.None);

            batch.Results.Should().ContainSingle().Which.Status.Should().Be(CandidateStatus.Unresolved);
            batch.Results.Single().Failure!.ReasonCode.Should().Be("DIRECT_INVOICE_PDF_ENTITY_MISMATCH");
            reviewStore.Items.Should().ContainSingle(item =>
                item.DocumentId == "provider-mismatch" && item.Reason == "DIRECT_INVOICE_PDF_ENTITY_MISMATCH");
            coordinator.Requests.Should().ContainSingle(request => request.Key.Role == "manual_review");
            coordinator.Requests.Should().NotContain(request => request.Key.Role == "standalone");
            batch.Artifacts.Should().ContainSingle(outcome =>
                outcome.DocumentId == "provider-mismatch"
                && outcome.RelativePath!.Contains("/review/", StringComparison.Ordinal));
        }
        finally
        {
            DeleteSource(source);
        }
    }

    [Fact]
    public async Task Run_id_path_traversal_is_rejected_before_coordinator_call()
    {
        var source = CreateSource("traversal.pdf", "safe bytes");
        try
        {
            var stage = CreateStage(out var coordinator, out _, out _);
            var result = NewResult("doc-traversal", source, InvoiceDocumentType.AirTicket, CandidateStatus.Resolved);

            var batch = await stage.ExecuteAsync(new ArchiveStageRequest("../../outside", TempRoot, NewBatch(result)), CancellationToken.None);

            batch.Results.Single().Failure!.ReasonCode.Should().Be("ARCHIVE_PATH_INVALID");
            batch.Artifacts.Should().ContainSingle(outcome => outcome.State == ArchiveArtifactState.Absent && outcome.RelativePath == null);
            coordinator.Requests.Should().BeEmpty();
            File.ReadAllText(source).Should().Be("safe bytes");
        }
        finally
        {
            DeleteSource(source);
        }
    }

    [Fact]
    public async Task Pre_cancelled_request_has_no_archive_side_effects()
    {
        var source = CreateSource("cancelled.pdf", "cancelled bytes");
        try
        {
            var stage = CreateStage(out var coordinator, out _, out _);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = () => stage.ExecuteAsync(
                new ArchiveStageRequest("run-cancelled", TempRoot, NewBatch(NewResult("doc-cancelled", source, InvoiceDocumentType.AirTicket, CandidateStatus.Resolved))),
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            coordinator.Requests.Should().BeEmpty();
            File.ReadAllText(source).Should().Be("cancelled bytes");
        }
        finally
        {
            DeleteSource(source);
        }
    }

    [Fact]
    public async Task Partial_pair_commit_leaves_pair_recovery_required_and_preserves_both_sources()
    {
        var invoiceSource = CreateSource("partial-invoice.pdf", "partial invoice");
        var itinerarySource = CreateSource("partial-itinerary.pdf", "partial itinerary");
        try
        {
            var stage = CreateStage(out var coordinator, out var pairStore, out _);
            coordinator.RecoveryOnCall = 2;
            var invoice = NewResult("partial-invoice", invoiceSource, InvoiceDocumentType.RideInvoice, CandidateStatus.Resolved);
            var itinerary = NewResult("partial-itinerary", itinerarySource, InvoiceDocumentType.RideItinerary, CandidateStatus.Resolved);
            var pairing = new PairingResult([
                new PairingAssignment(NewPairingDocument(invoice, PairingRole.RideInvoice), NewPairingDocument(itinerary, PairingRole.RideItinerary), 125)], [], [], []);

            var batch = await stage.ExecuteAsync(new ArchiveStageRequest("run-partial", TempRoot, new PairingBatch([pairing], [invoice, itinerary])), CancellationToken.None);

            pairStore.Records.Select(record => record.State).Should().Equal("Prepared", "RecoveryRequired");
            batch.Artifacts.Select(outcome => outcome.State).Should().Equal(ArchiveArtifactState.Committed, ArchiveArtifactState.RecoveryRequired);
            File.ReadAllText(invoiceSource).Should().Be("partial invoice");
            File.ReadAllText(itinerarySource).Should().Be("partial itinerary");
        }
        finally
        {
            DeleteSource(invoiceSource);
            DeleteSource(itinerarySource);
        }
    }

    [Fact]
    public async Task Pair_member_coordinator_exception_is_isolated_and_other_member_is_attempted()
    {
        var invoiceSource = CreateSource("exception-invoice.pdf", "exception invoice");
        var itinerarySource = CreateSource("exception-itinerary.pdf", "exception itinerary");
        try
        {
            var stage = CreateStage(out var coordinator, out var pairStore, out _);
            coordinator.ThrowOnCall = 1;
            var invoice = NewResult("exception-invoice", invoiceSource, InvoiceDocumentType.RideInvoice, CandidateStatus.Resolved);
            var itinerary = NewResult("exception-itinerary", itinerarySource, InvoiceDocumentType.RideItinerary, CandidateStatus.Resolved);
            var pairing = new PairingResult([
                new PairingAssignment(NewPairingDocument(invoice, PairingRole.RideInvoice), NewPairingDocument(itinerary, PairingRole.RideItinerary), 125)], [], [], []);

            var batch = await stage.ExecuteAsync(new ArchiveStageRequest("run-exception", TempRoot, new PairingBatch([pairing], [invoice, itinerary])), CancellationToken.None);

            coordinator.Requests.Should().HaveCount(2);
            pairStore.Records.Select(record => record.State).Should().Equal("Prepared", "RecoveryRequired");
            batch.Results.Should().OnlyContain(result => result.Status == CandidateStatus.ManualReview);
            File.ReadAllText(invoiceSource).Should().Be("exception invoice");
            File.ReadAllText(itinerarySource).Should().Be("exception itinerary");
        }
        finally
        {
            DeleteSource(invoiceSource);
            DeleteSource(itinerarySource);
        }
    }

    [Fact]
    public async Task Manual_review_and_retained_are_archived_but_duplicate_is_skipped()
    {
        var reviewSource = CreateSource("review.pdf", "review bytes");
        var retainedSource = CreateSource("retained.pdf", "retained bytes");
        var duplicateSource = CreateSource("duplicate.pdf", "duplicate bytes");
        try
        {
            var stage = CreateStage(out var coordinator, out _, out var reviewStore);
            var review = NewResult("doc-review", reviewSource, InvoiceDocumentType.AirTicket, CandidateStatus.ManualReview) with
            {
                Failure = new CandidateFailure("PAIRING_COUNTERPART_MISSING", FailureScope.Candidate, FailureCategory.Validation, false, "Review required."),
            };
            var retained = NewResult("doc-retained", retainedSource, InvoiceDocumentType.AirTicket, CandidateStatus.Retained);
            var duplicate = NewResult("doc-duplicate", duplicateSource, InvoiceDocumentType.AirTicket, CandidateStatus.Duplicate);

            var batch = await stage.ExecuteAsync(new ArchiveStageRequest("run-routing", TempRoot, new PairingBatch([], [review, retained, duplicate])), CancellationToken.None);

            reviewStore.Items.Should().ContainSingle(item => item.DocumentId == "doc-review" && item.Reason == "PAIRING_COUNTERPART_MISSING");
            coordinator.Requests.Should().HaveCount(2);
            batch.Artifacts.Should().HaveCount(2);
            batch.Artifacts.Select(outcome => outcome.RelativePath).Should().Contain(path => path!.Contains("/review/", StringComparison.Ordinal));
            batch.Artifacts.Select(outcome => outcome.RelativePath).Should().Contain(path => path!.Contains("/retained/", StringComparison.Ordinal));
            batch.Results.Single(result => result.Candidate.DocumentId.Value == "doc-duplicate").Status.Should().Be(CandidateStatus.Duplicate);
        }
        finally
        {
            DeleteSource(reviewSource);
            DeleteSource(retainedSource);
            DeleteSource(duplicateSource);
        }
    }

    [Fact]
    public async Task Cwt_cancellation_is_routed_to_manual_review_before_any_pair_processing()
    {
        var source = CreateSource("酒店预定取消知会-张三-20260610入住-上海.pdf", "synthetic cancellation notice");
        try
        {
            var stage = CreateStage(out var coordinator, out var pairStore, out var reviewStore);
            var cancellation = NewResult("cwt-cancellation", source, InvoiceDocumentType.Other, CandidateStatus.Resolved);
            cancellation = cancellation with
            {
                Candidate = cancellation.Candidate with
                {
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source_is_cwt"] = "true",
                    },
                },
            };

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest("run-cwt", TempRoot, NewBatch(cancellation)), CancellationToken.None);

            batch.Results.Single().Invoice!.DocumentType.Should().Be(InvoiceDocumentType.Other);
            reviewStore.Items.Should().ContainSingle(item =>
                item.DocumentId == "cwt-cancellation" && item.Reason == "CWT_HOTEL_CANCELLATION");
            batch.Artifacts.Should().ContainSingle(outcome =>
                outcome.DocumentId == "cwt-cancellation"
                && outcome.State == ArchiveArtifactState.Committed
                && outcome.RelativePath!.Contains("/review/", StringComparison.Ordinal));
            coordinator.Requests.Should().ContainSingle(request => request.Key.Role == "manual_review");
            pairStore.Records.Should().BeEmpty();
        }
        finally
        {
            DeleteSource(source);
        }
    }

    [Fact]
    public async Task Archive_status_golden_fixtures_match_routing_and_safe_failure_codes()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "DesktopParity", "archive-routing.json");
        var fixtures = JsonSerializer.Deserialize<ArchiveRoutingFixtureSet>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidDataException("Archive routing parity fixtures are empty.");

        foreach (var fixture in fixtures.Cases)
        {
            var source = fixture.SourceAvailable ? CreateSource($"{fixture.CaseId}.pdf", "synthetic archive bytes") : string.Empty;
            try
            {
                var stage = CreateStage(out var coordinator, out _, out _);
                var result = NewResult(fixture.CaseId, source, InvoiceDocumentType.AirTicket,
                    Enum.Parse<CandidateStatus>(fixture.Status, ignoreCase: true));
                if (fixture.ReasonCode is not null)
                {
                    result = result with
                    {
                        Failure = new CandidateFailure(fixture.ReasonCode, FailureScope.Candidate,
                            FailureCategory.Validation, Retryable: false, SafeMessage: "Synthetic review case."),
                    };
                }

                var batch = await stage.ExecuteAsync(
                    new ArchiveStageRequest(fixture.CaseId, TempRoot, NewBatch(result)), CancellationToken.None);

                batch.Artifacts.Should().HaveCount(fixture.ExpectedArtifactCount, fixture.CaseId);
                if (fixture.ExpectedState is null)
                {
                    batch.Artifacts.Should().BeEmpty(fixture.CaseId);
                }
                else
                {
                    batch.Artifacts.Select(outcome => outcome.State.ToString())
                        .Should().OnlyContain(state => state == fixture.ExpectedState, fixture.CaseId);
                }
                if (fixture.ExpectedPathSegment is not null)
                {
                    batch.Artifacts.Should().OnlyContain(outcome =>
                        outcome.RelativePath!.Contains($"/{fixture.ExpectedPathSegment}/", StringComparison.Ordinal), fixture.CaseId);
                }
                coordinator.Requests.Should().HaveCount(fixture.ExpectedArtifactCount, fixture.CaseId);
                batch.Results.Single().Failure?.ReasonCode.Should().Be(fixture.ExpectedFailureCode, fixture.CaseId);
            }
            finally
            {
                DeleteSource(source);
            }
        }
    }

    private static DocumentArchivingStage CreateStage(
        out RecordingCoordinator coordinator,
        out RecordingPairingStore pairStore,
        out RecordingReviewStore reviewStore)
    {
        coordinator = new RecordingCoordinator();
        pairStore = new RecordingPairingStore();
        reviewStore = new RecordingReviewStore();
        return new DocumentArchivingStage(
            new ArchiveNamingPolicy(),
            coordinator,
            new HashingFileSystem(),
            pairStore,
            reviewStore,
            new TestUnitOfWorkFactory());
    }

    private static PairingBatch NewBatch(CandidateProcessResult candidate) => new([], [candidate]);

    private static CandidateProcessResult NewResult(string id, string source, InvoiceDocumentType type, CandidateStatus status)
    {
        var bytes = string.IsNullOrEmpty(source) ? Array.Empty<byte>() : File.ReadAllBytes(source);
        var invoice = new InvoiceDocument(
            DocumentId: id,
            InvoiceDate: new DateOnly(2026, 9, 24),
            Purchaser: "Buyer",
            Seller: "Merchant",
            Amount: 120m,
            TaxAmount: 0m,
            TotalAmount: 120m,
            InvoiceCode: "code",
            InvoiceNumber: id,
            DocumentType: type,
            Category: null,
            Route: null,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: Path.GetFileName(source),
            ContentHash: bytes.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return new CandidateProcessResult(
            new DocumentCandidate(DocumentIdentity.Create(id), 0, "corr", "uid", Path.GetFileName(source), "application/pdf", bytes.Length, 1, "attachment"),
            status,
            invoice,
            source);
    }

    private static PairingDocument NewPairingDocument(CandidateProcessResult result, PairingRole role) => new(
        result.Candidate.DocumentId.Value,
        role,
        result.Invoice!.Amount,
        result.Invoice.InvoiceDate,
        "",
        [],
        result.Candidate.SourceMessageUid,
        result.ArtifactPath);

    private static string CreateSource(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"invoice-flow-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteSource(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static string TempRoot => Path.Combine(Path.GetTempPath(), $"invoice-flow-output-{Guid.NewGuid():N}");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record ArchiveRoutingFixtureSet(IReadOnlyList<ArchiveRoutingCase> Cases);

    private sealed record ArchiveRoutingCase(
        string CaseId,
        string Status,
        bool SourceAvailable,
        string? ReasonCode,
        int ExpectedArtifactCount,
        string? ExpectedPathSegment,
        string? ExpectedState,
        string? ExpectedFailureCode);

    private sealed class HashingFileSystem : IArchiveFileSystem
    {
        public Task<IReadOnlyList<string>> EnumerateDirectChildFilesAsync(string directoryPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(File.Exists(path));
        public Task<string> CopyToSiblingTempAsync(string sourcePath, string finalFilePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FlushToDiskAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingCoordinator : IArchiveCommitCoordinator
    {
        public List<ArchiveCommitRequest> Requests { get; } = [];
        public int RecoveryOnCall { get; set; }
        public int ThrowOnCall { get; set; }
        public Task<ArchiveCommitResult> CommitAsync(ArchiveCommitRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == ThrowOnCall) throw new IOException("simulated archive interruption");
            return Task.FromResult(new ArchiveCommitResult(
                $"artifact-{Requests.Count}",
                Requests.Count == RecoveryOnCall ? ArchiveArtifactState.RecoveryRequired : ArchiveArtifactState.Committed,
                request.FinalRelativePath,
                request.Key.ExpectedContentHash,
                AlreadyExisted: false,
                ReasonCode: Requests.Count == RecoveryOnCall ? "ARCHIVE_TEST_RECOVERY" : null));
        }
    }

    private sealed class RecordingPairingStore : IPairingStore
    {
        public List<PairingRecord> Records { get; } = [];
        public Task UpsertAsync(PairingRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
        public Task ReconcileArchiveStateAsync(string runId, IReadOnlyList<ArchiveArtifactSnapshot> artifacts, IUnitOfWork transaction, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingReviewStore : IManualReviewItemStore
    {
        public List<(string RunId, string DocumentId, int Revision, string Reason)> Items { get; } = [];
        public Task UpsertOpenAsync(string runId, string documentId, int processingRevision, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            Items.Add((runId, documentId, processingRevision, reasonCode));
            return Task.CompletedTask;
        }
    }

    private sealed class TestUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken) => Task.FromResult<IUnitOfWork>(new TestUnitOfWork(purpose));
    }

    private sealed class TestUnitOfWork(TransactionPurpose purpose) : IUnitOfWork
    {
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public TransactionPurpose Purpose { get; } = purpose;
        public bool IsCompleted { get; private set; }
        public Task CommitAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
