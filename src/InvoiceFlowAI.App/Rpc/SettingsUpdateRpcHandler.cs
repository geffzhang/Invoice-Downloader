using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Settings;

namespace InvoiceFlowAI.App.Rpc;

public sealed class SettingsUpdateRpcHandler(IUserSettingsStore store)
    : TypedRpcHandler<SettingsUpdateRequest, UserSettingsSnapshot>("settings.update")
{
    protected override Task<UserSettingsSnapshot> ExecuteAsync(
        SettingsUpdateRequest parameters,
        CancellationToken cancellationToken)
        => store.UpdateAsync(parameters, cancellationToken);
}