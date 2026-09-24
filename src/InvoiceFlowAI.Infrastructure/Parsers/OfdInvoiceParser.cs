using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Parsers;

public sealed class OfdInvoiceParser : IParser
{
    private readonly OfdPackageReader _packageReader;
    private readonly XmlInvoiceParser _xmlParser;

    public OfdInvoiceParser() : this(new OfdPackageReader(), new XmlInvoiceParser())
    {
    }

    public OfdInvoiceParser(OfdPackageReader packageReader, XmlInvoiceParser xmlParser)
    {
        _packageReader = packageReader ?? throw new ArgumentNullException(nameof(packageReader));
        _xmlParser = xmlParser ?? throw new ArgumentNullException(nameof(xmlParser));
    }

    public string ParserId => "ofd-invoice";
    public string Version => "1.0";
    public int Priority => 600;
    public IReadOnlyList<string> SourceKinds { get; } = ["ofd"];
    public string FailureCode => "OFD_PACKAGE_INVALID";

    public bool CanParse(ParserWorkItem workItem) =>
        workItem.SourceKind.Equals("ofd", StringComparison.OrdinalIgnoreCase);

    public async Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanParse(workItem))
        {
            return Failed("OFD_SOURCE_UNSUPPORTED", "Input is not an OFD package.");
        }

        IReadOnlyList<OfdXmlEntry> entries;
        try
        {
            entries = _packageReader.ReadInvoiceXmlEntries(workItem.DocumentBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            return Failed("OFD_PACKAGE_INVALID", "OFD package is malformed or exceeds its safety limits.");
        }

        var parsedInvoices = new List<InvoiceDocument>();
        var resolvedInvoices = new List<InvoiceDocument>();
        var needsFallback = false;
        CandidateFailure? fallbackFailure = null;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xmlWorkItem = workItem with
            {
                SourceKind = "ofd-xml",
                DocumentBytes = entry.Content,
            };
            var outcome = await _xmlParser.ParseAsync(xmlWorkItem, cancellationToken).ConfigureAwait(false);
            if (outcome.Disposition == ParserOutcomeDisposition.NeedsFallback)
            {
                needsFallback = true;
                fallbackFailure ??= outcome.Failure;
            }
            if (outcome.Invoice is not null)
            {
                parsedInvoices.Add(outcome.Invoice);
                if (outcome.Disposition == ParserOutcomeDisposition.Resolved)
                {
                    resolvedInvoices.Add(outcome.Invoice);
                }
            }
        }

        if (parsedInvoices.Count == 0)
        {
            if (needsFallback)
            {
                return new ParserOutcome(
                    ParserId,
                    Version,
                    Invoice: null,
                    MissingFields: Array.Empty<string>(),
                    Failure: fallbackFailure,
                    Disposition: ParserOutcomeDisposition.NeedsFallback);
            }
            return Failed("OFD_INVOICE_XML_MISSING", "OFD package does not contain a readable invoice XML entry.");
        }

        var selectedInvoice = resolvedInvoices.FirstOrDefault() ?? parsedInvoices[0];
        if (parsedInvoices.Any(invoice => !ReferenceEquals(invoice, selectedInvoice) && Conflicts(selectedInvoice, invoice)))
        {
            return Failed("OFD_XML_CONFLICT", "OFD invoice XML entries contain conflicting field values.");
        }

        var hasResolvedInvoice = resolvedInvoices.Count > 0;
        return new ParserOutcome(
            ParserId,
            Version,
            selectedInvoice,
            MissingFields: Array.Empty<string>(),
            Failure: hasResolvedInvoice ? null : fallbackFailure,
            Disposition: hasResolvedInvoice ? ParserOutcomeDisposition.Resolved : ParserOutcomeDisposition.NeedsFallback);

    }

    private static bool Conflicts(InvoiceDocument first, InvoiceDocument other) =>
        Conflicts(first.InvoiceNumber, other.InvoiceNumber)
        || Conflicts(first.InvoiceDate, other.InvoiceDate)
        || Conflicts(first.Amount, other.Amount)
        || Conflicts(first.TaxAmount, other.TaxAmount)
        || Conflicts(first.TotalAmount, other.TotalAmount)
        || Conflicts(first.Seller, other.Seller)
        || Conflicts(first.Purchaser, other.Purchaser);

    private static bool Conflicts(string? first, string? other) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(other)
        && !string.Equals(first, other, StringComparison.Ordinal);

    private static bool Conflicts<T>(T? first, T? other) where T : struct =>
        first.HasValue && other.HasValue && !EqualityComparer<T>.Default.Equals(first.Value, other.Value);

    private ParserOutcome Failed(string code, string message) => new(
        ParserId,
        Version,
        Invoice: null,
        MissingFields: Array.Empty<string>(),
        Failure: new CandidateFailure(
            code,
            FailureScope.Candidate,
            FailureCategory.Document,
            Retryable: false,
            SafeMessage: message),
        Disposition: ParserOutcomeDisposition.Failed);
}