namespace InvoiceFlowAI.Contracts.Rpc;

public enum SecretRetention
{
    Persistent,
    Session,
}

public sealed record SecretSetRequest(
    string Name,
    string Value,
    SecretRetention Retention);