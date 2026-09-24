using FluentAssertions;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Parsers;

public sealed class ParserPipelineRoutingTests
{
    [Fact]
    public async Task Parser_that_does_not_claim_document_is_not_executed()
    {
        var parser = new FakeParser(canParse: false);
        var registry = new ParserRegistry(new[] { parser });
        var pipeline = new ParserPipeline(registry);

        var outcome = await pipeline.RunAsync(NewWorkItem(), CancellationToken.None);

        parser.ParseCalls.Should().Be(0);
        outcome.Selected.Should().BeNull();
        outcome.Conflict.Should().NotBeNull();
        outcome.Conflict!.ReasonCode.Should().Be("PARSER_NOT_FOUND");
    }

    [Fact]
    public async Task Only_highest_priority_claiming_parser_is_executed()
    {
        var high = new FakeParser("high", canParse: true, priority: 200);
        var low = new FakeParser("low", canParse: true, priority: 100);
        var pipeline = new ParserPipeline(new ParserRegistry(new[] { low, high }));

        var outcome = await pipeline.RunAsync(NewWorkItem(), CancellationToken.None);

        outcome.Selected!.ParserId.Should().Be("high");
        high.ParseCalls.Should().Be(1);
        low.ParseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Same_highest_priority_claims_produce_conflict_without_parsing()
    {
        var first = new FakeParser("first", canParse: true, priority: 200);
        var second = new FakeParser("second", canParse: true, priority: 200);
        var pipeline = new ParserPipeline(new ParserRegistry(new[] { first, second }));

        var outcome = await pipeline.RunAsync(NewWorkItem(), CancellationToken.None);

        outcome.Selected.Should().BeNull();
        outcome.Conflict!.ReasonCode.Should().Be("SPECIAL_PARSER_CONFLICT");
        outcome.Conflict.Candidates.Select(candidate => candidate.ParserId)
            .Should().Equal("first", "second");
        first.ParseCalls.Should().Be(0);
        second.ParseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Claimed_parser_failure_is_not_replaced_by_lower_priority_parser()
    {
        var claimed = new FakeParser("claimed", canParse: true, priority: 200,
            disposition: ParserOutcomeDisposition.Failed,
            failure: new CandidateFailure("CLAIMED_PARSE_FAILED", FailureScope.Candidate,
                FailureCategory.Document, Retryable: false, SafeMessage: "Parser failed."));
        var lower = new FakeParser("lower", canParse: true, priority: 100);
        var pipeline = new ParserPipeline(new ParserRegistry(new[] { lower, claimed }));

        var outcome = await pipeline.RunAsync(NewWorkItem(), CancellationToken.None);

        outcome.Selected!.ParserId.Should().Be("claimed");
        outcome.Selected.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        lower.ParseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Needs_fallback_disposition_is_preserved_for_generic_route()
    {
        var parser = new FakeParser("unresolved", canParse: true, priority: 200,
            disposition: ParserOutcomeDisposition.NeedsFallback);
        var pipeline = new ParserPipeline(new ParserRegistry(new[] { parser }));

        var outcome = await pipeline.RunAsync(NewWorkItem(), CancellationToken.None);

        outcome.Selected!.Disposition.Should().Be(ParserOutcomeDisposition.NeedsFallback);
    }

    private static ParserWorkItem NewWorkItem()
    {
        var documentId = DocumentIdentity.Create("document-1");
        return new ParserWorkItem(
            Candidate: new DocumentCandidate(
                DocumentId: documentId,
                Sequence: 1,
                CorrelationId: "corr-1",
                SourceMessageUid: "uid-1",
                OriginalFileName: "invoice.pdf",
                ContentType: "application/pdf",
                ContentLength: 4,
                ProcessingRevision: 1,
                SourceKind: "pdf"),
            DocumentId: documentId.Value,
            SourceKind: "pdf",
            DocumentBytes: new byte[] { 0x25, 0x50, 0x44, 0x46 });
    }

    private sealed class FakeParser : IParser
    {
        private readonly bool _canParse;
        private readonly ParserOutcomeDisposition _disposition;
        private readonly CandidateFailure? _failure;

        public FakeParser(
            string parserId = "fake",
            bool canParse = false,
            int priority = 100,
            ParserOutcomeDisposition disposition = ParserOutcomeDisposition.Resolved,
            CandidateFailure? failure = null)
        {
            ParserId = parserId;
            _canParse = canParse;
            Priority = priority;
            _disposition = disposition;
            _failure = failure;
        }

        public string ParserId { get; }
        public string Version => "1.0";
        public int Priority { get; }
        public IReadOnlyList<string> SourceKinds => new[] { "pdf" };
        public string FailureCode => "FAKE_FAILED";
        public int ParseCalls { get; private set; }

        public bool CanParse(ParserWorkItem workItem) => _canParse;

        public Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
        {
            ParseCalls++;
            return Task.FromResult(new ParserOutcome(
                ParserId,
                Version,
                Invoice: null,
                MissingFields: Array.Empty<string>(),
                Failure: _failure,
                Disposition: _disposition));
        }
    }
}