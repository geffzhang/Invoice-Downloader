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
            SourceFileName = snapshot.SourceFileName,
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
        return rows.Select(row => ToSnapshot(row)).ToList();
    }

    public async Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListCommittedForInventoryAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.ArchivedArtifacts.AsNoTracking()
            .Where(artifact => artifact.State == "Committed")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return [];

        var documentIds = rows.Select(row => row.DocumentId).Distinct().ToArray();
        var documentNames = await _context.Documents.AsNoTracking()
            .Where(document => documentIds.Contains(document.DocumentId))
            .Select(document => new { document.DocumentId, document.SourceFileName })
            .ToDictionaryAsync(document => document.DocumentId, document => document.SourceFileName, cancellationToken)
            .ConfigureAwait(false);
        var revisions = rows.Select(row => new { row.DocumentId, row.ProcessingRevision }).Distinct().ToArray();
        var invoices = await _context.Invoices.AsNoTracking()
            .Where(invoice => documentIds.Contains(invoice.DocumentId))
            .Select(invoice => new { invoice.DocumentId, invoice.ProcessingRevision, invoice.DocumentType })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var types = invoices
            .Where(invoice => revisions.Any(revision => revision.DocumentId == invoice.DocumentId
                && revision.ProcessingRevision == invoice.ProcessingRevision))
            .GroupBy(invoice => (invoice.DocumentId, invoice.ProcessingRevision))
            .ToDictionary(group => group.Key, group => group.First().DocumentType);

        return rows.Select(row =>
        {
            documentNames.TryGetValue(row.DocumentId, out var sourceName);
            types.TryGetValue((row.DocumentId, row.ProcessingRevision), out var documentType);
            return ToSnapshot(row, row.SourceFileName ?? sourceName, documentType);
        }).ToArray();
    }

    public async Task<IReadOnlyList<ArchiveArtifactSnapshot>> ListRecoverableAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.ArchivedArtifacts.AsNoTracking()
            .Where(artifact => artifact.State == "Prepared" || artifact.State == "RecoveryRequired")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => ToSnapshot(row)).ToArray();
    }

    private static ArchiveArtifactSnapshot ToSnapshot(
        ArchivedArtifactRow row,
        string? sourceFileName = null,
        string? documentType = null) =>
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
            FinalFilePath: row.FinalFilePath ?? row.RelativePath,
            SourceFileName: sourceFileName ?? row.SourceFileName,
            DocumentType: documentType);

    private static ArchiveArtifactState ParseState(string state) => state switch
    {
        "Prepared" => ArchiveArtifactState.Prepared,
        "Committed" => ArchiveArtifactState.Committed,
        "RecoveryRequired" => ArchiveArtifactState.RecoveryRequired,
        _ => ArchiveArtifactState.Absent,
    };
}