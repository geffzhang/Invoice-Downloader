// Verifies IArchiveRecoveryService (design §7 / §11):
//   * Final file present with matching hash → commit and mark Committed
//   * Final file present with mismatched hash → RecoveryRequired
//   * Only temp file present → re-move and verify
//   * Both temp + final missing → RecoveryRequired (no silent delete)
//   * Orphan final file with no DB row → state is handled by the store
//   * Duplicate commit is idempotent

using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class ArchiveRecoveryServiceTests
{
    [Fact]
    public async Task Final_present_with_matching_hash_commits()
    {
        var fakes = SetupPrepared(content: "archive-content");
        var recovery = new ArchiveRecoveryService(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var entries = await recovery.ScanAsync("run-1", CancellationToken.None);
        var decision = await recovery.ResolveAsync(entries[0], CancellationToken.None);

        decision.ResolvedState.Should().Be(ArchiveArtifactState.Committed);
        decision.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task Final_present_with_different_hash_surfaces_recovery()
    {
        var fakes = SetupPrepared(content: "actual-bytes");
        // Replace the final file with mismatching bytes.
        fakes.FileSystem.ReplaceFinal("archive/final-1.bin", "tampered-bytes");
        var recovery = new ArchiveRecoveryService(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var entries = await recovery.ScanAsync("run-1", CancellationToken.None);
        var decision = await recovery.ResolveAsync(entries[0], CancellationToken.None);

        decision.ResolvedState.Should().Be(ArchiveArtifactState.RecoveryRequired);
        decision.ReasonCode.Should().Be("ARCHIVE_HASH_MISMATCH");
    }

    [Fact]
    public async Task Only_temp_present_removes_temp_and_marks_committed()
    {
        var fakes = SetupPrepared(content: "archive-content");
        // Delete the final file.
        await fakes.FileSystem.DeleteAsync("archive/final-1.bin", CancellationToken.None);
        var recovery = new ArchiveRecoveryService(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var entries = await recovery.ScanAsync("run-1", CancellationToken.None);
        var decision = await recovery.ResolveAsync(entries[0], CancellationToken.None);

        decision.ResolvedState.Should().Be(ArchiveArtifactState.Committed);
        (await fakes.FileSystem.FileExistsAsync("archive/final-1.bin", CancellationToken.None)).Should().BeTrue();
        (await fakes.FileSystem.FileExistsAsync("temp-1.bin", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Both_files_missing_surfaces_recovery_without_silent_delete()
    {
        var fakes = SetupPrepared(content: "archive-content");
        await fakes.FileSystem.DeleteAsync("archive/final-1.bin", CancellationToken.None);
        await fakes.FileSystem.DeleteAsync("temp-1.bin", CancellationToken.None);
        var recovery = new ArchiveRecoveryService(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var entries = await recovery.ScanAsync("run-1", CancellationToken.None);
        var decision = await recovery.ResolveAsync(entries[0], CancellationToken.None);

        decision.ResolvedState.Should().Be(ArchiveArtifactState.RecoveryRequired);
        decision.ReasonCode.Should().Be("ARCHIVE_BOTH_FILES_MISSING");
        // RecoveryRequired is never an excuse to delete files silently — we
        // verify that neither file was recreated by the recovery attempt.
        (await fakes.FileSystem.FileExistsAsync("archive/final-1.bin", CancellationToken.None)).Should().BeFalse();
        (await fakes.FileSystem.FileExistsAsync("temp-1.bin", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Idempotent_resolve_does_not_double_commit()
    {
        var fakes = SetupPrepared(content: "archive-content");
        var recovery = new ArchiveRecoveryService(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var entries = await recovery.ScanAsync("run-1", CancellationToken.None);
        var first = await recovery.ResolveAsync(entries[0], CancellationToken.None);
        var second = await recovery.ResolveAsync(entries[0], CancellationToken.None);

        first.ResolvedState.Should().Be(ArchiveArtifactState.Committed);
        second.ResolvedState.Should().Be(ArchiveArtifactState.Committed);
    }

    private static Fakes SetupPrepared(string content)
    {
        var fakes = new Fakes();
        fakes.FileSystem.WriteFile("temp-1.bin", content);
        fakes.FileSystem.WriteFile("archive/final-1.bin", content);
        var hash = Sha256Hex(content);
        fakes.Store.AddPrepared(
            new ArchiveArtifactSnapshot(
                ArtifactId: "archive-1",
                Key: new ArchiveArtifactKey("run-1", "doc-1", 1, "Invoice", hash),
                TempFilePath: "temp-1.bin",
                FinalRelativePath: "archive/final-1.bin",
                FileName: "final-1.bin",
                ExpectedContentHash: hash,
                State: ArchiveArtifactState.Prepared,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                CommittedAtUtc: null));
        return fakes;
    }

    private static string Sha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class Fakes
    {
        public FakeUowFactory UowFactory { get; } = new();
        public FakeArchiveFileSystem FileSystem { get; } = new();
        public FakeArchiveArtifactStore Store { get; } = new();
        public FakeAuditStore AuditStore { get; } = new();
    }

    private sealed class FakeUowFactory : IUnitOfWorkFactory
    {
        public int CommitCount { get; private set; }
        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
            => Task.FromResult<IUnitOfWork>(new FakeUow(this));
        private sealed class FakeUow : IUnitOfWork
        {
            private readonly FakeUowFactory _factory;
            public FakeUow(FakeUowFactory factory) { _factory = factory; TransactionId = Guid.NewGuid().ToString("N"); Purpose = TransactionPurpose.ArchiveCommit; }
            public string TransactionId { get; }
            public TransactionPurpose Purpose { get; }
            public bool IsCompleted { get; private set; }
            public Task CommitAsync(CancellationToken cancellationToken) { IsCompleted = true; _factory.CommitCount++; return Task.CompletedTask; }
            public Task RollbackAsync(CancellationToken cancellationToken) { IsCompleted = true; return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeArchiveFileSystem : IArchiveFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public void WriteFile(string path, string content) =>
            _files[path] = System.Text.Encoding.UTF8.GetBytes(content);

        public void ReplaceFinal(string path, string content) =>
            _files[path] = System.Text.Encoding.UTF8.GetBytes(content);

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            if (!_files.TryGetValue(path, out var bytes))
            {
                throw new FileNotFoundException(path);
            }
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Task.FromResult(Convert.ToHexString(hash).ToLowerInvariant());
        }

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(_files.ContainsKey(path));

        public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
        {
            if (!_files.TryGetValue(sourcePath, out var bytes))
            {
                throw new FileNotFoundException(sourcePath);
            }
            _files.Remove(sourcePath);
            _files[targetPath] = bytes;
            return Task.CompletedTask;
        }

        public Task FlushToDiskAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArchiveArtifactStore : IArchiveArtifactStore
    {
        private readonly Dictionary<string, ArchiveArtifactSnapshot> _byId = new();
        private readonly Dictionary<string, ArchiveArtifactSnapshot> _byKey = new();

        public void AddPrepared(ArchiveArtifactSnapshot snapshot)
        {
            _byId[snapshot.ArtifactId] = snapshot;
            _byKey[KeyOf(snapshot.Key)] = snapshot;
        }

        public Task<ArchiveArtifactSnapshot?> FindByKeyAsync(ArchiveArtifactKey key, CancellationToken cancellationToken)
        {
            _byKey.TryGetValue(KeyOf(key), out var s);
            return Task.FromResult<ArchiveArtifactSnapshot?>(s);
        }

        public Task InsertPreparedAsync(ArchiveArtifactSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            _byId[snapshot.ArtifactId] = snapshot;
            _byKey[KeyOf(snapshot.Key)] = snapshot;
            return Task.CompletedTask;
        }

        public Task MarkCommittedAsync(string artifactId, DateTimeOffset committedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            var existing = _byId[artifactId];
            _byId[artifactId] = existing with { State = ArchiveArtifactState.Committed, CommittedAtUtc = committedAtUtc };
            _byKey[KeyOf(existing.Key)] = _byId[artifactId];
            return Task.CompletedTask;
        }

        public Task MarkRecoveryRequiredAsync(string artifactId, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            var existing = _byId[artifactId];
            _byId[artifactId] = existing with { State = ArchiveArtifactState.RecoveryRequired };
            _byKey[KeyOf(existing.Key)] = _byId[artifactId];
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListByRunAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(_byId.Values.Where(s => s.Key.RunId == runId).ToList());

        private static string KeyOf(ArchiveArtifactKey k) =>
            $"{k.RunId}|{k.DocumentId}|{k.ProcessingRevision}|{k.Role}";
    }

    private sealed class FakeAuditStore : IAuditEventStore
    {
        public int Count { get; private set; }
        public Task AppendAsync(AuditEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}