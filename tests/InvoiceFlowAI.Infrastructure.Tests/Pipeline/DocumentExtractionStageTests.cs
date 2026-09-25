using FluentAssertions;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Pipeline;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Pipeline;

public sealed class DocumentExtractionStageTests
{
    [Fact]
    public async Task Accepted_format_parser_fast_path_skips_special_and_ai_routes()
    {
        var format = new FakeParser("xml-invoice", "xml", 500, true, ResolvedInvoice);
        var special = new FakeParser("special", "xml", 100, true, ResolvedInvoice);
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([format, special], extractor);

        var result = await stage.ExecuteAsync(new CandidateBatch([WorkItem("xml-candidate", "application/xml", "invoice.xml")]), CancellationToken.None);

        result.Results.Should().ContainSingle().Which.Status.Should().Be(CandidateStatus.Resolved);
        format.CallCount.Should().Be(1);
        special.CallCount.Should().Be(0);
        extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Claimed_special_parser_failure_is_terminal_and_does_not_call_generic_router()
    {
        var format = new FakeParser("pdf-text-invoice", "pdf", 400, true, NeedsFallback);
        var special = new FakeParser("special-failure", "pdf", 470, true, Failed);
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([format, special], extractor);

        var result = await stage.ExecuteAsync(new CandidateBatch([WorkItem("special-failure")]), CancellationToken.None);

        result.Results.Should().ContainSingle().Which.Failure!.ReasonCode.Should().Be("SPECIAL_FAILED");
        special.CallCount.Should().Be(1);
        extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Generic_extraction_receives_candidate_pdf_bytes_as_temporary_local_source()
    {
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([], extractor);

        var result = await stage.ExecuteAsync(new CandidateBatch([WorkItem("temporary-source")]), CancellationToken.None);

        result.Results.Should().ContainSingle().Which.Status.Should().Be(CandidateStatus.Resolved);
        extractor.SourceExistedDuringCall.Should().BeTrue();
        extractor.SourceBytesDuringCall.Should().Equal(1, 2, 3);
        extractor.LastRequest!.Source.LocalPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(extractor.LastRequest.Source.LocalPath).Should().BeFalse();
    }

    [Fact]
    public async Task Ambiguous_special_parser_claims_return_manual_review_without_generic_fallback()
    {
        var format = new FakeParser("pdf-text-invoice", "pdf", 400, true, NeedsFallback);
        var first = new FakeParser("special-a", "pdf", 470, true, ResolvedInvoice);
        var second = new FakeParser("special-b", "pdf", 470, true, ResolvedInvoice);
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([format, first, second], extractor);

        var result = await stage.ExecuteAsync(new CandidateBatch([WorkItem("ambiguous")]), CancellationToken.None);

        result.Results.Should().ContainSingle().Which.Status.Should().Be(CandidateStatus.ManualReview);
        result.Results[0].Failure!.ReasonCode.Should().Be("SPECIAL_PARSER_CONFLICT");
        first.CallCount.Should().Be(0);
        second.CallCount.Should().Be(0);
        extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Explicit_needs_fallback_reaches_generic_extractor()
    {
        var format = new FakeParser("pdf-text-invoice", "pdf", 400, true, NeedsFallback with { Invoice = Invoice });
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([format], extractor);

        var result = await stage.ExecuteAsync(new CandidateBatch([WorkItem("fallback")]), CancellationToken.None);

        result.Results.Should().ContainSingle().Which.Status.Should().Be(CandidateStatus.Resolved);
        extractor.CallCount.Should().Be(1);
        extractor.LastRequest!.DeterministicCorrections!.InvoiceNumber.Should().Be("INV-1");
    }

    [Fact]
    public async Task Existing_terminal_results_are_preserved_and_merged_in_sequence_order()
    {
        var terminal = new CandidateProcessResult(Candidate("terminal", 1), CandidateStatus.Retained);
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([], extractor);

        var result = await stage.ExecuteAsync(new CandidateBatch([WorkItem("active", sequence: 2)], [terminal]), CancellationToken.None);

        result.Results.Select(item => item.Candidate.Sequence).Should().Equal(1, 2);
        result.Results[0].Should().BeSameAs(terminal);
        result.Results.Should().HaveCount(2);
    }

    [Fact]
    public async Task Mixed_candidate_outcomes_are_isolated_and_preserve_candidate_identity_revision_and_order()
    {
        var extractor = new FakeFieldExtractor(identity => identity.Value == "bad"
            ? FailedExtraction(identity)
            : AcceptedExtraction(identity));
        var stage = CreateStage([], extractor);
        var batch = new CandidateBatch([WorkItem("good", sequence: 0, revision: 3), WorkItem("bad", sequence: 1, revision: 4)]);

        var result = await stage.ExecuteAsync(batch, CancellationToken.None);

        result.Results.Should().HaveCount(2);
        result.Results.Select(item => item.Candidate.DocumentId.Value).Should().Equal("good", "bad");
        result.Results.Select(item => item.Candidate.ProcessingRevision).Should().Equal(3, 4);
        result.Results[0].Status.Should().Be(CandidateStatus.Resolved);
        result.Results[1].Status.Should().Be(CandidateStatus.ManualReview);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_before_processing_candidates()
    {
        var extractor = new FakeFieldExtractor(AcceptedExtraction);
        var stage = CreateStage([], extractor);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage.ExecuteAsync(
            new CandidateBatch([WorkItem("cancelled")]), cancellation.Token));

        extractor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task One_result_is_returned_per_input_and_terminal_candidate()
    {
        var terminal = new CandidateProcessResult(Candidate("retained", 2), CandidateStatus.Retained);
        var stage = CreateStage([], new FakeFieldExtractor(AcceptedExtraction));
        var batch = new CandidateBatch([WorkItem("a", sequence: 0), WorkItem("b", sequence: 1)], [terminal]);

        var result = await stage.ExecuteAsync(batch, CancellationToken.None);

        result.Results.Should().HaveCount(batch.Items.Count + batch.EffectiveTerminalResults.Count);
    }

    private static DocumentExtractionStage CreateStage(IReadOnlyList<IParser> parsers, FakeFieldExtractor extractor)
    {
        return new DocumentExtractionStage(
            parsers,
            extractor,
            new InvoiceNormalizer(),
            new InvoiceAcceptanceService(),
            new InvoiceExtractionRules("Example Company"));
    }

    private static CandidateWorkItem WorkItem(string id, string contentType = "application/pdf", string fileName = "invoice.pdf", long sequence = 0, int revision = 0) =>
        new(Candidate(id, sequence, revision, fileName, contentType), new byte[] { 1, 2, 3 });

    private static DocumentCandidate Candidate(string id, long sequence = 0, int revision = 0, string fileName = "invoice.pdf", string contentType = "application/pdf") =>
        new(DocumentIdentity.Create(id), sequence, $"corr-{id}", "uid-1", fileName, contentType, 3, revision, "mime_attachment");

    private static readonly InvoiceDocument Invoice = new(
        "candidate", new DateOnly(2026, 9, 24), "Example Company", "Example Seller", 10m, 0m, 10m,
        null, "INV-1", InvoiceDocumentType.Catering, "餐饮", null, Array.Empty<InvoiceItem>(), "invoice.pdf", "hash")
    {
        Confidence = 0.95m,
    };

    private static readonly ParserOutcome ResolvedInvoice = new("format", "1", Invoice, Array.Empty<string>(), null);
    private static readonly ParserOutcome NeedsFallback = new("parser", "1", null, Array.Empty<string>(),
        new CandidateFailure("NEEDS_FALLBACK", FailureScope.Candidate, FailureCategory.Document, false, "Needs fallback."),
        ParserOutcomeDisposition.NeedsFallback);
    private static readonly ParserOutcome Failed = new("parser", "1", null, Array.Empty<string>(),
        new CandidateFailure("SPECIAL_FAILED", FailureScope.Candidate, FailureCategory.Document, false, "Special parser failed."),
        ParserOutcomeDisposition.Failed);

    private static FieldExtractionResult AcceptedExtraction(DocumentIdentity identity) => new(
        identity, Invoice with { DocumentId = identity.Value, Identity = identity }, AcceptanceDisposition.Accepted,
        Array.Empty<InvoiceAcceptanceFailure>(), Array.Empty<string>(), ExtractionRoute.OcrText, "ACCEPTED",
        new ExtractionTrace(ExtractionRoute.OcrText, "ACCEPTED", "NOT_RUN", "ACCEPTED", TimeSpan.Zero, "fake", "document"));

    private static FieldExtractionResult FailedExtraction(DocumentIdentity identity) => new(
        identity, null, AcceptanceDisposition.ManualReview,
        [new InvoiceAcceptanceFailure("AI_VISION_FAILED", FailureCategory.Document, false, "Safe extraction failure.")],
        Array.Empty<string>(), ExtractionRoute.VisionFallback, "AI_VISION_FAILED",
        new ExtractionTrace(ExtractionRoute.VisionFallback, "FAILED", "FAILED", "AI_VISION_FAILED", TimeSpan.Zero, "", "document"));

    private sealed class FakeFieldExtractor(Func<DocumentIdentity, FieldExtractionResult> outcome) : IInvoiceFieldExtractor
    {
        public int CallCount { get; private set; }
        public FieldExtractionRequest? LastRequest { get; private set; }
        public bool SourceExistedDuringCall { get; private set; }
        public byte[]? SourceBytesDuringCall { get; private set; }
        public Task<FieldExtractionResult> ExtractAsync(FieldExtractionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            SourceExistedDuringCall = !string.IsNullOrWhiteSpace(request.Source.LocalPath)
                && File.Exists(request.Source.LocalPath);
            SourceBytesDuringCall = SourceExistedDuringCall ? File.ReadAllBytes(request.Source.LocalPath) : null;
            return Task.FromResult(outcome(request.Candidate.DocumentId));
        }
    }

    private sealed class FakeParser(string parserId, string sourceKind, int priority, bool claims, ParserOutcome outcome) : IParser
    {
        public string ParserId => parserId;
        public string Version => "1.0";
        public int Priority => priority;
        public IReadOnlyList<string> SourceKinds { get; } = [sourceKind];
        public string FailureCode => "TEST_FAILED";
        public int CallCount { get; private set; }
        public bool CanParse(ParserWorkItem workItem) => claims && workItem.SourceKind == sourceKind;
        public Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(outcome with { ParserId = ParserId });
        }
    }
}
