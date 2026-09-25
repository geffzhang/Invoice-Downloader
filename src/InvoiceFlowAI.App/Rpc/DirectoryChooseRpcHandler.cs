using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Rpc;

public sealed class DirectoryChooseRpcHandler(IDirectoryPicker picker)
    : TypedRpcHandler<RpcEmptyParams, DirectoryChooseResult>("directory.choose", allowNullParams: true)
{
    protected override Task<DirectoryChooseResult> ExecuteAsync(RpcEmptyParams parameters, CancellationToken cancellationToken)
        => picker.ChooseAsync(cancellationToken);
}