using FluentAssertions;
using InvoiceFlowAI.App.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Events;

public sealed class AppEventBusTests
{
    [Fact]
    public void Subscriber_receives_published_event_until_disposed()
    {
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var received = new List<AppRunEvent>();
        var subscription = bus.Subscribe(received.Add);

        var first = new AppRunEvent("run.progress", "run-1", 1, DateTimeOffset.UnixEpoch, new object());
        bus.Publish(first);
        subscription.Dispose();
        bus.Publish(new AppRunEvent("run.progress", "run-1", 2, DateTimeOffset.UnixEpoch, new object()));

        received.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void Throwing_subscriber_does_not_block_other_subscribers()
    {
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var received = 0;
        bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe(_ => received++);

        bus.Publish(new AppRunEvent("run.terminal", "run-1", 3, DateTimeOffset.UnixEpoch, new object()));

        received.Should().Be(1);
    }

    [Fact]
    public void Native_publisher_maps_publisher_arguments_to_event()
    {
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        AppRunEvent? captured = null;
        bus.Subscribe(runEvent => captured = runEvent);
        var payload = new object();
        var emittedAt = DateTimeOffset.UnixEpoch;

        new NativeRunEventPublisher(bus).Publish("run.terminal", payload, "run-9", 42, emittedAt);

        captured.Should().NotBeNull();
        captured!.EventName.Should().Be("run.terminal");
        captured.RunId.Should().Be("run-9");
        captured.EventSequence.Should().Be(42);
        captured.EmittedAtUtc.Should().Be(emittedAt);
        captured.Payload.Should().BeSameAs(payload);
    }
}
