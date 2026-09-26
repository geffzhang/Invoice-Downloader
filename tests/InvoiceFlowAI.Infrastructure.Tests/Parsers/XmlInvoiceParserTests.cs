using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Parsers;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Parsers;

public sealed class XmlInvoiceParserTests
{
    private readonly XmlInvoiceParser _parser = new();

    [Theory]
    [InlineData("<Invoice><Header><InvoiceDate>2026-09-24</InvoiceDate><InvoiceNumber>INV-100</InvoiceNumber><InvoiceType>Catering</InvoiceType></Header><EInvoiceData><BuyerName>Example Company</BuyerName><SellerName>Example Seller</SellerName><Amount>100.00</Amount><TaxAmount>6.00</TaxAmount><TotalAmount>106.00</TotalAmount></EInvoiceData></Invoice>")]
    [InlineData("<Invoice><InvoiceInfo><InvoiceDate>2026-09-24</InvoiceDate><InvoiceNumber>INV-100</InvoiceNumber><InvoiceType>Catering</InvoiceType><Amount>100.00</Amount><TaxAmount>6.00</TaxAmount><TotalAmount>106.00</TotalAmount></InvoiceInfo><BuyerInfo><BuyerName>Example Company</BuyerName></BuyerInfo><SellerInfo><SellerName>Example Seller</SellerName></SellerInfo></Invoice>")]
    public async Task Parses_supported_xml_invoice_layouts(string xml)
    {
        var identity = DocumentIdentity.Create("xml-candidate-1");
        var outcome = await _parser.ParseAsync(NewWorkItem(xml, identity), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Failure.Should().BeNull();
        outcome.Invoice.Should().NotBeNull();
        outcome.Invoice!.Identity.Should().Be(identity);
        outcome.Invoice.InvoiceNumber.Should().Be("INV-100");
        outcome.Invoice.Amount.Should().Be(100m);
        outcome.Invoice.TotalAmount.Should().Be(106m);
        outcome.Invoice.DocumentType.Should().Be(InvoiceDocumentType.Catering);
    }

    [Fact]
    public async Task Missing_invoice_number_requests_generic_fallback()
    {
        var xml = "<Invoice><InvoiceInfo><InvoiceDate>2026-09-24</InvoiceDate><Amount>100</Amount></InvoiceInfo></Invoice>";

        var outcome = await _parser.ParseAsync(NewWorkItem(xml), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.NeedsFallback);
        outcome.Failure!.ReasonCode.Should().Be("INVOICE_NUMBER_MISSING");
    }

    [Theory]
    [InlineData("<Invoice><InvoiceInfo>")]
    [InlineData("<!DOCTYPE Invoice [<!ENTITY xxe SYSTEM 'file:///windows/win.ini'>]><Invoice><InvoiceInfo><SellerName>&xxe;</SellerName></InvoiceInfo></Invoice>")]
    public async Task Malformed_or_external_entity_xml_is_rejected(string xml)
    {
        var outcome = await _parser.ParseAsync(NewWorkItem(xml), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("XML_INVOICE_INVALID");
        outcome.Failure.SafeMessage.Should().NotContain("windows");
    }

    [Fact]
    public async Task Excessive_xml_nesting_returns_stable_failure()
    {
        var xml = string.Empty;
        for (var index = 0; index < 70; index++) xml += $"<N{index}>";
        xml += "<InvoiceNumber>INV-1</InvoiceNumber>";
        for (var index = 69; index >= 0; index--) xml += $"</N{index}>";

        var outcome = await _parser.ParseAsync(NewWorkItem(xml), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("XML_INVOICE_INVALID");
    }

    [Fact]
    public void Claims_xml_source_kind_only()
    {
        _parser.CanParse(NewWorkItem("<Invoice />")).Should().BeTrue();
        _parser.CanParse(NewWorkItem("<Invoice />") with { SourceKind = "pdf" }).Should().BeFalse();
    }

    private static ParserWorkItem NewWorkItem(string xml, DocumentIdentity? identity = null)
    {
        var documentIdentity = identity ?? DocumentIdentity.Create("xml-candidate-test");
        var candidate = new DocumentCandidate(
            documentIdentity, 1, "correlation", "uid-1", "invoice.xml", "application/xml",
            Encoding.UTF8.GetByteCount(xml), 0, "xml");
        return new ParserWorkItem(candidate, documentIdentity.Value, "xml", Encoding.UTF8.GetBytes(xml));
    }
}