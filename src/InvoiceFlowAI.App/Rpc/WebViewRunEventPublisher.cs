using InvoiceFlowAI.Application.Runs;

namespace InvoiceFlowAI.App.Rpc;

public sealed class WebViewRunEventPublisher : IRunEventPublisher
{
    private IWebViewRpcBridge? _bridge;

    public void Attach(IWebViewRpcBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        Interlocked.Exchange(ref _bridge, bridge);
    }

    public void Detach(IWebViewRpcBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        Interlocked.CompareExchange(ref _bridge, null, bridge);
    }

    public void Publish(
        string eventName,
        object payload,
        string runId,
        long eventSequence,
        DateTimeOffset emittedAtUtc)
    {
        Volatile.Read(ref _bridge)?.PushEvent(eventName, payload, runId, eventSequence, emittedAtUtc);
    }
}