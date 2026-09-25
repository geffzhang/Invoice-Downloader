using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceFlowAI.Contracts.Serialization;
using InvoiceFlowAI.Contracts.Settings;

namespace InvoiceFlowAI.App.Settings;

public interface IUserSettingsSnapshotStore
{
    Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(UserSettingsSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class JsonUserSettingsSnapshotStore(string path) : IUserSettingsSnapshotStore
{
    private readonly string _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));

    public async Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return CreateDefaultSnapshot();

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<UserSettingsSnapshot>(
            stream, InvoiceJsonOptions.Strict, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Settings snapshot is empty.");
    }

    public async Task SaveAsync(UserSettingsSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, InvoiceJsonOptions.Strict, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static UserSettingsSnapshot CreateDefaultSnapshot() => new(
        Revision: 0,
        CurrentAccountId: null,
        DefaultMailbox: string.Empty,
        MailboxFilters: new MailboxFilterRules(false, null, null),
        Pipeline: new PipelineOptionsPatch(),
        AllowVisionFallback: false,
        RuleSetId: "default",
        RuleSetVersion: 1,
        ConfigurationFingerprint: Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("invoiceflowai.settings.snapshot.default.v1"))).ToLowerInvariant(),
        UpdatedAtUtc: DateTimeOffset.UnixEpoch);
}