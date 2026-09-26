using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfCandidateSourceWriter : ICandidateSourceWriter
{
    private readonly InvoiceFlowDbContext _context;

    public EfCandidateSourceWriter(InvoiceFlowDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task UpsertSelectedArtifactAsync(
        DocumentCandidate candidate,
        string contentSha256,
        DocumentIdentity sourceGroupIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Regex.IsMatch(contentSha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Content hash must be a lowercase SHA-256 digest.", nameof(contentSha256));
        }
        if (string.IsNullOrWhiteSpace(sourceGroupIdentity.Value) || sourceGroupIdentity.Value.Length > 128)
        {
            throw new ArgumentException("Source group identity is invalid.", nameof(sourceGroupIdentity));
        }

        var row = await _context.Documents.SingleOrDefaultAsync(
            document => document.DocumentId == candidate.DocumentId.Value,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new DocumentSourceRow
            {
                DocumentId = candidate.DocumentId.Value,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            _context.Documents.Add(row);
        }

        row.SourceKind = candidate.SourceKind;
        row.SourceMessageUid = candidate.SourceMessageUid;
        row.SourceFileName = candidate.OriginalFileName;
        row.SourceLocator = candidate.DocumentId.Value;
        row.ProviderGroupKey = sourceGroupIdentity.Value;
        row.ContentHash = contentSha256;
        row.MimeType = candidate.ContentType;
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}