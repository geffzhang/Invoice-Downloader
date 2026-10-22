using InvoiceFlowAI.Application.Runs;

namespace InvoiceFlowAI.App.Events;

public sealed class NativeRunEventPublisher(IAppEventBus bus) : IRunEventPublisher
{
    public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
        => bus.Publish(new AppRunEvent(eventName, runId, eventSequence, emittedAtUtc, payload));
}
