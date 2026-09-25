using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Settings;

namespace InvoiceFlowAI.App.Rpc;

public sealed class SettingsGetRpcHandler(IUserSettingsStore store)
    : TypedRpcHandler<RpcEmptyParams, UserSettingsSnapshot>("settings.get", allowNullParams: true)
{
    protected override Task<UserSettingsSnapshot> ExecuteAsync(RpcEmptyParams parameters, CancellationToken cancellationToken)
        => store.LoadAsync(cancellationToken);
}