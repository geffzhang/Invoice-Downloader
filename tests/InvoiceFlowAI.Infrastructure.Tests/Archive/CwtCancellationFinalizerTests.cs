using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;
using InvoiceFlowAI.Infrastructure.Archive;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class CwtCancellationFinalizerTests
{
    [Fact]
    public async Task Moves_all_matching_hotel_confirmations_once_and_persists_only_safe_relationship_fields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoice-flow-cwt-{Guid.NewGuid():N}");
        var hotelRelativePath = "archive/run-cwt/住宿发票/confirmation.pdf";
        var secondHotelRelativePath = "archive/run-cwt/住宿发票/confirmation-2.pdf";
        var otherHotelRelativePath = "archive/run-cwt/住宿发票/confirmation-other.pdf";
        var cancellationRelativePath = "archive/run-cwt/review/cancellation.pdf";
        var hotelPath = Path.Combine(root, hotelRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var secondHotelPath = Path.Combine(root, secondHotelRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var otherHotelPath = Path.Combine(root, otherHotelRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var cancellationPath = Path.Combine(root, cancellationRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(hotelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cancellationPath)!);
        await File.WriteAllTextAsync(hotelPath, "synthetic hotel confirmation");
        await File.WriteAllTextAsync(secondHotelPath, "synthetic second hotel confirmation");
        await File.WriteAllTextAsync(otherHotelPath, "synthetic other hotel confirmation");
        await File.WriteAllTextAsync(cancellationPath, "synthetic cancellation notice");

        try
        {
            var hotelHash = Hash("synthetic hotel confirmation");
            var secondHotelHash = Hash("synthetic second hotel confirmation");
            var otherHotelHash = Hash("synthetic other hotel confirmation");
            var cancellationHash = Hash("synthetic cancellation notice");
            var store = new FakeArchiveArtifactStore(
                Snapshot("artifact-hotel", "doc-hotel", hotelRelativePath, hotelPath, "20260610_住宿确认单_张三酒店.pdf", hotelHash, "standalone"),
                Snapshot("artifact-hotel-secondary", "doc-hotel", hotelRelativePath, hotelPath, "20260610_住宿确认单_张三酒店.pdf", hotelHash, "zsecondary"),
                Snapshot("artifact-hotel-2", "doc-hotel-2", secondHotelRelativePath, secondHotelPath, "20260611_住宿确认单_张三酒店.pdf", secondHotelHash, "standalone"),
                Snapshot("artifact-other-hotel", "doc-other-hotel", otherHotelRelativePath, otherHotelPath, "20260612_住宿确认单_李四酒店.pdf", otherHotelHash, "standalone"),
                Snapshot("artifact-cancel", "doc-cancel", cancellationRelativePath, cancellationPath,
                    "酒店预定取消知会-张三-20260610入住-上海.pdf", cancellationHash, "manual_review"));
            var reviews = new FakeManualReviewItemStore();
            var pairings = new FakePairingStore();
            var audits = new FakeAuditEventStore();
            var fileSystem = new PhysicalArchiveFileSystem();
            var finalizer = new CwtCancellationFinalizer(
                new ArchiveNamingPolicy(), store, fileSystem, reviews, new FakeUnitOfWorkFactory(), pairings, audits);
            var candidates = new[]
            {
                Candidate("doc-cancel", "酒店预定取消知会-张三-20260610入住-上海.pdf", InvoiceDocumentType.AccommodationConfirmation,
                    new Dictionary<string, string> { ["source_is_cwt"] = "true" }),
                Candidate("doc-hotel", "20260610_住宿确认单_张三酒店.pdf", InvoiceDocumentType.AccommodationConfirmation),
                Candidate("doc-hotel-2", "20260611_住宿确认单_张三酒店.pdf", InvoiceDocumentType.AccommodationConfirmation),
                Candidate("doc-other-hotel", "20260612_住宿确认单_李四酒店.pdf", InvoiceDocumentType.AccommodationConfirmation),
            };

            var first = await finalizer.FinalizeAsync("run-cwt", root, candidates, CancellationToken.None);
            var second = await finalizer.FinalizeAsync("run-cwt", root, candidates, CancellationToken.None);

            first.Matches.Should().Equal("doc-hotel", "doc-hotel-2");
            second.Matches.Should().BeEmpty();
            File.Exists(hotelPath).Should().BeFalse();
            File.Exists(secondHotelPath).Should().BeFalse();
            File.Exists(otherHotelPath).Should().BeTrue();
            var moved = Path.Combine(root, first.UpdatedRelativePaths["doc-hotel"].Replace('/', Path.DirectorySeparatorChar));
            var movedSecond = Path.Combine(root, first.UpdatedRelativePaths["doc-hotel-2"].Replace('/', Path.DirectorySeparatorChar));
            File.Exists(moved).Should().BeTrue();
            File.Exists(movedSecond).Should().BeTrue();
            Hash(await File.ReadAllTextAsync(moved)).Should().Be(hotelHash);
            Hash(await File.ReadAllTextAsync(movedSecond)).Should().Be(secondHotelHash);
            var sidecar = await File.ReadAllTextAsync($"{moved}.json");
            sidecar.Should().Contain("CWT_CANCELLATION_MATCH");
            sidecar.Should().Contain("doc-cancel").And.Contain("doc-hotel");
            sidecar.Should().NotContain("张三").And.NotContain(root).And.NotContain("mycwt.com");
            store.Snapshots.Single(snapshot => snapshot.ArtifactId == "artifact-hotel").ExpectedContentHash.Should().Be(hotelHash);
            store.Snapshots.Single(snapshot => snapshot.ArtifactId == "artifact-hotel").FinalRelativePath.Should().Be(first.UpdatedRelativePaths["doc-hotel"]);
            store.Snapshots.Single(snapshot => snapshot.Key.DocumentId == "doc-hotel-2").ExpectedContentHash.Should().Be(secondHotelHash);
            reviews.Items.Select(item => item.DocumentId).Should().Equal("doc-hotel", "doc-hotel-2");
            reviews.Items.Should().OnlyContain(item => item.ReasonCode == "CWT_CANCELLATION_MATCH");
            pairings.ReconcileCalls.Should().Be(1);
            pairings.ReconciledArtifactIds.Should().HaveCount(5).And.Contain("artifact-hotel-secondary");
            audits.Events.Should().HaveCount(2).And.OnlyContain(item => item.ReasonCode == "CWT_CANCELLATION_MATCH");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Matches_every_archived_inventory_candidate_by_source_filename_not_parsed_document_type()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoice-flow-cwt-inventory-{Guid.NewGuid():N}");
        const string relativePath = "archive/run-cwt/住宿发票/other-classified-document.pdf";
        const string fileName = "20260610_其他分类_张三酒店.pdf";
        const string content = "synthetic archived hotel inventory artifact";
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        try
        {
            var snapshot = Snapshot("artifact-inventory-hotel", "doc-inventory-hotel", relativePath, fullPath,
                fileName, Hash(content), "standalone");
            var store = new FakeArchiveArtifactStore(snapshot);
            var finalizer = new CwtCancellationFinalizer(new ArchiveNamingPolicy(), store,
                new PhysicalArchiveFileSystem(), new FakeManualReviewItemStore(), new FakeUnitOfWorkFactory(),
                new FakePairingStore(), new FakeAuditEventStore());
            var candidates = new[]
            {
                Candidate("doc-cancel", "酒店预定取消知会-张三-20260610入住-上海.pdf", InvoiceDocumentType.AccommodationConfirmation,
                    new Dictionary<string, string> { ["source_is_cwt"] = "true" }),
                Candidate("doc-inventory-hotel", fileName, InvoiceDocumentType.Other),
            };

            var result = await finalizer.FinalizeAsync("run-cwt", root, candidates, CancellationToken.None);

            result.Matches.Should().ContainSingle().Which.Should().Be("doc-inventory-hotel");
            result.UpdatedRelativePaths["doc-inventory-hotel"].Should().Contain("/review/");
            File.Exists(fullPath).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_recovery_required_when_file_rollback_fails_after_database_update_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoice-flow-cwt-rollback-{Guid.NewGuid():N}");
        const string relativePath = "archive/run-cwt/住宿发票/confirmation.pdf";
        const string fileName = "20260610_住宿确认单_张三酒店.pdf";
        const string content = "rollback recovery artifact";
        var sourcePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, content);
        try
        {
            var snapshot = Snapshot("artifact-rollback", "doc-rollback", relativePath, sourcePath,
                fileName, Hash(content), "standalone");
            var fileSystem = new RollbackFailingArchiveFileSystem();
            var store = new FakeArchiveArtifactStore(snapshot) { ThrowOnLocationUpdate = true };
            var finalizer = new CwtCancellationFinalizer(new ArchiveNamingPolicy(), store, fileSystem,
                new FakeManualReviewItemStore(), new FakeUnitOfWorkFactory(), new FakePairingStore(), new FakeAuditEventStore());
            var candidates = new[]
            {
                Candidate("doc-cancel", "酒店预定取消知会-张三-20260610入住-上海.pdf", InvoiceDocumentType.Other,
                    new Dictionary<string, string> { ["source_is_cwt"] = "true" }),
                Candidate("doc-rollback", fileName, InvoiceDocumentType.Other),
            };

            var result = await finalizer.FinalizeAsync("run-cwt", root, candidates, CancellationToken.None);

            result.Matches.Should().BeEmpty();
            result.Failures.Should().ContainSingle().Which.ReasonCode.Should().Be("CWT_MATCH_RECOVERY_REQUIRED");
            var current = await store.ListByRunAsync("run-cwt", CancellationToken.None);
            current.Single().State.Should().Be(ArchiveArtifactState.RecoveryRequired);
            current.Single().FinalRelativePath.Should().Contain("/review/");
            File.Exists(sourcePath).Should().BeFalse();
            File.Exists(current.Single().FinalFilePath!).Should().BeTrue();
            File.Exists(current.Single().FinalFilePath + ".json").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ArchiveArtifactSnapshot Snapshot(
        string artifactId, string documentId, string relativePath, string fullPath, string fileName, string hash, string role)
        => new(artifactId, new ArchiveArtifactKey("run-cwt", documentId, 1, role, hash), string.Empty,
            relativePath, fileName, hash, ArchiveArtifactState.Committed, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, fullPath);

    private static CandidateProcessResult Candidate(
        string documentId, string fileName, InvoiceDocumentType documentType,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var document = new InvoiceDocument(documentId, new DateOnly(2026, 6, 10), "buyer", "seller", 0m, 0m, 0m,
            null, null, documentType, null, null, [], fileName, string.Empty);
        return new CandidateProcessResult(
            new DocumentCandidate(DocumentIdentity.Create(documentId), 0, "corr", "uid", fileName,
                "application/pdf", 1, 1, "mime_attachment", Metadata: metadata),
            CandidateStatus.Resolved, document, string.Empty);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FakeArchiveArtifactStore(params ArchiveArtifactSnapshot[] snapshots) : IArchiveArtifactStore
    {
        private readonly List<ArchiveArtifactSnapshot> _snapshots = [.. snapshots];
        public IReadOnlyList<ArchiveArtifactSnapshot> Snapshots => _snapshots;
        public bool ThrowOnLocationUpdate { get; set; }
        public Task<ArchiveArtifactSnapshot?> FindByKeyAsync(ArchiveArtifactKey key, CancellationToken cancellationToken)
            => Task.FromResult(_snapshots.FirstOrDefault(snapshot => snapshot.Key == key));
        public Task InsertPreparedAsync(ArchiveArtifactSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task MarkCommittedAsync(string artifactId, DateTimeOffset committedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task MarkRecoveryRequiredAsync(string artifactId, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            var index = _snapshots.FindIndex(snapshot => snapshot.ArtifactId == artifactId);
            _snapshots[index] = _snapshots[index] with { State = ArchiveArtifactState.RecoveryRequired };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListByRunAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(_snapshots.Where(snapshot => snapshot.Key.RunId == runId).ToArray());
        public Task UpdateCommittedLocationAsync(string artifactId, string relativePath, string finalPath, string fileName,
            IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (ThrowOnLocationUpdate)
            {
                ThrowOnLocationUpdate = false;
                throw new InvalidOperationException("synthetic persistence failure");
            }
            var index = _snapshots.FindIndex(snapshot => snapshot.ArtifactId == artifactId);
            _snapshots[index] = _snapshots[index] with { FinalRelativePath = relativePath, FinalFilePath = finalPath, FileName = fileName };
            return Task.CompletedTask;
        }
    }

    private sealed class RollbackFailingArchiveFileSystem : IArchiveFileSystem
    {
        private readonly PhysicalArchiveFileSystem _inner = new();
        private string? _movedTarget;
        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) => _inner.ComputeSha256Async(path, cancellationToken);
        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) => _inner.FileExistsAsync(path, cancellationToken);
        public Task<string> CopyToSiblingTempAsync(string sourcePath, string finalFilePath, CancellationToken cancellationToken)
            => _inner.CopyToSiblingTempAsync(sourcePath, finalFilePath, cancellationToken);
        public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
        {
            if (_movedTarget is not null && sourcePath == _movedTarget)
                throw new IOException("synthetic rollback move failure");
            _movedTarget = targetPath;
            return _inner.AtomicMoveAsync(sourcePath, targetPath, cancellationToken);
        }
        public Task FlushToDiskAsync(string path, CancellationToken cancellationToken) => _inner.FlushToDiskAsync(path, cancellationToken);
        public Task DeleteAsync(string path, CancellationToken cancellationToken) => _inner.DeleteAsync(path, cancellationToken);
        public Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
            => _inner.WriteTextAtomicAsync(path, content, cancellationToken);
    }

    private sealed class FakeManualReviewItemStore : IManualReviewItemStore
    {
        public List<(string RunId, string DocumentId, int Revision, string ReasonCode)> Items { get; } = [];
        public Task UpsertOpenAsync(string runId, string documentId, int processingRevision, string reasonCode,
            IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            if (!Items.Any(item => item.RunId == runId && item.DocumentId == documentId && item.Revision == processingRevision))
                Items.Add((runId, documentId, processingRevision, reasonCode));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePairingStore : IPairingStore
    {
        public int ReconcileCalls { get; private set; }
        public IReadOnlyList<string> ReconciledArtifactIds { get; private set; } = [];
        public Task UpsertAsync(PairingRecord record, IUnitOfWork transaction, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReconcileArchiveStateAsync(string runId, IReadOnlyList<ArchiveArtifactSnapshot> artifacts,
            IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            ReconcileCalls++;
            ReconciledArtifactIds = artifacts.Select(snapshot => snapshot.ArtifactId).ToArray();
            artifacts.Where(snapshot => snapshot.Key.DocumentId.StartsWith("doc-hotel", StringComparison.Ordinal))
                .Should().OnlyContain(snapshot => snapshot.State == ArchiveArtifactState.Committed);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditEventStore : IAuditEventStore
    {
        public List<AuditEventRecord> Events { get; } = [];
        public Task AppendAsync(AuditEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            Events.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUnitOfWorkFactory : IUnitOfWorkFactory
    {
        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
            => Task.FromResult<IUnitOfWork>(new FakeUnitOfWork(purpose));
    }

    private sealed class FakeUnitOfWork(TransactionPurpose purpose) : IUnitOfWork
    {
        public string TransactionId { get; } = Guid.NewGuid().ToString("N");
        public TransactionPurpose Purpose { get; } = purpose;
        public bool IsCompleted { get; private set; }
        public Task CommitAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
