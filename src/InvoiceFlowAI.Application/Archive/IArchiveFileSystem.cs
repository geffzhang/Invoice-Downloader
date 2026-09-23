// Application abstraction for the archive filesystem. Exists so the
// coordinator can be tested without touching real disks: production
// binds a thin wrapper around System.IO; tests inject a fake that records
// or simulates crashes at each phase.

namespace InvoiceFlowAI.Application.Archive;

public interface IArchiveFileSystem
{
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken);

    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken);

    Task AtomicMoveAsync(string sourcePath, string targetPath, CancellationToken cancellationToken);

    Task FlushToDiskAsync(string path, CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}