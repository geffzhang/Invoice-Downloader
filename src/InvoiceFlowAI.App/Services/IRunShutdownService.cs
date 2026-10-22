namespace InvoiceFlowAI.App.Services;

public enum RunCloseDecision
{
    Close,
    Cancel,
}

public interface IRunShutdownService
{
    Task<RunCloseDecision> ConfirmCloseAsync(CancellationToken cancellationToken);
}
