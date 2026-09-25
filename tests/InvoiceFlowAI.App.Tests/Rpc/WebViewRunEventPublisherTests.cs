using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class WebViewRunEventPublisherTests
{
    [Fact]
    public void Publishes_committed_events_in_sequence_through_the_bridge_and_detaches_cleanly()
    {
        var channel = new RecordingChannel();
        var bridge = new WebViewRpcBridge(
            new RpcDispatcher(new BridgeHelloInfo("InvoiceFlowAI", "1.0.0", "test", "invoiceflow.rpc.v1", [])),
            channel,
            new WebViewRpcBridgeOptions());
        var publisher = new WebViewRunEventPublisher();
        publisher.Attach(bridge);

        publisher.Publish("run.progress", new { progress = 0.1 }, "run-1", 4, DateTimeOffset.UnixEpoch);
        publisher.Publish("run.candidate", new { documentId = "opaque-id" }, "run-1", 5, DateTimeOffset.UnixEpoch.AddSeconds(1));
        publisher.Detach(bridge);
        publisher.Publish("run.terminal", new { runState = "completed" }, "run-1", 6, DateTimeOffset.UnixEpoch.AddSeconds(2));

        channel.Messages.Should().HaveCount(2);
        channel.Messages.Select(message => JsonDocument.Parse(message).RootElement.GetProperty("eventSequence").GetInt64())
            .Should().Equal(4, 5);
        channel.Messages.Select(message => JsonDocument.Parse(message).RootElement.GetProperty("event").GetString())
            .Should().Equal("run.progress", "run.candidate");
        JsonDocument.Parse(channel.Messages[0]).RootElement.GetProperty("emittedAtUtc").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.UnixEpoch);
    }

    private sealed class RecordingChannel : IWebViewMessageChannel
    {
        public event Action<string>? MessageReceived { add { } remove { } }
        public List<string> Messages { get; } = [];
        public void PostMessage(string payload) => Messages.Add(payload);
    }
}
