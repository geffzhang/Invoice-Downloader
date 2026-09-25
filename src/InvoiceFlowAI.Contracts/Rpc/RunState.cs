namespace InvoiceFlowAI.Contracts.Rpc;

public enum RunState
{
    Idle,
    Running,
    Stopping,
    Completed,
    Failed,
    Cancelled,
}