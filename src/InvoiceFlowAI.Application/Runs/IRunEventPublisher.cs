namespace InvoiceFlowAI.Application.Runs;

public interface IRunEventPublisher
{
    void Publish(
        string eventName,
        object payload,
        string runId,
        long eventSequence,
        DateTimeOffset emittedAtUtc);
}