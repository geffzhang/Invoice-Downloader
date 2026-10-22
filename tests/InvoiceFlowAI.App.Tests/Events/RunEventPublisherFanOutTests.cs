using FluentAssertions;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.Application.Runs;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Events;

public sealed class RunEventPublisherFanOutTests
{
    [Fact]
    public void Fan_out_publishes_to_every_inner_publisher()
    {
        var first = new RecordingPublisher();
        var second = new RecordingPublisher();
        var fanOut = new RunEventPublisherFanOut([first, second]);

        fanOut.Publish("run.progress", new object(), "run-1", 7, DateTimeOffset.UnixEpoch);

        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
        first.LastRunId.Should().Be("run-1");
        second.LastSequence.Should().Be(7);
    }

    private sealed class RecordingPublisher : IRunEventPublisher
    {
        public int Calls { get; private set; }
        public string? LastRunId { get; private set; }
        public long LastSequence { get; private set; }

        public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
        {
            Calls++;
            LastRunId = runId;
            LastSequence = eventSequence;
        }
    }
}
