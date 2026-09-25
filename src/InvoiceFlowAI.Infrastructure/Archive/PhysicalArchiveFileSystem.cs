// Physical IArchiveFileSystem implementation. Wraps System.IO calls and
// uses SHA-256 streaming so a multi-megabyte PDF does not pull the whole
// file into memory at once.

using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
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

    public async Task<string> CopyToSiblingTempAsync(string sourcePath, string finalFilePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(finalFilePath);
        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(finalFilePath))!;
        Directory.CreateDirectory(targetDirectory);
        var tempPath = Path.Combine(targetDirectory, $".{Path.GetFileName(finalFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return tempPath;
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    public Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }
        if (File.Exists(targetPath))
        {
            throw new IOException("Archive destination already exists.");
        }
        File.Move(sourcePath, targetPath);
        return Task.CompletedTask;
    }

    public Task FlushToDiskAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Windows commits the rename on the volume when the file handle is closed.
        if (OperatingSystem.IsWindows()) return Task.CompletedTask;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            var descriptor = NativeOpen(directory, 0);
            if (descriptor < 0) throw new IOException("Could not open the archive directory for synchronization.", new Win32Exception(Marshal.GetLastPInvokeError()));
            try
            {
                if (NativeFsync(descriptor) != 0)
                {
                    throw new IOException("Could not synchronize the archive directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
            }
            finally
            {
                NativeClose(descriptor);
            }
        }
        return Task.CompletedTask;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int fileDescriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int fileDescriptor);

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}