// Verifies EventReplayService from design §5:
//   * Gap detection returns null for a contiguous run of events
//   * Gap detection surfaces REPLAY_GAP with the missing sequence range
//   * ReadSliceAsync returns events in ascending sequence order
//   * ReadSliceAsync honors the afterSequence cursor

using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Runs;

public sealed class EventReplayServiceTests
{
    [Fact]
    public async Task DetectGapAsync_returns_null_for_contiguous_events()
    {
        var store = new FakeEventReplayStore();
        store.Append("run-1", 1, "run.started");
        store.Append("run-1", 2, "candidate.scanned");
        store.Append("run-1", 3, "candidate.resolved");

        var service = new EventReplayService(store);

        var gap = await service.DetectGapAsync("run-1", CancellationToken.None);

        gap.Should().BeNull();
    }

    [Fact]
    public async Task DetectGapAsync_returns_gap_when_sequence_skipped()
    {
        var store = new FakeEventReplayStore();
        store.Append("run-1", 1, "run.started");
        store.Append("run-1", 2, "candidate.scanned");
        // Sequence 3 is missing.
        store.Append("run-1", 4, "candidate.resolved");

        var service = new EventReplayService(store);

        var gap = await service.DetectGapAsync("run-1", CancellationToken.None);

        gap.Should().NotBeNull();
        gap!.MissingFromSequence.Should().Be(3);
        gap.LowerBoundSequence.Should().Be(2);
        gap.HighBoundSequence.Should().Be(4);
        gap.ReasonCode.Should().Be("REPLAY_GAP");
    }

    [Fact]
    public async Task DetectGapAsync_returns_gap_when_sequence_starts_after_one()
    {
        var store = new FakeEventReplayStore();
        store.Append("run-1", 2, "candidate.scanned");
        store.Append("run-1", 3, "candidate.resolved");

        var service = new EventReplayService(store);

        var gap = await service.DetectGapAsync("run-1", CancellationToken.None);

        gap.Should().NotBeNull();
        gap!.MissingFromSequence.Should().Be(1);
    }

    [Fact]
    public async Task DetectGapAsync_returns_null_for_empty_run()
    {
        var store = new FakeEventReplayStore();
        var service = new EventReplayService(store);

        var gap = await service.DetectGapAsync("run-empty", CancellationToken.None);

        gap.Should().BeNull();
    }

    [Fact]
    public async Task ReadSliceAsync_returns_events_in_sequence_order()
    {
        var store = new FakeEventReplayStore();
        store.Append("run-1", 1, "run.started");
        store.Append("run-1", 2, "candidate.scanned");
        store.Append("run-1", 3, "candidate.resolved");

        var service = new EventReplayService(store);

        var slice = await service.ReadSliceAsync("run-1", afterSequence: 0, limit: 10, CancellationToken.None);

        slice.Events.Select(e => e.EventSequence).Should().Equal(1, 2, 3);
        slice.LatestSequence.Should().Be(3);
        slice.RequiresFullRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task ReadSliceAsync_honors_afterSequence_cursor()
    {
        var store = new FakeEventReplayStore();
        store.Append("run-1", 1, "run.started");
        store.Append("run-1", 2, "candidate.scanned");
        store.Append("run-1", 3, "candidate.resolved");

        var service = new EventReplayService(store);

        var slice = await service.ReadSliceAsync("run-1", afterSequence: 1, limit: 10, CancellationToken.None);

        slice.Events.Select(e => e.EventSequence).Should().Equal(2, 3);
    }

    private sealed class FakeEventReplayStore : IEventReplayStore
    {
        private readonly Dictionary<string, List<StoredRunEventRecord>> _events = new();

        public void Append(string runId, long sequence, string type)
        {
            if (!_events.TryGetValue(runId, out var list))
            {
                list = new List<StoredRunEventRecord>();
                _events[runId] = list;
            }
            list.Add(new StoredRunEventRecord(runId, sequence, type, "{}", DateTimeOffset.UtcNow, null));
        }

        public Task AppendAsync(StoredRunEventRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
        {
            Append(record.RunId, record.EventSequence, record.EventType);
            return Task.CompletedTask;
        }

        public Task<EventReplayResultRecord> ReadSinceAsync(string runId, long afterSequence, int limit, CancellationToken cancellationToken)
        {
            if (!_events.TryGetValue(runId, out var list))
            {
                return Task.FromResult(new EventReplayResultRecord(Array.Empty<StoredRunEventRecord>(), LatestSequence: afterSequence, RequiresFullRefresh: false));
            }
            var page = list
                .Where(e => e.EventSequence > afterSequence)
                .OrderBy(e => e.EventSequence)
                .Take(limit)
                .ToList();
            var latest = page.Count == 0 ? afterSequence : page[^1].EventSequence;
            return Task.FromResult(new EventReplayResultRecord(page, latest, RequiresFullRefresh: false));
        }
    }
}