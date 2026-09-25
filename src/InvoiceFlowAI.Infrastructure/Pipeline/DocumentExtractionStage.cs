using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Infrastructure.Pipeline;

public sealed class DocumentExtractionStage : IDocumentExtractionStage
{
    private static readonly IReadOnlyDictionary<string, string> FormatParserIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["xml"] = "xml-invoice",
            ["ofd"] = "ofd-invoice",
            ["pdf"] = "pdf-text-invoice",
        };

    private readonly IReadOnlyList<IParser> _parsers;
    private readonly IInvoiceFieldExtractor _extractor;
    private readonly InvoiceNormalizer _normalizer;
    private readonly IInvoiceAcceptanceService _acceptance;
    private readonly InvoiceExtractionRules _rules;

    public DocumentExtractionStage(
        IEnumerable<IParser> parsers,
        IInvoiceFieldExtractor extractor,
        InvoiceNormalizer normalizer,
        IInvoiceAcceptanceService acceptance,
        InvoiceExtractionRules rules)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        _parsers = parsers.ToArray();
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _acceptance = acceptance ?? throw new ArgumentNullException(nameof(acceptance));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public async Task<ExtractionBatch> ExecuteAsync(CandidateBatch input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var terminalResults = input.EffectiveTerminalResults;
        var results = new List<(int Order, CandidateProcessResult Result)>(terminalResults.Count + input.Items.Count);
        for (var index = 0; index < terminalResults.Count; index++)
        {
            results.Add((index, terminalResults[index]));
        }

        for (var index = 0; index < input.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = input.Items[index];
            CandidateProcessResult result;
            try
            {
                result = await ProcessCandidateAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                result = Failure(item.Candidate, "EXTRACTION_STAGE_FAILED", "Document extraction could not be completed.", FailureCategory.Internal);
            }

            results.Add((terminalResults.Count + index, result));
        }

        var ordered = results
            .OrderBy(entry => entry.Result.Candidate.Sequence)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Result)
            .ToArray();
        return new ExtractionBatch(ordered) { PreflightResults = terminalResults };
    }

    private async Task<CandidateProcessResult> ProcessCandidateAsync(CandidateWorkItem item, CancellationToken cancellationToken)
    {
        var candidate = item.Candidate;
        var sourceKind = GetSourceKind(candidate.ContentType, candidate.OriginalFileName);
        var formatOutcome = await RunFormatParserAsync(item, sourceKind, cancellationToken).ConfigureAwait(false);
        if (formatOutcome is { Disposition: ParserOutcomeDisposition.Resolved })
        {
            return FromParserOutcome(candidate, formatOutcome);
        }
        if (formatOutcome is { Disposition: ParserOutcomeDisposition.Failed })
        {
            return FromParserOutcome(candidate, formatOutcome);
        }

        var deterministicSource = formatOutcome?.Invoice;
        if (sourceKind == "pdf")
        {
            var specialOutcome = await RunSpecialParserAsync(item, sourceKind, cancellationToken).ConfigureAwait(false);
            if (specialOutcome.Conflict is not null)
            {
                return Failure(candidate, specialOutcome.Conflict.ReasonCode,
                    "Multiple document parsers claimed this candidate.", FailureCategory.Validation);
            }
            if (specialOutcome.Selected is { Disposition: not ParserOutcomeDisposition.NeedsFallback } selected)
            {
                return FromParserOutcome(candidate, selected);
            }
            if (specialOutcome.Selected?.Invoice is { } specialInvoice)
            {
                deterministicSource = specialInvoice;
            }
        }

        return await RunGenericExtractorAsync(item, deterministicSource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ParserOutcome?> RunFormatParserAsync(
        CandidateWorkItem item,
        string sourceKind,
        CancellationToken cancellationToken)
    {
        if (!FormatParserIds.TryGetValue(sourceKind, out var parserId))
        {
            return null;
        }

        var parser = _parsers.FirstOrDefault(candidate =>
            candidate.ParserId.Equals(parserId, StringComparison.Ordinal)
            && candidate.CanParse(CreateParserWorkItem(item, sourceKind)));
        return parser is null
            ? null
            : await parser.ParseAsync(CreateParserWorkItem(item, sourceKind), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ParserPipelineOutcome> RunSpecialParserAsync(
        CandidateWorkItem item,
        string sourceKind,
        CancellationToken cancellationToken)
    {
        var workItem = CreateParserWorkItem(item, sourceKind);
        var formatParserId = FormatParserIds[sourceKind];
        var claimingParsers = _parsers
            .Where(parser => !parser.ParserId.Equals(formatParserId, StringComparison.Ordinal)
                && parser.CanParse(workItem))
            .ToArray();
        if (claimingParsers.Length == 0)
        {
            return new ParserPipelineOutcome(null, null, Array.Empty<ParserOutcome>());
        }

        return await new ParserPipeline(new ParserRegistry(claimingParsers))
            .RunAsync(workItem, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CandidateProcessResult> RunGenericExtractorAsync(
        CandidateWorkItem item,
        InvoiceDocument? deterministicSource,
        CancellationToken cancellationToken)
    {
        string? temporaryPdfPath = null;
        try
        {
            if (item.Candidate.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                && !item.Content.IsEmpty)
            {
                temporaryPdfPath = Path.Combine(Path.GetTempPath(), $"invoiceflow-{Guid.NewGuid():N}.pdf");
                await File.WriteAllBytesAsync(temporaryPdfPath, item.Content.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            var source = new DocumentSource(
                item.Candidate.DocumentId,
                LocalPath: temporaryPdfPath ?? string.Empty,
                SourceUrl: item.Candidate.SourceUrl?.AbsoluteUri ?? string.Empty,
                MimeType: item.Candidate.ContentType,
                ContentHash: GetMetadata(item.Candidate, "content_sha256"),
                AttachmentPartId: GetMetadata(item.Candidate, "attachment_part_id"),
                Mailbox: GetMetadata(item.Candidate, "mailbox"),
                Subject: GetMetadata(item.Candidate, "subject"),
                Sender: GetMetadata(item.Candidate, "sender"));
            var extraction = await _extractor.ExtractAsync(
                new FieldExtractionRequest(item.Candidate, source, null, _rules, AllowVisionFallback: true,
                    DeterministicCorrections: ToCorrections(deterministicSource)),
                cancellationToken).ConfigureAwait(false);
            return FromExtractionResult(item.Candidate, extraction);
        }
        finally
        {
            if (temporaryPdfPath is not null)
            {
                File.Delete(temporaryPdfPath);
            }
        }
    }

    private static InvoiceResponseFields? ToCorrections(InvoiceDocument? document) => document is null
        ? null
        : new InvoiceResponseFields(
            null,
            document.InvoiceDate,
            Optional(document.Purchaser),
            Optional(document.Seller),
            document.Amount,
            document.TaxAmount,
            document.TotalAmount,
            Optional(document.InvoiceCode),
            Optional(document.InvoiceNumber),
            document.DocumentType == InvoiceDocumentType.Unrecognized ? null : document.DocumentType,
            Optional(document.Category),
            document.Confidence > 0m ? document.Confidence : null,
            document.Flags == InvoiceFlags.None
                ? null
                : Enum.GetValues<InvoiceFlags>().Where(flag => flag != InvoiceFlags.None && document.Flags.HasFlag(flag)).ToArray(),
            document.Route is null
                ? null
                : new InvoiceRouteResponse(
                    document.Route.Direction,
                    document.Route.DepartureDate,
                    document.Route.DepartureCity,
                    document.Route.DestinationCity),
            document.Items.Count == 0 ? null : document.Items.Select(item => new InvoiceItemResponse(
                item.Name,
                item.Quantity,
                item.UnitPrice,
                item.Amount,
                item.TaxAmount,
                item.Unit,
                item.Specification)).ToArray());

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private CandidateProcessResult FromParserOutcome(DocumentCandidate candidate, ParserOutcome outcome)
    {
        if (outcome.Disposition == ParserOutcomeDisposition.NeedsFallback)
        {
            return Failure(candidate,
                outcome.Failure?.ReasonCode ?? "PARSER_NEEDS_FALLBACK",
                outcome.Failure?.SafeMessage ?? "The document parser requires further extraction.",
                outcome.Failure?.Category ?? FailureCategory.Document);
        }
        if (outcome.Disposition == ParserOutcomeDisposition.Failed || outcome.Invoice is null)
        {
            return outcome.Failure is { } failure
                ? new CandidateProcessResult(candidate, CandidateStatus.ManualReview, Failure: failure)
                : Failure(candidate, "PARSER_FAILED", "The document parser could not process this candidate.", FailureCategory.Document);
        }

        var document = _normalizer.Normalize(outcome.Invoice, candidate.DocumentId);
        var acceptance = _acceptance.Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            document,
            _rules.AcceptancePolicy ?? new InvoiceAcceptancePolicy(),
            _rules.CompanyName,
            IsVisionFallback: false,
            SourceParser: outcome.ParserId));
        return FromAcceptance(candidate, acceptance, outcome.Failure);
    }

    private static CandidateProcessResult FromExtractionResult(DocumentCandidate candidate, FieldExtractionResult extraction)
    {
        if (extraction.Identity != candidate.DocumentId)
        {
            return Failure(candidate, "IDENTITY_MISMATCH", "Extraction result identity did not match its candidate.", FailureCategory.Validation);
        }

        var status = extraction.Disposition switch
        {
            AcceptanceDisposition.Accepted when extraction.Document is not null => CandidateStatus.Resolved,
            AcceptanceDisposition.Retained => CandidateStatus.Retained,
            AcceptanceDisposition.ManualReview => CandidateStatus.ManualReview,
            AcceptanceDisposition.Rejected when extraction.ReasonCode == "AI_AUTHENTICATION_FAILED" => CandidateStatus.AuthFailed,
            _ => CandidateStatus.Unresolved,
        };
        var failure = extraction.Failures.FirstOrDefault();
        return new CandidateProcessResult(
            candidate,
            status,
            status == CandidateStatus.Resolved ? extraction.Document : null,
            Failure: failure is null ? null : new CandidateFailure(
                failure.ReasonCode,
                FailureScope.Candidate,
                failure.Category,
                failure.Retryable,
                failure.SafeMessage),
            Warnings: extraction.Warnings,
            Trace: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["route"] = extraction.Route.ToString(),
                ["reason_code"] = extraction.ReasonCode,
            });
    }

    private static CandidateProcessResult FromAcceptance(
        DocumentCandidate candidate,
        InvoiceAcceptanceResult acceptance,
        CandidateFailure? parserFailure)
    {
        var status = acceptance.Disposition switch
        {
            AcceptanceDisposition.Accepted when acceptance.Document is not null => CandidateStatus.Resolved,
            AcceptanceDisposition.Retained => CandidateStatus.Retained,
            AcceptanceDisposition.ManualReview => CandidateStatus.ManualReview,
            _ => CandidateStatus.Unresolved,
        };
        var acceptanceFailure = acceptance.Failures.FirstOrDefault();
        return new CandidateProcessResult(
            candidate,
            status,
            status == CandidateStatus.Resolved ? acceptance.Document : null,
            Failure: parserFailure ?? (acceptanceFailure is null ? null : new CandidateFailure(
                acceptanceFailure.ReasonCode,
                FailureScope.Candidate,
                acceptanceFailure.Category,
                acceptanceFailure.Retryable,
                acceptanceFailure.SafeMessage)),
            Warnings: acceptance.Warnings);
    }

    private static ParserWorkItem CreateParserWorkItem(CandidateWorkItem item, string sourceKind) =>
        new(item.Candidate, item.Candidate.DocumentId.Value, sourceKind, item.Content);

    private static string GetSourceKind(string contentType, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return "xml";
        if (extension.Equals(".ofd", StringComparison.OrdinalIgnoreCase)) return "ofd";
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)) return "xml";
        if (contentType.Contains("ofd", StringComparison.OrdinalIgnoreCase)) return "ofd";
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        return string.Empty;
    }

    private static string GetMetadata(DocumentCandidate candidate, string key) =>
        candidate.Metadata is not null && candidate.Metadata.TryGetValue(key, out var value) ? value : string.Empty;

    private static CandidateProcessResult Failure(
        DocumentCandidate candidate,
        string reasonCode,
        string safeMessage,
        FailureCategory category) =>
        new(candidate, CandidateStatus.ManualReview, Failure: new CandidateFailure(
            reasonCode,
            FailureScope.Candidate,
            category,
            Retryable: false,
            safeMessage));
}