using InvoiceFlowAI.Contracts.Settings;

namespace InvoiceFlowAI.Application.Persistence;

public interface IUserSettingsStore
{
    Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task<UserSettingsSnapshot> UpdateAsync(
        SettingsUpdateRequest request,
        CancellationToken cancellationToken);

    Task ImportSnapshotAsync(
        UserSettingsSnapshot snapshot,
        CancellationToken cancellationToken);
}