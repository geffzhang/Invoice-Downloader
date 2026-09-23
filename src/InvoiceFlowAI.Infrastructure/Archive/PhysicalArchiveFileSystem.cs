// Physical IArchiveFileSystem implementation. Wraps System.IO calls and
// uses SHA-256 streaming so a multi-megabyte PDF does not pull the whole
// file into memory at once.

using System.Security.Cryptography;
using InvoiceFlowAI.Application.Archive;

namespace InvoiceFlowAI.Infrastructure.Archive;

public sealed class PhysicalArchiveFileSystem : IArchiveFileSystem
{
    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(File.Exists(path));

    public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }
        if (File.Exists(targetPath))
        {
            // Atomic replace on Windows requires File.Move with overwrite=true,
            // which uses MoveFileEx semantics. The original file is preserved
            // until the rename completes.
            File.Replace(sourcePath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(sourcePath, targetPath);
        }
        return Task.CompletedTask;
    }

    public Task FlushToDiskAsync(string path, CancellationToken cancellationToken)
    {
        // On Windows the OS commits the rename on the volume when the file
        // handle is closed. We force a flush of the parent directory so a
        // crash after this point still sees the file.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            using var dirHandle = File.Open(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            dirHandle.Flush(true);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}