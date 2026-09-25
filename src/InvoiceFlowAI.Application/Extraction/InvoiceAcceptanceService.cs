using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Extraction;

public sealed record InvoiceAcceptancePolicy(
    decimal AmountTolerance = 0.01m,
    decimal TaxTotalTolerance = 0.01m,
    decimal MinimumConfidence = 0.60m,
    bool RequireSeller = true,
    bool RequirePurchaserForNonExemptTypes = true,
    bool AllowNegativeAmountOnlyWithCreditFlag = true);

public sealed record InvoiceAcceptanceRequest(
    DocumentCandidate Candidate,
    InvoiceDocument Document,
    InvoiceAcceptancePolicy Policy,
    string CompanyName,
    bool IsVisionFallback,
    string SourceParser);

public enum AcceptanceDisposition
{
    Accepted,
    ManualReview,
    Rejected,
    Retained,
}

public sealed record InvoiceAcceptanceFailure(
    string ReasonCode,
    FailureCategory Category,
    bool Retryable,
    string SafeMessage,
    string Field = "");

public sealed record InvoiceAcceptanceResult(
    AcceptanceDisposition Disposition,
    InvoiceDocument? Document,
    IReadOnlyList<InvoiceAcceptanceFailure> Failures,
    IReadOnlyList<string> Warnings,
    string ReasonCode);

public interface IInvoiceAcceptanceService
{
    InvoiceAcceptanceResult Evaluate(InvoiceAcceptanceRequest request);
}

