// Fixture-backed parser implementation. The four production parsers
// (railway-ticket, accommodation-folio, foreign-invoice, provider-
// special-layout) are deterministic and are exercised in tests by
// loading the corresponding JSON fixture from disk. The fixture
// controls the parsed fields and (optionally) a "missingFields" list
// for the missing-field scenario.
//
// In production these parsers call out to PDFium / PdfPig / Sdcb
// SimdPaddleOCR / the DeepSeek vision model — the application layer
// only knows about the IParser contract so the test path here is
// the same shape as the real implementation.

using System.Globalization;
using System.Text.Json;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class FixtureBackedParser : IParser
{
    private readonly string _fixturePath;

    public string ParserId { get; }
    public string Version { get; }
    public int Priority { get; }
    public IReadOnlyList<string> SourceKinds { get; }
    public string FailureCode { get; }

    public FixtureBackedParser(
        string parserId,
        string version,
        int priority,
        IReadOnlyList<string> sourceKinds,
        string failureCode,
        string fixturePath)
    {
        ParserId = parserId;
        Version = version;
        Priority = priority;
        SourceKinds = sourceKinds;
        FailureCode = failureCode;
        _fixturePath = fixturePath;
    }

    public bool CanParse(ParserWorkItem workItem) =>
        SourceKinds.Contains(workItem.SourceKind, StringComparer.OrdinalIgnoreCase);

    public async Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
    {
        if (!File.Exists(_fixturePath))
        {
            return Failed("PARSER_FIXTURE_MISSING", $"Fixture not found: {_fixturePath}");
        }

        await using var stream = File.OpenRead(_fixturePath);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var missing = new List<string>();
        if (root.TryGetProperty("missingFields", out var missingEl))
        {
            foreach (var m in missingEl.EnumerateArray())
            {
                missing.Add(m.GetString() ?? "");
            }
        }

        var fields = root.GetProperty("fields");
        if (missing.Count == 0)
        {
            var invoice = BuildInvoice(workItem, fields);
            return new ParserOutcome(ParserId, Version, invoice, Array.Empty<string>(), Failure: null);
        }

        // Partial result: invoice with what's present, plus the missing
        // field list. The pipeline surfaces this for NeedsManualReview.
        var partial = BuildInvoice(workItem, fields, allowMissing: true);
        return new ParserOutcome(ParserId, Version, partial, missing, Failure: null);
    }

    private InvoiceDocument BuildInvoice(ParserWorkItem workItem, JsonElement fields, bool allowMissing = false)
    {
        string Read(string key)
        {
            if (!fields.TryGetProperty(key, out var el)) return "";
            return el.GetString() ?? "";
        }
        decimal ReadDec(string key)
        {
            var text = Read(key);
            if (string.IsNullOrEmpty(text)) return 0m;
            return decimal.Parse(text, CultureInfo.InvariantCulture);
        }

        var trace = new Dictionary<string, string>
        {
            ["ParsedBy"] = ParserId,
            ["ParserVersion"] = Version,
            ["SourceKind"] = workItem.SourceKind,
            ["Confidence"] = allowMissing ? "0.50" : "0.95",
        };

        return new InvoiceDocument(
            DocumentId: workItem.DocumentId,
            InvoiceDate: ParseDate(Read("invoiceDate")),
            Purchaser: Read("purchaser"),
            Seller: Read("seller"),
            Amount: ReadDec("amount"),
            TaxAmount: ReadDec("taxAmount"),
            TotalAmount: ReadDec("totalAmount"),
            InvoiceCode: NullIfEmpty(Read("invoiceCode")),
            InvoiceNumber: NullIfEmpty(Read("invoiceNumber")),
            DocumentType: ResolveDocumentType(Read("documentType")),
            Category: null,
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: workItem.Candidate.OriginalFileName,
            ContentHash: "",
            Trace: trace);
    }

    private static DateOnly ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return DateOnly.MinValue;
    }

    private static InvoiceDocumentType ResolveDocumentType(string text) => text switch
    {
        "RailwayTicket" => InvoiceDocumentType.TrainTicket,
        "TrainTicket" => InvoiceDocumentType.TrainTicket,
        "AccommodationFolio" => InvoiceDocumentType.HotelFolio,
        "HotelFolio" => InvoiceDocumentType.HotelFolio,
        "HotelInvoice" => InvoiceDocumentType.HotelInvoice,
        "FlightInvoice" => InvoiceDocumentType.AirTicket,
        "AirTicket" => InvoiceDocumentType.AirTicket,
        "Catering" => InvoiceDocumentType.Other,
        "TaxInvoice" => InvoiceDocumentType.TaxInvoice,
        "VatInvoice" => InvoiceDocumentType.VatInvoice,
        "RideInvoice" => InvoiceDocumentType.RideInvoice,
        "RideItinerary" => InvoiceDocumentType.RideItinerary,
        "Other" => InvoiceDocumentType.Other,
        _ => InvoiceDocumentType.Other,
    };

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    private ParserOutcome Failed(string code, string message) => new(
        ParserId,
        Version,
        Invoice: null,
        MissingFields: Array.Empty<string>(),
        Failure: new CandidateFailure(
            ReasonCode: code,
            Scope: FailureScope.Candidate,
            Category: FailureCategory.Document,
            Retryable: false,
            SafeMessage: message),
        Disposition: ParserOutcomeDisposition.Failed);
}
