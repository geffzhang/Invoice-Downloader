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
        var reviewReasons = new Dictionary<string, string>(StringComparer.Ordinal);

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

            foreach (var ambiguity in result.Ambiguities)
            {
                foreach (var documentId in ambiguity.DocumentIds)
                {
                    reviewReasons[documentId] = "PAIRING_AMBIGUOUS";
                }
            }

            foreach (var unmatched in result.UnmatchedInvoices.Concat(result.UnmatchedCompanions))
            {
                reviewReasons.TryAdd(unmatched.Id, "PAIRING_UNMATCHED");
            }
        }

        foreach (var projection in eligible)
        {
            if (!reviewReasons.TryGetValue(projection.Document.Id, out var reasonCode))
            {
                continue;
            }

            candidateResults[projection.ResultIndex] = candidateResults[projection.ResultIndex] with
            {
                Status = CandidateStatus.ManualReview,
                Failure = BuildReviewFailure(reasonCode),
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

    private static CandidateFailure BuildReviewFailure(string reasonCode)
        => new(
            reasonCode,
            FailureScope.Candidate,
            FailureCategory.Validation,
            Retryable: false,
            reasonCode == "PAIRING_AMBIGUOUS"
                ? "The matching documents are ambiguous and require review."
                : "No matching companion document was found.");

    private sealed record PairingProjection(
        int ResultIndex,
        string Mailbox,
        PairingFamily Family,
        PairingDocument Document);
}