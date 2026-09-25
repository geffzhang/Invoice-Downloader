// IArchiveArtifactStore implementation. Persists ArchivedArtifacts rows
// through the caller's UoW so the Prepared / Committed transitions commit
// atomically with the surrounding audit row.

using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Archive;

public sealed class EfArchiveArtifactStore : IArchiveArtifactStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfArchiveArtifactStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<ArchiveArtifactSnapshot?> FindByKeyAsync(ArchiveArtifactKey key, CancellationToken cancellationToken)
    {
        var row = await _context.ArchivedArtifacts
            .AsNoTracking()
            .Where(a =>
                a.RunId == key.RunId &&
                a.DocumentId == key.DocumentId &&
                a.ProcessingRevision == key.ProcessingRevision &&
                a.Role == key.Role)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSnapshot(row);
    }

    public async Task InsertPreparedAsync(ArchiveArtifactSnapshot snapshot, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("ArchiveArtifactStore writes must use EfUnitOfWork.");
        }
        _context.ArchivedArtifacts.Add(new ArchivedArtifactRow
        {
            ArtifactId = snapshot.ArtifactId,
            RunId = snapshot.Key.RunId,
            DocumentId = snapshot.Key.DocumentId,
            ProcessingRevision = snapshot.Key.ProcessingRevision,
            Role = snapshot.Key.Role,
            RelativePath = snapshot.FinalRelativePath,
            TempFilePath = snapshot.TempFilePath,
            FinalFilePath = snapshot.FinalFilePath,
            FileName = snapshot.FileName,
            ContentHash = snapshot.ExpectedContentHash,
            State = "Prepared",
            AlreadyExisted = false,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            CommittedAtUtc = null,
        });
        await Task.CompletedTask;
    }

    public async Task MarkCommittedAsync(string artifactId, DateTimeOffset committedAtUtc, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("ArchiveArtifactStore writes must use EfUnitOfWork.");
        }
        var row = await _context.ArchivedArtifacts
            .FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException($"ArchivedArtifact '{artifactId}' not found.");
        }
        row.State = "Committed";
        row.CommittedAtUtc = committedAtUtc;
        await Task.CompletedTask;
    }

    public async Task MarkRecoveryRequiredAsync(string artifactId, string reasonCode, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("ArchiveArtifactStore writes must use EfUnitOfWork.");
        }
        var row = await _context.ArchivedArtifacts
            .FirstOrDefaultAsync(a => a.ArtifactId == artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException($"ArchivedArtifact '{artifactId}' not found.");
        }
        row.State = "RecoveryRequired";
        await Task.CompletedTask;
    }

    public async Task UpdateCommittedLocationAsync(
        string artifactId,
        string relativePath,
        string finalPath,
        string fileName,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("ArchiveArtifactStore writes must use EfUnitOfWork.");
        }
        var row = await _context.ArchivedArtifacts
            .FirstOrDefaultAsync(artifact => artifact.ArtifactId == artifactId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.State != "Committed")
        {
            throw new InvalidOperationException("Only a committed archive artifact can change location.");
        }
        row.RelativePath = relativePath;
        row.FinalFilePath = finalPath;
        row.FileName = fileName;
    }

    public async Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListByRunAsync(string runId, CancellationToken cancellationToken)
    {
        var rows = await _context.ArchivedArtifacts
            .AsNoTracking()
            .Where(a => a.RunId == runId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToSnapshot).ToList();
    }

    private static ArchiveArtifactSnapshot ToSnapshot(ArchivedArtifactRow row) =>
        new(
            ArtifactId: row.ArtifactId,
            Key: new ArchiveArtifactKey(row.RunId, row.DocumentId, row.ProcessingRevision, row.Role, row.ContentHash),
            TempFilePath: row.TempFilePath ?? string.Empty,
            FinalRelativePath: row.RelativePath,
            FileName: row.FileName,
            ExpectedContentHash: row.ContentHash,
            State: ParseState(row.State),
            CreatedAtUtc: row.CreatedAtUtc,
            CommittedAtUtc: row.CommittedAtUtc,
            FinalFilePath: row.FinalFilePath ?? row.RelativePath);

    private static ArchiveArtifactState ParseState(string state) => state switch
    {
        "Prepared" => ArchiveArtifactState.Prepared,
        "Committed" => ArchiveArtifactState.Committed,
        "RecoveryRequired" => ArchiveArtifactState.RecoveryRequired,
        _ => ArchiveArtifactState.Absent,
    };
}