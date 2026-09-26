using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfPairingStore : IPairingStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfPairingStore(InvoiceFlowDbContext context) => _context = context;

    public async Task UpsertAsync(PairingRecord record, IUnitOfWork transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("PairingStore writes must use EfUnitOfWork.");
        }
        ArgumentException.ThrowIfNullOrEmpty(record.RunId);
        ArgumentException.ThrowIfNullOrEmpty(record.InvoiceDocumentId);
        ArgumentOutOfRangeException.ThrowIfNegative(record.InvoiceProcessingRevision);
        ArgumentException.ThrowIfNullOrEmpty(record.State);
        ArgumentNullException.ThrowIfNull(record.Companions);
        if (record.Companions.Count == 0 || record.Companions.Any(companion =>
                string.IsNullOrWhiteSpace(companion.DocumentId) || companion.ProcessingRevision < 0))
        {
            throw new ArgumentException("A pairing must contain valid companion documents.", nameof(record));
        }
        if (record.Companions.Select(companion => companion.DocumentId).Distinct(StringComparer.Ordinal).Count() != record.Companions.Count)
        {
            throw new ArgumentException("A pairing cannot contain duplicate companion documents.", nameof(record));
        }

        var existing = await _context.Pairings.FirstOrDefaultAsync(row =>
            row.RunId == record.RunId
            && row.InvoiceDocumentId == record.InvoiceDocumentId
            && row.InvoiceProcessingRevision == record.InvoiceProcessingRevision,
            cancellationToken).ConfigureAwait(false);
        var orderedCompanions = record.Companions.OrderBy(companion => companion.DocumentId, StringComparer.Ordinal).ToArray();
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            existing = new PairingRow
            {
                PairingId = CreatePairingId(record.RunId, record.InvoiceDocumentId, record.InvoiceProcessingRevision),
                RunId = record.RunId,
                InvoiceDocumentId = record.InvoiceDocumentId,
                InvoiceProcessingRevision = record.InvoiceProcessingRevision,
                CreatedAtUtc = now,
            };
            _context.Pairings.Add(existing);
        }

        existing.CompanionDocumentIdsJson = JsonSerializer.Serialize(orderedCompanions.Select(companion => companion.DocumentId));
        existing.CompanionProcessingRevisionsJson = JsonSerializer.Serialize(orderedCompanions.Select(companion => companion.ProcessingRevision));
        existing.Score = record.TotalScore.ToString(CultureInfo.InvariantCulture);
        existing.State = record.State;
        existing.ReasonCode = record.ReasonCode ?? string.Empty;
        existing.UpdatedAtUtc = now;
    }

    public async Task ReconcileArchiveStateAsync(
        string runId,
        IReadOnlyList<ArchiveArtifactSnapshot> artifacts,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("PairingStore writes must use EfUnitOfWork.");
        }

        var rows = await _context.Pairings
            .Where(row => row.RunId == runId && (row.State == "Prepared" || row.State == "RecoveryRequired"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            var reason = GetReconciliationReason(row, runId, artifacts);
            row.State = reason is null ? "Committed" : "RecoveryRequired";
            row.ReasonCode = reason ?? string.Empty;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private static string? GetReconciliationReason(
        PairingRow row,
        string runId,
        IReadOnlyList<ArchiveArtifactSnapshot> artifacts)
    {
        List<string>? companionIds;
        List<int>? companionRevisions;
        try
        {
            companionIds = JsonSerializer.Deserialize<List<string>>(row.CompanionDocumentIdsJson);
            companionRevisions = JsonSerializer.Deserialize<List<int>>(row.CompanionProcessingRevisionsJson);
        }
        catch (JsonException)
        {
            return "PAIR_ARCHIVE_METADATA_INVALID";
        }

        if (companionIds is null || companionRevisions is null || companionIds.Count == 0 || companionIds.Count != companionRevisions.Count)
        {
            return "PAIR_ARCHIVE_METADATA_INVALID";
        }

        var members = new List<(string DocumentId, int Revision)>
        {
            (row.InvoiceDocumentId, row.InvoiceProcessingRevision),
        };
        members.AddRange(companionIds.Zip(companionRevisions, (id, revision) => (id, revision)));

        foreach (var member in members)
        {
            var evidence = artifacts.Where(artifact =>
                artifact.Key.RunId == runId
                && artifact.Key.DocumentId == member.DocumentId
                && artifact.Key.ProcessingRevision == member.Revision).ToArray();
            if (evidence.Length == 0)
            {
                return "PAIR_ARCHIVE_MEMBER_EVIDENCE_MISSING";
            }
            if (evidence.Any(artifact => artifact.State == ArchiveArtifactState.RecoveryRequired))
            {
                return "PAIR_ARCHIVE_MEMBER_RECOVERY_REQUIRED";
            }
            if (evidence.Any(artifact => artifact.State != ArchiveArtifactState.Committed))
            {
                return "PAIR_ARCHIVE_MEMBER_PREPARED";
            }
        }

        return null;
    }

    private static string CreatePairingId(string runId, string invoiceDocumentId, int revision)
    {
        var canonicalKey = $"{runId}\n{invoiceDocumentId}\n{revision.ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey))).ToLowerInvariant();
    }
}