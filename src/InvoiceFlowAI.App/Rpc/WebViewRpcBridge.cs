// WebView ↔ RpcDispatcher bridge. The bridge is the only component that
// knows the WebView transport exists — handlers stay in the Application
// layer and never see a WebView2 type. IWebViewMessageChannel is the
// thin abstraction the tests use to drive the bridge without spinning
// up a real WebView2 instance.

using System.Text.Json;
using System.Diagnostics;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public interface IWebViewMessageChannel
{
    event Action<string>? MessageReceived;
    void PostMessage(string payload);
}

public interface IWebViewRpcBridge
{
    void Start();
    void Stop();
    void OnMessage(string payload);
    void PushEvent(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc);
}

public sealed class WebViewRpcBridge : IWebViewRpcBridge
{
    private readonly IRpcDispatcher _dispatcher;
    private readonly IWebViewMessageChannel _channel;
    private readonly IWebViewRpcBridgeOptions _options;
    private bool _started;

    public WebViewRpcBridge(IRpcDispatcher dispatcher, IWebViewMessageChannel channel, IWebViewRpcBridgeOptions options)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public void Start()
    {
        if (_started) return;
        _channel.MessageReceived += OnMessage;
        _started = true;
    }

    public void Stop()
    {
        if (!_started) return;
        _channel.MessageReceived -= OnMessage;
        _started = false;
    }

    public void OnMessage(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        RpcRequest<JsonElement?>? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest<JsonElement?>>(payload, JsonOptions.Default);
        }
        catch (JsonException)
        {
            // Malformed message from the page — drop without crashing the
            // bridge. The page should never send non-JSON; if it does we
            // ignore rather than echo garbage back.
            return;
        }
        if (request is null) return;

        _ = DispatchAndPostAsync(request);
    }

    public void PushEvent(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        var envelope = new
        {
            protocol = "invoiceflow.rpc.v1",
            @event = eventName,
            runId,
            eventSequence,
            emittedAtUtc = emittedAtUtc.UtcDateTime,
            payload,
        };
        _channel.PostMessage(JsonSerializer.Serialize(envelope, JsonOptions.Default));
    }

    private async Task DispatchAndPostAsync(RpcRequest<JsonElement?> request)
    {
        // The bridge enforces a per-request timeout independent of the
        // caller's cancellation token. We race the dispatcher against a
        // Task.Delay so that a timeout produces TimeoutException, which
        // the dispatcher maps to RPC_TIMEOUT (vs RPC_CANCELLED for
        // user-driven cancellation).
        using var cts = new CancellationTokenSource();
        var phase = "dispatch";
        try
        {
            var dispatchTask = _dispatcher.DispatchAsync(request, cts.Token);
            var timeoutTask = Task.Delay(_options.RequestTimeout, CancellationToken.None);
            var completed = await Task.WhenAny(dispatchTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                cts.Cancel();
                throw new TimeoutException($"RPC {request.Method} exceeded {_options.RequestTimeout}");
            }
            var response = await dispatchTask.ConfigureAwait(false);
            phase = "serialize response";
            var json = JsonSerializer.Serialize(response, JsonOptions.Default);
            phase = "post response";
            _channel.PostMessage(json);
        }
        catch (TimeoutException)
        {
            var response = new RpcResponse<JsonElement?>(
                "invoiceflow.rpc.v1", request.Id, false, null,
                new RpcError("RPC_TIMEOUT", "rpc", true, "request timed out", false));
            _channel.PostMessage(JsonSerializer.Serialize(response, JsonOptions.Default));
        }
        catch (Exception exception)
        {
            Trace.TraceError("RPC {0} bridge failed during {1} with {2}.", request.Method, phase, exception.GetType().Name);
            var response = new RpcResponse<JsonElement?>(
                "invoiceflow.rpc.v1", request.Id, false, null,
                new RpcError(RpcDispatcher.InternalErrorCode, "rpc", false, "internal error", false));
            _channel.PostMessage(JsonSerializer.Serialize(response, JsonOptions.Default));
        }
    }
}

public interface IWebViewRpcBridgeOptions
{
    TimeSpan RequestTimeout { get; }
}

public sealed class WebViewRpcBridgeOptions : IWebViewRpcBridgeOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
