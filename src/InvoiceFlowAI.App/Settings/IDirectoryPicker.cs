using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Settings;

public interface IDirectoryPicker
{
    Task<DirectoryChooseResult> ChooseAsync(CancellationToken cancellationToken);
}