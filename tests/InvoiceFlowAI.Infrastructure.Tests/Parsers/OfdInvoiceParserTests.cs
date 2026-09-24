using System.IO.Compression;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Parsers;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Parsers;

public sealed class OfdInvoiceParserTests
{
    private const string InvoiceXml = "<Invoice><Header><InvoiceDate>2026-09-24</InvoiceDate><InvoiceNumber>OFD-100</InvoiceNumber><InvoiceType>Catering</InvoiceType></Header><EInvoiceData><BuyerName>Example Company</BuyerName><SellerName>Example Seller</SellerName><Amount>100</Amount><TaxAmount>6</TaxAmount><TotalAmount>106</TotalAmount></EInvoiceData></Invoice>";

    [Fact]
    public async Task Parses_original_invoice_xml_from_ofd_package()
    {
        var bytes = CreateOfd(("Doc_0/Other/original_invoice.xml", InvoiceXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice.Should().NotBeNull();
        outcome.Invoice!.Identity.Should().Be(DocumentIdentity.Create("ofd-candidate"));
        outcome.Invoice.InvoiceNumber.Should().Be("OFD-100");
        outcome.Invoice.DocumentType.Should().Be(InvoiceDocumentType.Catering);
    }

    [Fact]
    public async Task Uses_page_content_xml_when_original_invoice_is_missing()
    {
        var bytes = CreateOfd(("Doc_0/Pages/Page_0/Content.xml", InvoiceXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceNumber.Should().Be("OFD-100");
    }

    [Fact]
    public async Task Traverses_multiple_pages_until_invoice_xml_is_found()
    {
        var bytes = CreateOfd(
            ("Doc_0/Pages/Page_0/Content.xml", "<Page><TextObject>Cover page</TextObject></Page>"),
            ("Doc_0/Pages/Page_1/Content.xml", InvoiceXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceNumber.Should().Be("OFD-100");
    }

    [Fact]
    public async Task Resolves_document_root_and_page_baseloc_references()
    {
        const string ofdXml = "<OFD><DocBody><DocRoot>Package/Document.xml</DocRoot></DocBody></OFD>";
        const string documentXml = "<Document><Pages><Page ID=\"1\" BaseLoc=\"Pages/InvoiceData.xml\" /></Pages></Document>";
        var bytes = CreateOfd(
            ("OFD.xml", ofdXml),
            ("Package/Document.xml", documentXml),
            ("Package/Pages/InvoiceData.xml", InvoiceXml),
            ("Unreferenced/Content.xml", "<Invoice><InvoiceNumber>OTHER-999</InvoiceNumber></Invoice>"));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceNumber.Should().Be("OFD-100");
    }

    [Fact]
    public async Task Resolves_object_reference_to_custom_invoice_xml()
    {
        const string ofdXml = "<OFD><DocBody><DocRoot>Package/Document.xml</DocRoot></DocBody></OFD>";
        const string documentXml = "<Document><Pages><Page ID=\"page-1\" BaseLoc=\"Pages/Page.xml\" /></Pages></Document>";
        const string pageXml = "<Page><TextObject ID=\"page-object-1\" /></Page>";
        const string tagXml = "<Tags><CustomTag Name=\"InvoiceData\" ObjectRef=\"invoice-object-1\" /></Tags>";
        var referencedInvoiceXml = InvoiceXml.Replace("<Invoice>", "<Invoice ID=\"invoice-object-1\">", StringComparison.Ordinal);
        var bytes = CreateOfd(
            ("OFD.xml", ofdXml),
            ("Package/Document.xml", documentXml),
            ("Package/Pages/Page.xml", pageXml),
            ("Package/Tags/Custom.xml", tagXml),
            ("Package/Tags/Data/Fields.xml", referencedInvoiceXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceNumber.Should().Be("OFD-100");
    }

    [Fact]
    public async Task Conflicting_original_and_page_xml_returns_reviewable_failure()
    {
        const string conflictingXml = "<Invoice><Header><InvoiceDate>2026-09-24</InvoiceDate><InvoiceNumber>OFD-100</InvoiceNumber><InvoiceType>Catering</InvoiceType></Header><EInvoiceData><BuyerName>Example Company</BuyerName><SellerName>Example Seller</SellerName><Amount>101</Amount><TaxAmount>6</TaxAmount><TotalAmount>107</TotalAmount></EInvoiceData></Invoice>";
        var bytes = CreateOfd(
            ("Doc_0/Other/original_invoice.xml", InvoiceXml),
            ("Doc_0/Pages/Page_0/Content.xml", conflictingXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("OFD_XML_CONFLICT");
    }

    [Fact]
    public async Task Corrupt_package_returns_stable_failure()
    {
        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem([1, 2, 3, 4]), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("OFD_PACKAGE_INVALID");
    }

    [Fact]
    public async Task Incomplete_embedded_invoice_requests_extraction_fallback()
    {
        const string incompleteXml = "<Invoice><InvoiceInfo><InvoiceDate>2026-09-24</InvoiceDate><Amount>100</Amount></InvoiceInfo></Invoice>";
        var bytes = CreateOfd(("Doc_0/Other/original_invoice.xml", incompleteXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.NeedsFallback);
        outcome.Failure!.ReasonCode.Should().Be("INVOICE_NUMBER_MISSING");
    }

    [Fact]
    public async Task Rejects_package_with_traversal_entry()
    {
        var bytes = CreateOfd(("../outside.xml", InvoiceXml), ("Doc_0/Other/original_invoice.xml", InvoiceXml));

        var outcome = await new OfdInvoiceParser().ParseAsync(NewWorkItem(bytes), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("OFD_PACKAGE_INVALID");
    }

    [Fact]
    public void Package_reader_rejects_dot_segment_paths()
    {
        var bytes = CreateOfd(
            ("Doc_0/Other/original_invoice.xml", InvoiceXml),
            ("Doc_0/Other/./original_invoice.xml", InvoiceXml));

        var act = () => new OfdPackageReader().ReadXmlEntries(bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Package_reader_rejects_case_insensitive_duplicate_paths()
    {
        var bytes = CreateOfd(
            ("Doc_0/Other/original_invoice.xml", InvoiceXml),
            ("Doc_0/Other/ORIGINAL_INVOICE.XML", InvoiceXml));

        var act = () => new OfdPackageReader().ReadXmlEntries(bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Package_reader_rejects_duplicate_object_ids()
    {
        var bytes = CreateOfd(
            ("OFD.xml", "<OFD><DocBody><DocRoot>Document.xml</DocRoot></DocBody></OFD>"),
            ("Document.xml", "<Document><Page ID=\"duplicate\" BaseLoc=\"Page.xml\" /></Document>"),
            ("Page.xml", "<Page><TextObject ID=\"duplicate\" /></Page>"));

        var act = () => new OfdPackageReader().ReadInvoiceXmlEntries(bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Package_reader_rejects_documents_with_too_many_pages()
    {
        var pages = string.Concat(Enumerable.Range(0, OfdPackageReader.MaximumPageCount + 1)
            .Select(index => $"<Page ID=\"page-{index}\" BaseLoc=\"Pages/Page-{index}.xml\" />"));
        var bytes = CreateOfd(
            ("OFD.xml", "<OFD><DocBody><DocRoot>Document.xml</DocRoot></DocBody></OFD>"),
            ("Document.xml", $"<Document><Pages>{pages}</Pages></Document>"));

        var act = () => new OfdPackageReader().ReadInvoiceXmlEntries(bytes);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Package_reader_rejects_oversized_entry()
    {
        var oversized = new string('x', checked((int)OfdPackageReader.MaximumEntryBytes + 1));
        var bytes = CreateOfd(("Doc_0/Other/large.xml", oversized));

        var act = () => new OfdPackageReader().ReadXmlEntries(bytes);

        act.Should().Throw<InvalidDataException>();
    }

    private static byte[] CreateOfd(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private static ParserWorkItem NewWorkItem(byte[] bytes)
    {
        var identity = DocumentIdentity.Create("ofd-candidate");
        var candidate = new DocumentCandidate(
            identity, 1, "correlation", "uid-1", "invoice.ofd", "application/ofd",
            bytes.Length, 0, "ofd");
        return new ParserWorkItem(candidate, identity.Value, "ofd", bytes);
    }
}