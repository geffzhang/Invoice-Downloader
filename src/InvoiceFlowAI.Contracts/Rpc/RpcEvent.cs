namespace InvoiceFlowAI.Contracts.Rpc;

/// <summary>
/// Wire envelope for a server-pushed event. The <c>EventName</c> field is
/// named <c>event</c> on the wire per the v1 envelope contract.
/// </summary>
public sealed record RpcEvent<TPayload>(
    string Protocol,
    [property: System.Text.Json.Serialization.JsonPropertyName("event")] string EventName,
    string RunId,
    long EventSequence,
    DateTimeOffset EmittedAtUtc,
    TPayload Payload);