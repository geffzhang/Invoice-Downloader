using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfDocumentProcessingStore : IDocumentProcessingStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfDocumentProcessingStore(InvoiceFlowDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task UpsertAsync(
        DocumentProcessingWrite record,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("DocumentProcessingStore writes must use EfUnitOfWork.");
        }
        ArgumentException.ThrowIfNullOrEmpty(record.DocumentId);
        ArgumentException.ThrowIfNullOrEmpty(record.RunId);

        var document = await _context.Documents
            .SingleOrDefaultAsync(row => row.DocumentId == record.DocumentId, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            _context.Documents.Add(new DocumentSourceRow
            {
                DocumentId = record.DocumentId,
                SourceKind = string.IsNullOrWhiteSpace(record.SourceKind) ? "unknown" : record.SourceKind,
                SourceMessageUid = record.SourceMessageUid,
                SourceFileName = record.SourceFileName,
                SourceLocator = record.DocumentId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            // FK_DocumentProcessing_Documents is enforced at INSERT time and
            // EF batches inserts without a model relationship, so the parent
            // row must be durable before the processing row is staged.
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var row = await _context.DocumentProcessing
            .SingleOrDefaultAsync(
                candidate => candidate.DocumentId == record.DocumentId
                    && candidate.ProcessingRevision == record.ProcessingRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            var maxSequence = await _context.DocumentProcessing
                .Where(candidate => candidate.RunId == record.RunId)
                .MaxAsync(candidate => (long?)candidate.Sequence, cancellationToken)
                .ConfigureAwait(false);
            row = new DocumentProcessingRow
            {
                DocumentId = record.DocumentId,
                ProcessingRevision = record.ProcessingRevision,
                RunId = record.RunId,
                Sequence = (maxSequence ?? -1) + 1,
            };
            _context.DocumentProcessing.Add(row);
        }

        row.Stage = record.Stage;
        row.Status = record.Status.ToString();
        row.ReasonCode = record.ReasonCode;
        row.Retryable = record.Retryable;
        row.Attempt = record.Attempt;
        row.MaxAttempts = record.MaxAttempts;
        row.ArtifactPath = record.ArtifactPath;
        row.ResultJson = record.ResultJson;
        row.TraceJson = record.TraceJson;
        row.StartedAtUtc = record.StartedAtUtc;
        row.CompletedAtUtc = record.CompletedAtUtc;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // Flush now: the next candidate's MAX(Sequence)+1 runs in the same
        // transaction and would not see this row otherwise, producing a
        // duplicate (RunId, Sequence) key.
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
