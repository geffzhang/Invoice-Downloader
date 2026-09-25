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
    public Task<IReadOnlyList<string>> EnumerateDirectChildFilesAsync(string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(rootPath))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Archive inventory root cannot be a reparse point.");
        }

        var files = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(entry);
            if (!string.Equals(Path.GetDirectoryName(fullPath), rootPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                continue;
            }

            files.Add(fullPath);
        }

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

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

    public async Task WriteTextAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) throw new IOException("Archive sidecar destination already exists.");
            File.Move(tempPath, fullPath);
            await FlushToDiskAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }
}