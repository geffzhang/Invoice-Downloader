// Verifies WebViewRpcBridge (design §6 / Task 9):
//   * Incoming JSON is dispatched and the response is posted back.
//   * Events are serialised with the v1 envelope shape.
//   * Malformed JSON from the page does not crash the bridge.
//   * The bridge enforces a per-request timeout (configured via options).

using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class WebViewRpcBridgeTests
{
    [Fact]
    public void OnMessage_dispatches_incoming_request_and_posts_response()
    {
        var harness = new Harness();
        harness.Bridge.Start();

        harness.Bridge.OnMessage(NewHelloRequestJson());

        harness.Channel.Sent.Should().ContainSingle();
        var response = JsonDocument.Parse(harness.Channel.Sent[0]).RootElement;
        response.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.GetProperty("result").GetProperty("appName").GetString().Should().Be("InvoiceFlowAI");
    }

    [Fact]
    public void OnMessage_with_malformed_json_does_not_throw()
    {
        var harness = new Harness();
        harness.Bridge.Start();

        var act = () => harness.Bridge.OnMessage("{not valid json");
        act.Should().NotThrow();
        harness.Channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public void OnMessage_with_null_payload_does_not_throw()
    {
        var harness = new Harness();
        harness.Bridge.Start();

        var act = () => harness.Bridge.OnMessage("");
        act.Should().NotThrow();
        harness.Channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public void PushEvent_emits_v1_envelope_with_event_field()
    {
        var harness = new Harness();
        harness.Bridge.Start();

        harness.Bridge.PushEvent("run.progress", new { progress = 0.42 }, "run-1", 7,
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

        harness.Channel.Sent.Should().ContainSingle();
        var envelope = JsonDocument.Parse(harness.Channel.Sent[0]).RootElement;
        envelope.GetProperty("protocol").GetString().Should().Be("invoiceflow.rpc.v1");
        envelope.GetProperty("event").GetString().Should().Be("run.progress");
        envelope.GetProperty("runId").GetString().Should().Be("run-1");
        envelope.GetProperty("eventSequence").GetInt64().Should().Be(7);
        envelope.GetProperty("payload").GetProperty("progress").GetDouble().Should().BeApproximately(0.42, 0.0001);
    }

    [Fact]
    public void Start_then_Stop_disconnect_subscription()
    {
        var harness = new Harness();
        harness.Bridge.Start();
        harness.Bridge.Stop();

        // After Stop the channel's MessageReceived event is unsubscribed,
        // so a real page message would no longer be dispatched.
        harness.Channel.RaiseMessageReceived(NewHelloRequestJson());

        harness.Channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_timeout_maps_to_RPC_TIMEOUT()
    {
        var harness = new Harness(new WebViewRpcBridgeOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) });
        harness.Dispatcher.RegisterHandler("slow", new SlowHandler());
        harness.Bridge.Start();

        harness.Bridge.OnMessage(NewRequestJson("slow"));

        await WaitFor(() => harness.Channel.Sent.Count > 0, TimeSpan.FromSeconds(2));
        var response = JsonDocument.Parse(harness.Channel.Sent[0]).RootElement;
        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("error").GetProperty("code").GetString().Should().Be("RPC_TIMEOUT");
    }

    [Fact]
    public async Task Unexpected_dispatch_failure_maps_to_RPC_INTERNAL_ERROR()
    {
        var channel = new RecordingChannel();
        var bridge = new WebViewRpcBridge(
            new ThrowingDispatcher(), channel, new WebViewRpcBridgeOptions());
        bridge.Start();

        bridge.OnMessage(NewRequestJson("explode"));

        await WaitFor(() => channel.Sent.Count > 0, TimeSpan.FromSeconds(2));
        var response = JsonDocument.Parse(channel.Sent[0]).RootElement;
        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("error").GetProperty("code").GetString().Should().Be("RPC_INTERNAL_ERROR");
        response.GetRawText().Should().NotContain("private dispatcher detail");
    }

    private static string NewHelloRequestJson() => JsonSerializer.Serialize(
        new { protocol = "invoiceflow.rpc.v1", id = "req-1", method = "bridge.hello", @params = (object?)null },
        JsonOptions.Default);

    private static string NewRequestJson(string method) => JsonSerializer.Serialize(
        new { protocol = "invoiceflow.rpc.v1", id = $"req-{method}", method, @params = (object?)null },
        JsonOptions.Default);

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (!condition() && DateTimeOffset.UtcNow - start < timeout)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed class Harness
    {
        public Harness(IWebViewRpcBridgeOptions? options = null)
        {
            Channel = new RecordingChannel();
            Dispatcher = new RpcDispatcher(new BridgeHelloInfo(
                "InvoiceFlowAI", "1.0.0", "WebView2", "invoiceflow.rpc.v1", new[] { "bridge.hello" }));
            Bridge = new WebViewRpcBridge(Dispatcher, Channel, options ?? new WebViewRpcBridgeOptions());
        }
        public RecordingChannel Channel { get; }
        public RpcDispatcher Dispatcher { get; }
        public WebViewRpcBridge Bridge { get; }
    }

    private sealed class RecordingChannel : IWebViewMessageChannel
    {
        private readonly List<string> _sent = new();
        public event Action<string>? MessageReceived;
        public IReadOnlyList<string> Sent => _sent;
        public void PostMessage(string payload) => _sent.Add(payload);
        public void RaiseMessageReceived(string payload) => MessageReceived?.Invoke(payload);
    }

    private sealed class SlowHandler : IRpcHandler
    {
        public string Method => "slow";
        public async Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return new RpcHandlerResult(JsonDocument.Parse("{}").RootElement, null);
        }
    }

    private sealed class ThrowingDispatcher : IRpcDispatcher
    {
        public IReadOnlyCollection<string> RegisteredMethods => Array.Empty<string>();
        public void RegisterHandler(string method, IRpcHandler handler) => throw new NotSupportedException();
        public Task<RpcResponse<JsonElement?>> DispatchAsync(
            RpcRequest<JsonElement?> request,
            CancellationToken cancellationToken)
            => Task.FromException<RpcResponse<JsonElement?>>(new InvalidOperationException("private dispatcher detail"));
    }
}
