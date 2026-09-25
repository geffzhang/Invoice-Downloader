using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Pairing;

public sealed class ArtifactPairingStage : IArtifactPairingStage
{
    private static readonly Regex MerchantTokenPattern = new(
        "[A-Za-z0-9]+|[\\u4e00-\\u9fff]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IPairingEngine _pairingEngine;

    public ArtifactPairingStage(IPairingEngine pairingEngine)
    {
        _pairingEngine = pairingEngine ?? throw new ArgumentNullException(nameof(pairingEngine));
    }

    public Task<PairingBatch> ExecuteAsync(ExtractionBatch input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var candidateResults = input.Results.Concat(input.EffectivePreflightResults).ToArray();
        var eligible = candidateResults
            .Select((result, index) => TryProject(result, index))
            .Where(static projection => projection is not null)
            .Select(static projection => projection!)
            .ToArray();
        var pairingResults = new List<PairingResult>();
        var reviewReasons = new Dictionary<string, PairingDocumentReviewReason>(StringComparer.Ordinal);

        foreach (var group in eligible
            .GroupBy(static projection => (projection.Mailbox, projection.Family))
            .OrderBy(static group => group.Key.Mailbox, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Family))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documents = group.ToArray();
            var result = _pairingEngine.Pair(
                group.Key.Family,
                documents.Where(static item => IsInvoiceRole(item.Document.Role)).Select(static item => item.Document).ToArray(),
                documents.Where(static item => !IsInvoiceRole(item.Document.Role)).Select(static item => item.Document).ToArray());
            pairingResults.Add(result);

            foreach (var reason in result.ReviewReasons)
            {
                reviewReasons[reason.DocumentId] = reason;
            }
        }

        foreach (var projection in eligible)
        {
            if (!reviewReasons.TryGetValue(projection.Document.Id, out var reason))
            {
                continue;
            }

            candidateResults[projection.ResultIndex] = candidateResults[projection.ResultIndex] with
            {
                Status = CandidateStatus.ManualReview,
                Failure = BuildReviewFailure(reason),
            };
        }

        return Task.FromResult(new PairingBatch(pairingResults, candidateResults));
    }

    private static PairingProjection? TryProject(CandidateProcessResult result, int resultIndex)
    {
        if (result.Status != CandidateStatus.Resolved || result.Invoice is null)
        {
            return null;
        }

        PairingFamily family;
        PairingRole role;
        switch (result.Invoice.DocumentType)
        {
            case InvoiceDocumentType.RideInvoice:
                family = PairingFamily.Ride;
                role = PairingRole.RideInvoice;
                break;
            case InvoiceDocumentType.RideItinerary:
                family = PairingFamily.Ride;
                role = PairingRole.RideItinerary;
                break;
            case InvoiceDocumentType.HotelInvoice:
                family = PairingFamily.Hotel;
                role = PairingRole.HotelInvoice;
                break;
            case InvoiceDocumentType.HotelFolio:
                family = PairingFamily.Hotel;
                role = PairingRole.HotelFolio;
                break;
            default:
                return null;
        }

        var invoice = result.Invoice;
        var provider = GetMetadata(result.Candidate, "provider");
        if (provider.Length == 0)
        {
            provider = GetMetadata(result.Candidate, "provider_family");
        }

        var merchantTokens = MerchantTokenPattern.Matches(invoice.Seller ?? string.Empty)
            .Select(static match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static token => token, StringComparer.Ordinal)
            .ToArray();
        var document = new PairingDocument(
            result.Candidate.DocumentId.Value,
            role,
            invoice.TotalAmount ?? invoice.Amount,
            invoice.InvoiceDate,
            provider,
            merchantTokens,
            result.Candidate.SourceMessageUid,
            result.ArtifactPath);

        return new PairingProjection(
            resultIndex,
            GetMetadata(result.Candidate, "mailbox"),
            family,
            document);
    }

    private static string GetMetadata(DocumentCandidate candidate, string key)
        => candidate.Metadata is not null && candidate.Metadata.TryGetValue(key, out var value)
            ? value.Trim()
            : string.Empty;

    private static bool IsInvoiceRole(PairingRole role)
        => role is PairingRole.RideInvoice or PairingRole.HotelInvoice;

    private static CandidateFailure BuildReviewFailure(PairingDocumentReviewReason reason)
        => new(
            reason.ReasonCode,
            FailureScope.Candidate,
            FailureCategory.Validation,
            Retryable: false,
            reason.Code switch
            {
                PairingReviewReasonCode.CounterpartMissing => "A companion document is missing and requires review.",
                PairingReviewReasonCode.NoCompatibleEdge => "No compatible companion document was found.",
                PairingReviewReasonCode.AmbiguousOptimum => "The matching documents are ambiguous and require review.",
                PairingReviewReasonCode.UnmatchedByGlobalAssignment => "A compatible document was assigned to a stronger match and requires review.",
                _ => "The document requires manual review.",
            });

    private sealed record PairingProjection(
        int ResultIndex,
        string Mailbox,
        PairingFamily Family,
        PairingDocument Document);
}