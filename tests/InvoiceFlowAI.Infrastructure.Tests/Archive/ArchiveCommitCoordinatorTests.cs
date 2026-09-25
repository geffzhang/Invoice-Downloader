// Verifies IArchiveCommitCoordinator (design §7 / §11):
//   * Happy path: temp → DB Prepared → atomic move → DB Committed
//   * Idempotent retry with the same hash is a no-op (AlreadyExisted=true)
//   * Retry with a different content hash is rejected
//   * Hash mismatch on temp file fails before phase A commits
//   * Final hash mismatch after atomic move surfaces RecoveryRequired

using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class ArchiveCommitCoordinatorTests
{
    [Fact]
    public async Task HappyPath_writes_Prepared_then_Committed()
    {
        var fakes = new Fakes();
        await fakes.FileSystem.WriteTempAsync("temp-1.bin", "hello-archive");
        var hash = Sha256Hex("hello-archive");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var result = await coordinator.CommitAsync(NewRequest(hash, "temp-1.bin"), CancellationToken.None);

        result.State.Should().Be(ArchiveArtifactState.Committed);
        result.AlreadyExisted.Should().BeFalse();
        result.ContentHash.Should().Be(hash);
        fakes.FileSystem.FinalExists("archive/final-1.bin").Should().BeTrue();
        fakes.Store.Get(hash).State.Should().Be(ArchiveArtifactState.Committed);
        fakes.AuditStore.Count.Should().Be(2); // prepare + commit
        fakes.UowFactory.CommitCount.Should().Be(2);
    }

    [Fact]
    public async Task Commit_preserves_source_file_and_its_bytes()
    {
        var fakes = new Fakes();
        const string sourcePath = "source-1.bin";
        await fakes.FileSystem.WriteTempAsync(sourcePath, "immutable-source");
        var sourceHash = Sha256Hex("immutable-source");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        await coordinator.CommitAsync(NewRequest(sourceHash, sourcePath), CancellationToken.None);

        (await fakes.FileSystem.FileExistsAsync(sourcePath, CancellationToken.None)).Should().BeTrue();
        (await fakes.FileSystem.ComputeSha256Async(sourcePath, CancellationToken.None)).Should().Be(sourceHash);
    }

    [Fact]
    public async Task Commit_does_not_overwrite_an_existing_destination()
    {
        var fakes = new Fakes();
        await fakes.FileSystem.WriteTempAsync("source-1.bin", "new-content");
        await fakes.FileSystem.WriteTempAsync("archive/final-1.bin", "pre-existing-content");
        var existingHash = Sha256Hex("pre-existing-content");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var act = () => coordinator.CommitAsync(NewRequest(Sha256Hex("new-content"), "source-1.bin"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        (await fakes.FileSystem.ComputeSha256Async("archive/final-1.bin", CancellationToken.None)).Should().Be(existingHash);
        (await fakes.FileSystem.FileExistsAsync("source-1.bin", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Move_interruption_after_prepare_keeps_source_and_recovery_copy()
    {
        var fakes = new Fakes();
        const string sourcePath = "source-1.bin";
        await fakes.FileSystem.WriteTempAsync(sourcePath, "prepared-content");
        var hash = Sha256Hex("prepared-content");
        fakes.FileSystem.ThrowOnMove = true;
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var act = () => coordinator.CommitAsync(NewRequest(hash, sourcePath), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        var snapshot = fakes.Store.Get(hash);
        snapshot.State.Should().Be(ArchiveArtifactState.Prepared);
        snapshot.TempFilePath.Should().NotBe(sourcePath);
        (await fakes.FileSystem.FileExistsAsync(snapshot.TempFilePath, CancellationToken.None)).Should().BeTrue();
        (await fakes.FileSystem.FileExistsAsync(sourcePath, CancellationToken.None)).Should().BeTrue();
        (await fakes.FileSystem.ComputeSha256Async(sourcePath, CancellationToken.None)).Should().Be(hash);
    }

    [Fact]
    public async Task IdempotentRetry_with_same_hash_returns_AlreadyExisted()
    {
        var fakes = new Fakes();
        await fakes.FileSystem.WriteTempAsync("temp-1.bin", "hello-archive");
        var hash = Sha256Hex("hello-archive");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        await coordinator.CommitAsync(NewRequest(hash, "temp-1.bin"), CancellationToken.None);
        // Re-attempt with a brand new temp file (real systems get a fresh temp).
        await fakes.FileSystem.WriteTempAsync("temp-2.bin", "hello-archive");
        var result = await coordinator.CommitAsync(NewRequest(hash, "temp-2.bin"), CancellationToken.None);

        result.State.Should().Be(ArchiveArtifactState.Committed);
        result.AlreadyExisted.Should().BeTrue();
        fakes.UowFactory.CommitCount.Should().Be(2); // only the first attempt committed twice; second committed 0 (early return).
    }

    [Fact]
    public async Task Retry_with_different_hash_throws()
    {
        var fakes = new Fakes();
        await fakes.FileSystem.WriteTempAsync("temp-1.bin", "hello-archive");
        var hash = Sha256Hex("hello-archive");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        await coordinator.CommitAsync(NewRequest(hash, "temp-1.bin"), CancellationToken.None);
        await fakes.FileSystem.WriteTempAsync("temp-2.bin", "tampered-archive");
        var tamperedHash = Sha256Hex("tampered-archive");

        var act = () => coordinator.CommitAsync(NewRequest(tamperedHash, "temp-2.bin"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Temp_hash_mismatch_throws_before_phase_A()
    {
        var fakes = new Fakes();
        await fakes.FileSystem.WriteTempAsync("temp-1.bin", "actual-bytes");
        var wrongHash = Sha256Hex("expected-but-different");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        var act = () => coordinator.CommitAsync(NewRequest(wrongHash, "temp-1.bin"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        fakes.UowFactory.CommitCount.Should().Be(0);
        fakes.FileSystem.FinalExists("archive/final-1.bin").Should().BeFalse();
    }

    [Fact]
    public async Task Final_hash_mismatch_after_move_surfaces_RecoveryRequired()
    {
        var fakes = new Fakes();
        await fakes.FileSystem.WriteTempAsync("temp-1.bin", "hello-archive");
        var hash = Sha256Hex("hello-archive");
        var coordinator = new ArchiveCommitCoordinator(fakes.UowFactory, fakes.Store, fakes.FileSystem, fakes.AuditStore);

        // Replace the final write with tampered bytes so the final hash check fails.
        fakes.FileSystem.TamperFinalPath = "archive/final-1.bin";
        var result = await coordinator.CommitAsync(NewRequest(hash, "temp-1.bin"), CancellationToken.None);

        result.State.Should().Be(ArchiveArtifactState.RecoveryRequired);
        fakes.Store.Get(hash).State.Should().Be(ArchiveArtifactState.RecoveryRequired);
    }

    private static ArchiveCommitRequest NewRequest(string hash, string tempPath) => new(
        Key: new ArchiveArtifactKey("run-1", "doc-1", 1, "Invoice", hash),
        SourceFilePath: tempPath,
        FinalFilePath: "archive/final-1.bin",
        FinalRelativePath: "archive/final-1.bin",
        FileName: "final-1.bin");

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
            public FakeUow(FakeUowFactory factory) { _factory = factory; TransactionId = Guid.NewGuid().ToString("N"); Purpose = TransactionPurpose.ArchivePrepare; }
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
        private int _tempCounter;
        public string? TamperFinalPath { get; set; }
        public bool ThrowOnMove { get; set; }

        public Task<IReadOnlyList<string>> EnumerateDirectChildFilesAsync(string directoryPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public async Task WriteTempAsync(string path, string content)
        {
            _files[path] = System.Text.Encoding.UTF8.GetBytes(content);
            await Task.CompletedTask;
        }

        public bool FinalExists(string path) => _files.ContainsKey(path);

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

        public Task<string> CopyToSiblingTempAsync(string sourcePath, string finalFilePath, CancellationToken cancellationToken)
        {
            if (!_files.TryGetValue(sourcePath, out var sourceBytes)) throw new FileNotFoundException(sourcePath);
            var tempPath = $"{finalFilePath}.private-{++_tempCounter}";
            _files[tempPath] = sourceBytes.ToArray();
            return Task.FromResult(tempPath);
        }

        public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
        {
            if (ThrowOnMove) throw new IOException("Simulated move interruption.");
            if (!_files.TryGetValue(sourcePath, out var bytes))
            {
                throw new FileNotFoundException(sourcePath);
            }
            if (_files.ContainsKey(targetPath)) throw new IOException("Archive destination already exists.");
            _files.Remove(sourcePath);
            _files[targetPath] = bytes;
            // If the test rigged a tamper for this target, swap the contents.
            if (TamperFinalPath == targetPath)
            {
                _files[targetPath] = System.Text.Encoding.UTF8.GetBytes("TAMPERED");
            }
            return Task.CompletedTask;
        }

        public Task FlushToDiskAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeArchiveArtifactStore : IArchiveArtifactStore
    {
        private readonly Dictionary<string, ArchiveArtifactSnapshot> _byHash = new();
        private readonly List<ArchiveArtifactSnapshot> _byRun = new();

        public ArchiveArtifactSnapshot Get(string hash) => _byHash[hash];

        public Task<ArchiveArtifactSnapshot?> FindByKeyAsync(ArchiveArtifactKey key, CancellationToken cancellationToken)
        {
            foreach (var kv in _byHash)
            {
                if (kv.Value.Key.RunId == key.RunId
                    && kv.Value.Key.DocumentId == key.DocumentId
                    && kv.Value.Key.ProcessingRevision == key.ProcessingRevision
                    && kv.Value.Key.Role == key.Role)
                {
                    return Task.FromResult<ArchiveArtifactSnapshot?>(kv.Value);
                }
            }
            return Task.FromResult<ArchiveArtifactSnapshot?>(null);
        }

        public Task InsertPreparedAsync(ArchiveArtifactSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            _byHash[snapshot.ExpectedContentHash] = snapshot;
            _byRun.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task MarkCommittedAsync(string artifactId, DateTimeOffset committedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            var kvp = _byHash.First(kv => kv.Value.ArtifactId == artifactId);
            _byHash[kvp.Key] = kvp.Value with { State = ArchiveArtifactState.Committed, CommittedAtUtc = committedAtUtc };
            return Task.CompletedTask;
        }

        public Task MarkRecoveryRequiredAsync(string artifactId, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            var kvp = _byHash.First(kv => kv.Value.ArtifactId == artifactId);
            _byHash[kvp.Key] = kvp.Value with { State = ArchiveArtifactState.RecoveryRequired };
            return Task.CompletedTask;
        }

        public Task UpdateCommittedLocationAsync(string artifactId, string relativePath, string finalPath, string fileName,
            IUnitOfWork transaction, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListByRunAsync(string runId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(_byRun.Where(s => s.Key.RunId == runId).ToList());

        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListCommittedForInventoryAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(_byHash.Values.Where(s => s.State == ArchiveArtifactState.Committed).ToArray());

        public Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListRecoverableAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchiveArtifactSnapshot>>(_byHash.Values.Where(s => s.State is ArchiveArtifactState.Prepared or ArchiveArtifactState.RecoveryRequired).ToArray());
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