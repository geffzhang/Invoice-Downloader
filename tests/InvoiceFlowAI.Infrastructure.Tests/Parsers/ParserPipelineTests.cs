// Verifies ParserPipeline (design §3 / Task 8):
//   * Each of the four registry parsers produces a clean outcome from its
//     own success fixture.
//   * Missing-field fixture produces a Selected outcome with the
//     missing list populated.
//   * When two parsers both succeed, the pipeline emits
//     SPECIAL_PARSER_CONFLICT and routes to manual_review.
//   * When no parser matches a source kind, the pipeline emits
//     PARSER_NOT_FOUND.

using FluentAssertions;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Parsers;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Parsers;

public sealed class ParserPipelineTests
{
    private const string FixtureRoot = "../../../../../docs/superpowers/fixtures/parsers";

    [Fact]
    public async Task Railway_ticket_parser_parses_pdf_via_basic_fixture()
    {
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("railway-ticket", $"{FixtureRoot}/railway-ticket-basic.json", priority: 400, sourceKinds: new[] { "pdf" }),
        });
        var pipeline = new ParserPipeline(registry);
        var workItem = NewPdfWorkItem(documentId: "document-railway-1");

        var outcome = await pipeline.RunAsync(workItem, CancellationToken.None);

        outcome.Selected.Should().NotBeNull();
        outcome.Selected!.ParserId.Should().Be("railway-ticket");
        outcome.Selected.Invoice.Should().NotBeNull();
        outcome.Selected.Invoice!.Seller.Should().Be("中国铁路");
        outcome.Selected.Invoice.DocumentType.Should().Be(InvoiceFlowAI.Domain.Invoices.InvoiceDocumentType.TrainTicket);
        outcome.Selected.Invoice.InvoiceNumber.Should().Be("E12345678");
        outcome.Conflict.Should().BeNull();
    }

    [Fact]
    public async Task Accommodation_folio_parser_parses_pdf_via_basic_fixture()
    {
        var pipeline = NewPipeline();
        // Use a document id that no other parser's fixture knows about,
        // then run the accommodation parser in isolation.
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("accommodation-folio", $"{FixtureRoot}/accommodation-folio-basic.json", priority: 390, sourceKinds: new[] { "pdf" }),
        });
        var isolated = new ParserPipeline(registry);

        var outcome = await isolated.RunAsync(NewPdfWorkItem("document-folio-1"), CancellationToken.None);

        outcome.Selected.Should().NotBeNull();
        outcome.Selected!.ParserId.Should().Be("accommodation-folio");
        outcome.Selected.Invoice!.Seller.Should().Be("万丽酒店");
        outcome.Selected.Invoice.DocumentType.Should().Be(InvoiceFlowAI.Domain.Invoices.InvoiceDocumentType.HotelFolio);
    }

    [Fact]
    public async Task Foreign_invoice_parser_parses_image_via_basic_fixture()
    {
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("foreign-invoice", $"{FixtureRoot}/foreign-invoice-basic.json", priority: 380, sourceKinds: new[] { "image" }),
        });
        var pipeline = new ParserPipeline(registry);

        var outcome = await pipeline.RunAsync(NewImageWorkItem("document-foreign-1"), CancellationToken.None);

        outcome.Selected.Should().NotBeNull();
        outcome.Selected!.ParserId.Should().Be("foreign-invoice");
        outcome.Selected.Invoice!.DocumentType.Should().Be(InvoiceFlowAI.Domain.Invoices.InvoiceDocumentType.AirTicket);
        outcome.Selected.Invoice.InvoiceNumber.Should().Be("INV-DE-12345");
    }

    [Fact]
    public async Task Provider_special_layout_parser_parses_ofd_via_basic_fixture()
    {
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("provider-special-layout", $"{FixtureRoot}/provider-special-layout-basic.json", priority: 300, sourceKinds: new[] { "ofd" }),
        });
        var pipeline = new ParserPipeline(registry);

        var outcome = await pipeline.RunAsync(NewWorkItem("ofd", "document-special-1"), CancellationToken.None);

        outcome.Selected.Should().NotBeNull();
        outcome.Selected!.ParserId.Should().Be("provider-special-layout");
        outcome.Selected.Invoice!.DocumentType.Should().Be(InvoiceFlowAI.Domain.Invoices.InvoiceDocumentType.Other);
    }

    [Fact]
    public async Task Missing_field_fixture_yields_partial_outcome_with_missing_list()
    {
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("railway-ticket", $"{FixtureRoot}/missing-fields.json", priority: 400, sourceKinds: new[] { "pdf" }),
        });
        var pipeline = new ParserPipeline(registry);

        var outcome = await pipeline.RunAsync(NewPdfWorkItem("document-railway-2"), CancellationToken.None);

        outcome.Selected.Should().NotBeNull();
        outcome.Selected!.MissingFields.Should().Contain("taxAmount");
        outcome.Selected.MissingFields.Should().Contain("totalAmount");
        outcome.Selected.MissingFields.Should().Contain("invoiceNumber");
        outcome.Selected.MissingFields.Should().Contain("documentType");
        outcome.Conflict.Should().BeNull();
    }

    [Fact]
    public async Task Two_parsers_claiming_same_document_produces_special_parser_conflict()
    {
        // Two parsers that both succeed on the same pdf → manual_review.
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("railway-ticket", $"{FixtureRoot}/railway-ticket-basic.json", priority: 400, sourceKinds: new[] { "pdf" }),
            NewParser("accommodation-folio", $"{FixtureRoot}/accommodation-folio-basic.json", priority: 390, sourceKinds: new[] { "pdf" }),
        });
        var pipeline = new ParserPipeline(registry);

        var outcome = await pipeline.RunAsync(NewPdfWorkItem("document-1"), CancellationToken.None);

        outcome.Selected.Should().BeNull();
        outcome.Conflict.Should().NotBeNull();
        outcome.Conflict!.ReasonCode.Should().Be("SPECIAL_PARSER_CONFLICT");
        outcome.Conflict.RequiresManualReview.Should().BeTrue();
        outcome.Conflict.Candidates.Should().HaveCount(2);
        outcome.Conflict.Candidates.Select(c => c.ParserId).Should()
            .BeEquivalentTo(new[] { "railway-ticket", "accommodation-folio" });
    }

    [Fact]
    public async Task No_parser_for_source_kind_yields_parser_not_found()
    {
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("railway-ticket", $"{FixtureRoot}/railway-ticket-basic.json", priority: 400, sourceKinds: new[] { "pdf" }),
        });
        var pipeline = new ParserPipeline(registry);

        var outcome = await pipeline.RunAsync(NewWorkItem("xls", "document-xls-1"), CancellationToken.None);

        outcome.Selected.Should().BeNull();
        outcome.Conflict.Should().NotBeNull();
        outcome.Conflict!.ReasonCode.Should().Be("PARSER_NOT_FOUND");
    }

    private static ParserPipeline NewPipeline()
    {
        var registry = new ParserRegistry(new IParser[]
        {
            NewParser("railway-ticket", $"{FixtureRoot}/railway-ticket-basic.json", priority: 400, sourceKinds: new[] { "pdf" }),
            NewParser("accommodation-folio", $"{FixtureRoot}/accommodation-folio-basic.json", priority: 390, sourceKinds: new[] { "pdf" }),
            NewParser("foreign-invoice", $"{FixtureRoot}/foreign-invoice-basic.json", priority: 380, sourceKinds: new[] { "pdf", "image" }),
            NewParser("provider-special-layout", $"{FixtureRoot}/provider-special-layout-basic.json", priority: 300, sourceKinds: new[] { "pdf", "ofd", "xml", "url" }),
        });
        return new ParserPipeline(registry);
    }

    private static IParser NewParser(string parserId, string fixturePath, int priority, IReadOnlyList<string> sourceKinds) =>
        new FixtureBackedParser(parserId, "1.0", priority, sourceKinds, "PARSER_FAILED", fixturePath);

    private static ParserWorkItem NewPdfWorkItem(string documentId) =>
        NewWorkItem("pdf", documentId);

    private static ParserWorkItem NewImageWorkItem(string documentId) =>
        NewWorkItem("image", documentId);

    private static ParserWorkItem NewWorkItem(string sourceKind, string documentId) => new(
        Candidate: new DocumentCandidate(
            DocumentId: DocumentIdentity.Create(documentId),
            Sequence: 1,
            CorrelationId: "corr-1",
            SourceMessageUid: "uid-1",
            OriginalFileName: $"{documentId}.bin",
            ContentType: "application/octet-stream",
            ContentLength: 1024,
            ProcessingRevision: 1,
            SourceKind: sourceKind),
        DocumentId: documentId,
        SourceKind: sourceKind,
        DocumentBytes: new byte[] { 0x25, 0x50, 0x44, 0x46 });
}
