using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Settings;

namespace InvoiceFlowAI.App.Settings;

public sealed class LegacySettingsSnapshotImporter(
    string legacySnapshotPath,
    IUserSettingsSnapshotStore legacyStore,
    IUserSettingsStore settingsStore)
{
    public async Task ImportIfPresentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(legacySnapshotPath))
        {
            return;
        }

        UserSettingsSnapshot snapshot = await legacyStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await settingsStore.ImportSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }
}