public sealed class InvoiceAcceptanceService : IInvoiceAcceptanceService
{
    private static readonly HashSet<InvoiceDocumentType> KnownTypes = Enum.GetValues<InvoiceDocumentType>().ToHashSet();
    private static readonly HashSet<string> UnknownPurchaserValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "未知", "未知抬头", "未知购买方", "暂无抬头", "暂无购买方", "unknown", "unknownbuyer", "unknownpurchaser",
    };
    private static readonly HashSet<InvoiceDocumentType> PurchaserExemptTypes =
    [
        InvoiceDocumentType.TrainTicket, InvoiceDocumentType.Taxi, InvoiceDocumentType.FlightTicket,
        InvoiceDocumentType.Itinerary, InvoiceDocumentType.TravelService,
        InvoiceDocumentType.AccommodationStatement, InvoiceDocumentType.AccommodationConfirmation,
        InvoiceDocumentType.Toll, InvoiceDocumentType.FixedInvoice,
        InvoiceDocumentType.NonTargetCompanyInvoice, InvoiceDocumentType.PersonalNonReimbursementInvoice,
        InvoiceDocumentType.RideInvoice, InvoiceDocumentType.RideItinerary,
        InvoiceDocumentType.HotelInvoice, InvoiceDocumentType.HotelFolio, InvoiceDocumentType.AirTicket,
    ];

    public InvoiceAcceptanceResult Evaluate(InvoiceAcceptanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = request.Document;
        var candidateIdentity = request.Candidate.DocumentId;
        var identity = string.IsNullOrWhiteSpace(document.Identity.Value)
            ? new DocumentIdentity(document.DocumentId)
            : document.Identity;

        if (identity != candidateIdentity || !string.Equals(document.DocumentId, candidateIdentity.Value, StringComparison.Ordinal))
            return Rejected("IDENTITY_MISMATCH", "Document identity does not match its candidate.", "Identity");
        if (!document.IsInvoice)
            return Rejected("DOCUMENT_NOT_INVOICE", "Document was classified as non-invoice content.", "IsInvoice");
        if (!Enum.IsDefined(document.DocumentType) || !KnownTypes.Contains(document.DocumentType)
            || document.DocumentType == InvoiceDocumentType.Unrecognized)
            return Rejected("DOCUMENT_TYPE_UNKNOWN", "Document type is not in the supported registry.", "DocumentType");

        var effectiveDate = IsTransport(document.DocumentType) ? document.Route?.DepartureDate ?? document.InvoiceDate : document.InvoiceDate;
        if (effectiveDate is null || effectiveDate == DateOnly.MinValue)
            return Rejected("INVOICE_DATE_INVALID", "A valid invoice date is required.", "InvoiceDate");
        document = document with { InvoiceDate = effectiveDate };
        if (!document.Amount.HasValue)
            return Rejected("INVOICE_AMOUNT_INVALID", "A valid finite amount is required.", "Amount");
        if (document.Amount == 0m)
        {
            if ((document.Flags & (InvoiceFlags.CreditNote | InvoiceFlags.Cancellation)) != 0)
                return Manual("INVOICE_AMOUNT_ZERO", "Zero-value credit or cancellation invoice requires manual review.", "Amount");
            return Rejected("INVOICE_AMOUNT_ZERO", "Zero-value invoice is not accepted.", "Amount");
        }
        if (document.Amount < 0m
            && (!request.Policy.AllowNegativeAmountOnlyWithCreditFlag
                || (document.Flags & (InvoiceFlags.CreditNote | InvoiceFlags.Cancellation)) == 0))
            return Rejected("INVOICE_AMOUNT_INVALID", "Negative amount requires a credit or cancellation flag.", "Amount");
        if (document.TotalAmount.HasValue
            && Math.Abs(document.TotalAmount.Value - (document.Amount.Value + (document.TaxAmount ?? 0m))) > request.Policy.TaxTotalTolerance)
            return Rejected("TAX_TOTAL_MISMATCH", "Tax and amount do not match the invoice total.", "TotalAmount");

        if (request.Policy.RequireSeller && RequiresSeller(document.DocumentType) && string.IsNullOrWhiteSpace(document.Seller))
            return Rejected("INVOICE_SELLER_MISSING", "Invoice seller is required.", "Seller");
        if (RequiresInvoiceNumber(document.DocumentType) && string.IsNullOrWhiteSpace(document.InvoiceNumber))
            return document.DocumentType == InvoiceDocumentType.Other
                ? Manual("INVOICE_NUMBER_MISSING", "Invoice number is missing.", "InvoiceNumber")
                : Rejected("INVOICE_NUMBER_MISSING", "Invoice number is required.", "InvoiceNumber");

        if (RequiresPurchaser(document.DocumentType, request.Policy))
        {
            if (string.IsNullOrWhiteSpace(document.Purchaser) || IsUnknownPurchaser(document.Purchaser))
                return Manual("PURCHASER_UNKNOWN", "Purchaser could not be determined.", "Purchaser");
            if (!IsTargetPurchaser(document.Purchaser, request.CompanyName))
                return new InvoiceAcceptanceResult(
                    AcceptanceDisposition.Retained,
                    document with { DocumentType = InvoiceDocumentType.NonTargetCompanyInvoice },
                    [Failure("PURCHASER_NOT_TARGET", "Invoice purchaser is not the configured company.", "Purchaser")],
                    Array.Empty<string>(),
                    "PURCHASER_NOT_TARGET");
        }

        if ((document.Flags & (InvoiceFlags.LowConfidence | InvoiceFlags.RequiresManualReview)) != 0
            || document.Confidence < request.Policy.MinimumConfidence
            || request.IsVisionFallback)
            return Manual("ACCEPTANCE_LOW_CONFIDENCE", "Extraction requires manual review.");

        var warnings = document.TaxAmount is null && IsTransport(document.DocumentType)
            ? new[] { "TAX_AMOUNT_MISSING" }
            : Array.Empty<string>();
        return new InvoiceAcceptanceResult(AcceptanceDisposition.Accepted, document, Array.Empty<InvoiceAcceptanceFailure>(), warnings, "ACCEPTED");
    }

    private static bool IsTransport(InvoiceDocumentType type) => type is
        InvoiceDocumentType.TrainTicket or InvoiceDocumentType.Taxi or InvoiceDocumentType.FlightTicket
        or InvoiceDocumentType.Itinerary or InvoiceDocumentType.TravelService or InvoiceDocumentType.RideItinerary
        or InvoiceDocumentType.AirTicket;

    private static bool RequiresSeller(InvoiceDocumentType type) => type is
        InvoiceDocumentType.Catering or InvoiceDocumentType.AccommodationInvoice or InvoiceDocumentType.Other
        or InvoiceDocumentType.AccommodationStatement or InvoiceDocumentType.AccommodationConfirmation;

    private static bool RequiresInvoiceNumber(InvoiceDocumentType type) => type is
        InvoiceDocumentType.Catering or InvoiceDocumentType.AccommodationInvoice or InvoiceDocumentType.Other
        or InvoiceDocumentType.Toll or InvoiceDocumentType.FixedInvoice;

    private static bool RequiresPurchaser(InvoiceDocumentType type, InvoiceAcceptancePolicy policy) =>
        policy.RequirePurchaserForNonExemptTypes && !PurchaserExemptTypes.Contains(type);

    private static bool IsUnknownPurchaser(string purchaser) => UnknownPurchaserValues.Contains(purchaser.Trim());

    private static bool IsTargetPurchaser(string purchaser, string companyName) =>
        !string.IsNullOrWhiteSpace(companyName)
        && purchaser.Contains(companyName.Trim(), StringComparison.OrdinalIgnoreCase);

    private static InvoiceAcceptanceResult Rejected(string code, string message, string field) =>
        new(AcceptanceDisposition.Rejected, null, [Failure(code, message, field)], Array.Empty<string>(), code);

    private static InvoiceAcceptanceResult Manual(string code, string message, string field = "") =>
        new(AcceptanceDisposition.ManualReview, null, [Failure(code, message, field)], Array.Empty<string>(), code);

    private static InvoiceAcceptanceFailure Failure(string code, string message, string field) =>
        new(code, FailureCategory.Document, false, message, field);
}