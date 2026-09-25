using InvoiceFlowAI.Contracts.Errors;

namespace InvoiceFlowAI.Application.Persistence;

public sealed class SettingsRevisionConflictException(int expectedRevision, int actualRevision)
    : Exception("User settings revision changed.")
{
    public int ExpectedRevision { get; } = expectedRevision;

    public int ActualRevision { get; } = actualRevision;

    public string ReasonCode => RpcErrorCodes.SettingsRevisionConflict;
}