// JSON-RPC dispatcher. Handlers are registered at startup and invoked
// on a background thread; the response is posted back to the WebView
// on the UI thread by the bridge. Unknown methods (including the old
// pywebview.api.* surface) are rejected before handler lookup so a
// malicious page cannot reach any handler. The handshake (bridge.hello)
// is the only method the dispatcher answers on its own; everything
// else routes through DI-registered handlers.

using System.Text.Json;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public interface IRpcDispatcher
{
    Task<RpcResponse<JsonElement?>> DispatchAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken);
    void RegisterHandler(string method, IRpcHandler handler);
    IReadOnlyCollection<string> RegisteredMethods { get; }
}

public interface IRpcHandler
{
    string Method { get; }
    Task<RpcHandlerResult> HandleAsync(RpcRequest<JsonElement?> request, CancellationToken cancellationToken);
}

public sealed record RpcHandlerResult(JsonElement? Result, RpcError? Error);

public sealed class RpcDispatcher : IRpcDispatcher
{
    public const string Protocol = "invoiceflow.rpc.v1";
    public const string HelloMethod = "bridge.hello";
    public const string UnknownMethodCode = "RPC_METHOD_UNKNOWN";
    public const string ProtocolMismatchCode = "RPC_PROTOCOL_MISMATCH";
    public const string BadRequestCode = "RPC_BAD_REQUEST";
    public const string InternalErrorCode = "RPC_INTERNAL_ERROR";
    public const string TimeoutCode = "RPC_TIMEOUT";
    public const string CancelledCode = "RPC_CANCELLED";

    private readonly Dictionary<string, IRpcHandler> _handlers = new(StringComparer.Ordinal);
    private readonly BridgeHelloInfo _hello;

    public RpcDispatcher(BridgeHelloInfo hello)
    {
        _hello = hello;
    }

    public IReadOnlyCollection<string> RegisteredMethods => _handlers.Keys.Concat(new[] { HelloMethod }).ToList();

    public void RegisterHandler(string method, IRpcHandler handler)
    {
        if (string.IsNullOrEmpty(method)) throw new ArgumentException("Method required", nameof(method));
        if (_handlers.ContainsKey(method) || string.Equals(method, HelloMethod, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"RPC method already registered: {method}");
        }
        _handlers[method] = handler;
    }

    public async Task<RpcResponse<JsonElement?>> DispatchAsync(
        RpcRequest<JsonElement?> request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("", BadRequestCode, "request is null");
        }
        if (!string.Equals(request.Protocol, Protocol, StringComparison.Ordinal))
        {
            return Error(request.Id, ProtocolMismatchCode, $"protocol {request.Protocol} not supported");
        }
        if (string.IsNullOrEmpty(request.Id))
        {
            return BadRequest("", BadRequestCode, "request.id required");
        }
        if (string.IsNullOrEmpty(request.Method))
        {
            return Error(request.Id, BadRequestCode, "request.method required");
        }
        if (request.Method.StartsWith("pywebview.api.", StringComparison.Ordinal))
        {
            return Error(request.Id, UnknownMethodCode, $"method {request.Method} is not part of the v1 surface");
        }

        if (string.Equals(request.Method, HelloMethod, StringComparison.Ordinal))
        {
            var helloJson = JsonSerializer.SerializeToElement(_hello, JsonOptions.Default);
            return new RpcResponse<JsonElement?>(Protocol, request.Id, true, helloJson, null);
        }

        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            return Error(request.Id, UnknownMethodCode, $"method {request.Method} not registered");
        }

        try
        {
            var result = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                return new RpcResponse<JsonElement?>(Protocol, request.Id, false, null, result.Error);
            }
            return new RpcResponse<JsonElement?>(Protocol, request.Id, true, result.Result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(request.Id, CancelledCode, "request cancelled");
        }
        catch (TimeoutException)
        {
            return Error(request.Id, TimeoutCode, "request timed out");
        }
        catch (Exception ex)
        {
            // Never leak exception text. Log the full exception with
            // fingerprint, surface a stable reason code only.
            return Error(request.Id, InternalErrorCode, $"internal error: {ex.GetType().Name}");
        }
    }

    private static RpcResponse<JsonElement?> Error(string id, string code, string userMessage) =>
        new(Protocol, id, false, null, new RpcError(code, "rpc", false, userMessage, false));

    private static RpcResponse<JsonElement?> BadRequest(string id, string code, string userMessage) =>
        new(Protocol, id, false, null, new RpcError(code, "rpc", false, userMessage, false));
}

public sealed record BridgeHelloInfo(
    string AppName,
    string AppVersion,
    string BackendKind,
    string Protocol,
    string[] RegisteredMethods);
