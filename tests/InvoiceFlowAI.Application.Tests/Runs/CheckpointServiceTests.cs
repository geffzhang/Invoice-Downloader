// Verifies CheckpointService from design §5:
//   * RecordAsync writes the checkpoint and commits the UoW
//   * Repeat RecordAsync increments CheckpointRevision monotonically
//   * LatestAsync returns null when no checkpoint exists
//   * ExceedsSnapshotThresholdAsync returns true on first packet, then
//     once every Nth packet

using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class CheckpointServiceTests
{
    [Fact]
    public async Task RecordAsync_writes_checkpoint_and_commits_uow()
    {
        var store = new FakeCheckpointStore();
        var uowFactory = new FakeUowFactory();
        var service = new CheckpointService(store, uowFactory);

        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox",
            lastCommittedSequence: 1, outputCount: 3, CancellationToken.None);

        var snapshot = await service.LatestAsync("run-1", "scan-mailbox", CancellationToken.None);
        snapshot.Should().NotBeNull();
        snapshot!.LastCommittedSequence.Should().Be(1);
        snapshot.CheckpointRevision.Should().Be(1);
        uowFactory.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_increments_checkpoint_revision_monotonically()
    {
        var store = new FakeCheckpointStore();
        var uowFactory = new FakeUowFactory();
        var service = new CheckpointService(store, uowFactory);

        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox", 1, 0, CancellationToken.None);
        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox", 2, 0, CancellationToken.None);
        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox", 3, 0, CancellationToken.None);

        var snapshot = await service.LatestAsync("run-1", "scan-mailbox", CancellationToken.None);
        snapshot!.CheckpointRevision.Should().Be(3);
        snapshot.LastCommittedSequence.Should().Be(3);
    }

    [Fact]
    public async Task LatestAsync_returns_null_when_checkpoint_absent()
    {
        var store = new FakeCheckpointStore();
        var uowFactory = new FakeUowFactory();
        var service = new CheckpointService(store, uowFactory);

        var snapshot = await service.LatestAsync("run-missing", "scan-mailbox", CancellationToken.None);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task ExceedsSnapshotThresholdAsync_returns_true_when_no_checkpoint()
    {
        var store = new FakeCheckpointStore();
        var uowFactory = new FakeUowFactory();
        var service = new CheckpointService(store, uowFactory);

        var exceeded = await service.ExceedsSnapshotThresholdAsync("run-1", "scan-mailbox", 10, CancellationToken.None);

        exceeded.Should().BeTrue();
    }

    [Fact]
    public async Task ExceedsSnapshotThresholdAsync_returns_true_every_nth_revision()
    {
        var store = new FakeCheckpointStore();
        var uowFactory = new FakeUowFactory();
        var service = new CheckpointService(store, uowFactory);

        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox", 1, 0, CancellationToken.None);
        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox", 2, 0, CancellationToken.None);
        await service.RecordAsync("run-1", "scan-mailbox", "scan-mailbox", 3, 0, CancellationToken.None);

        var at3 = await service.ExceedsSnapshotThresholdAsync("run-1", "scan-mailbox", 3, CancellationToken.None);
        var at5 = await service.ExceedsSnapshotThresholdAsync("run-1", "scan-mailbox", 5, CancellationToken.None);

        at3.Should().BeTrue();
        at5.Should().BeFalse();
    }

    private sealed class FakeCheckpointStore : IRunCheckpointStore
    {
        private readonly Dictionary<(string RunId, string NodeId), RunCheckpointSnapshot> _store = new();

        public Task<RunCheckpointSnapshot?> ReadAsync(string runId, string nodeId, CancellationToken cancellationToken)
        {
            _store.TryGetValue((runId, nodeId), out var snapshot);
            return Task.FromResult<RunCheckpointSnapshot?>(snapshot);
        }

        public Task WriteAsync(RunCheckpointSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            _store[(snapshot.RunId, snapshot.NodeId)] = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunCheckpointSnapshot>> ListAsync(string runId, CancellationToken cancellationToken)
        {
            IReadOnlyList<RunCheckpointSnapshot> list = _store
                .Where(kv => kv.Key.RunId == runId)
                .Select(kv => kv.Value)
                .ToList();
            return Task.FromResult(list);
        }
    }

    private sealed class FakeUowFactory : IUnitOfWorkFactory
    {
        public int CommitCount { get; private set; }

        public Task<IUnitOfWork> BeginAsync(TransactionPurpose purpose, CancellationToken cancellationToken)
        {
            IUnitOfWork uow = new FakeUow(this);
            return Task.FromResult(uow);
        }

        private sealed class FakeUow : IUnitOfWork
        {
            private readonly FakeUowFactory _factory;
            public FakeUow(FakeUowFactory factory)
            {
                _factory = factory;
                TransactionId = Guid.NewGuid().ToString("N");
                Purpose = TransactionPurpose.PacketCommit;
            }

            public string TransactionId { get; }
            public TransactionPurpose Purpose { get; }
            public bool IsCompleted { get; private set; }

            public Task CommitAsync(CancellationToken cancellationToken)
            {
                IsCompleted = true;
                _factory.CommitCount++;
                return Task.CompletedTask;
            }

            public Task RollbackAsync(CancellationToken cancellationToken)
            {
                IsCompleted = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}