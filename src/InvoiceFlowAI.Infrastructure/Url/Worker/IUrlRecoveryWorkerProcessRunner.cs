namespace InvoiceFlowAI.Infrastructure.Url.Worker;

public interface IUrlRecoveryWorkerProcessRunner
{
    Task<int> RunAsync(string requestManifestPath, TimeSpan timeout, CancellationToken cancellationToken);
}