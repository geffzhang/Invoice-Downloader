using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class XmlInvoiceParser : IParser
{
    private const long MaximumDocumentCharacters = 5_000_000;
    private const int MaximumXmlDepth = 64;

    public string ParserId => "xml-invoice";
    public string Version => "1.0";
    public int Priority => 500;
    public IReadOnlyList<string> SourceKinds { get; } = ["xml", "ofd-xml"];
    public string FailureCode => "XML_INVOICE_INVALID";

    public bool CanParse(ParserWorkItem workItem) =>
        workItem.SourceKind.Equals("xml", StringComparison.OrdinalIgnoreCase)
        || workItem.SourceKind.Equals("ofd-xml", StringComparison.OrdinalIgnoreCase);

    public Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanParse(workItem))
        {
            return Task.FromResult(Failed("XML_SOURCE_UNSUPPORTED", "Input is not an XML invoice source."));
        }
        if (workItem.DocumentBytes.IsEmpty || workItem.DocumentBytes.Length > MaximumDocumentCharacters)
        {
            return Task.FromResult(Failed("XML_INVOICE_INVALID", "XML invoice input is empty or exceeds the size limit."));
        }

        try
        {
            using var stream = new MemoryStream(workItem.DocumentBytes.ToArray(), writable: false);
            using (var preflightReader = XmlReader.Create(stream, CreateReaderSettings()))
            {
                while (preflightReader.Read())
                {
                    if (preflightReader.Depth > MaximumXmlDepth)
                    {
                        return Task.FromResult(Failed("XML_INVOICE_INVALID", "XML invoice exceeds the nesting limit."));
                    }
                }
            }

            stream.Position = 0;
            using var reader = XmlReader.Create(stream, CreateReaderSettings());
            var root = XDocument.Load(reader, LoadOptions.None).Root;
            if (root is null)
            {
                return Task.FromResult(Failed("XML_INVOICE_INVALID", "XML invoice has no document element."));
            }

            var invoiceNumber = FindValue(root, "InvoiceNumber", "InvoiceNo", "Invoice_Number", "Number");
            var dateText = FindValue(root, "InvoiceDate", "Date", "Invoice_Date", "IssueDate");
            var date = ParseDate(dateText);
            var amount = ParseDecimal(FindValue(root, "Amount", "InvoiceAmount", "TotalAmountWithoutTax", "PreTaxAmount"));
            var tax = ParseDecimal(FindValue(root, "TaxAmount", "Tax", "TotalTax"));
            var total = ParseDecimal(FindValue(root, "TotalAmount", "TaxInclusiveAmount", "PriceTaxTotal", "TotalAmountWithTax"));
            var seller = FindValue(root, "SellerName", "Seller", "SellerNameText", "NameOfSeller");
            var purchaser = FindValue(root, "BuyerName", "Purchaser", "Buyer", "NameOfBuyer");
            var type = ResolveType(FindValue(root, "InvoiceType", "DocumentType", "Type"));

            var invoice = new InvoiceDocument(
                DocumentId: workItem.Candidate.DocumentId.Value,
                InvoiceDate: date,
                Purchaser: purchaser,
                Seller: seller,
                Amount: amount,
                TaxAmount: tax,
                TotalAmount: total,
                InvoiceCode: FindValue(root, "InvoiceCode", "Invoice_Code"),
                InvoiceNumber: invoiceNumber,
                DocumentType: type,
                Category: string.Empty,
                Route: null,
                Items: Array.Empty<InvoiceItem>(),
                SourceFileName: workItem.Candidate.OriginalFileName,
                ContentHash: string.Empty)
            {
                Identity = workItem.Candidate.DocumentId,
                ParserName = ParserId,
            };

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(invoiceNumber)) missing.Add("InvoiceNumber");
            if (date is null) missing.Add("InvoiceDate");
            if (amount is null) missing.Add("Amount");
            if (string.IsNullOrWhiteSpace(seller)) missing.Add("Seller");
            var needsFallback = missing.Count > 0;
            var failureCode = string.IsNullOrWhiteSpace(invoiceNumber)
                ? "INVOICE_NUMBER_MISSING"
                : "XML_FIELDS_INCOMPLETE";
            var failureMessage = string.IsNullOrWhiteSpace(invoiceNumber)
                ? "Invoice number is missing."
                : "XML invoice fields are incomplete.";
            return Task.FromResult(new ParserOutcome(
                ParserId,
                Version,
                invoice,
                missing,
                Failure: needsFallback ? Failure(failureCode, failureMessage) : null,
                Disposition: needsFallback ? ParserOutcomeDisposition.NeedsFallback : ParserOutcomeDisposition.Resolved));
        }
        catch (XmlException)
        {
            return Task.FromResult(Failed("XML_INVOICE_INVALID", "XML invoice is malformed or contains prohibited declarations."));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(Failed("XML_INVOICE_INVALID", "XML invoice could not be read."));
        }
        catch (FormatException)
        {
            return Task.FromResult(Failed("XML_INVOICE_INVALID", "XML invoice contains an invalid value."));
        }
    }

    private ParserOutcome Failed(string code, string message) => new(
        ParserId,
        Version,
        Invoice: null,
        MissingFields: Array.Empty<string>(),
        Failure: Failure(code, message),
        Disposition: ParserOutcomeDisposition.Failed);

    private static CandidateFailure Failure(string code, string message) => new(
        code,
        FailureScope.Candidate,
        FailureCategory.Document,
        Retryable: false,
        SafeMessage: message);

    private static string FindValue(XElement root, params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return root.DescendantsAndSelf()
            .Where(element => allowed.Contains(element.Name.LocalName))
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
    }

    private static XmlReaderSettings CreateReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumDocumentCharacters,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static DateOnly? ParseDate(string value)
    {
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            return date;
        }
        return null;
    }

    private static decimal? ParseDecimal(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return amount;
        }
        return null;
    }

    private static InvoiceDocumentType ResolveType(string value) => value.Trim() switch
    {
        "Catering" or "餐饮" => InvoiceDocumentType.Catering,
        "AccommodationInvoice" or "住宿发票" => InvoiceDocumentType.AccommodationInvoice,
        "TrainTicket" or "RailwayTicket" or "火车票" => InvoiceDocumentType.TrainTicket,
        "Taxi" or "出租车" => InvoiceDocumentType.Taxi,
        "FlightTicket" or "AirTicket" or "航空运输电子客票行程单" => InvoiceDocumentType.FlightTicket,
        "Itinerary" or "RideItinerary" => InvoiceDocumentType.Itinerary,
        "TravelService" => InvoiceDocumentType.TravelService,
        "Toll" => InvoiceDocumentType.Toll,
        "FixedInvoice" => InvoiceDocumentType.FixedInvoice,
        "Other" or "其他" => InvoiceDocumentType.Other,
        _ => InvoiceDocumentType.Unrecognized,
    };
}