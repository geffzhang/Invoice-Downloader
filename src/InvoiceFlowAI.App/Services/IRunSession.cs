namespace InvoiceFlowAI.App.Services;

public interface IRunSession
{
    string? RunId { get; }
    void SetRunId(string? runId);
}

public sealed class RunSession : IRunSession
{
    public string? RunId { get; private set; }
    public void SetRunId(string? runId) => RunId = string.IsNullOrWhiteSpace(runId) ? null : runId;
}
