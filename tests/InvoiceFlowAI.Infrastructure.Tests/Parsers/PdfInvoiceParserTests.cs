using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Parsers;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Parsers;

public sealed class PdfInvoiceParserTests
{
    [Fact]
    public async Task Parses_searchable_chinese_e_invoice_text_without_ocr()
    {
        const string text = "VAT Invoice\nInvoice Number: 123456789012\nInvoice Date: 2026-09-24\nPurchaser: Example Company\nSeller: Example Seller\nAmount: 100.00\nTax Amount: 6.00\nTotal Amount: 106.00";
        var outcome = await new PdfInvoiceParser().ParseAsync(NewWorkItem(CreateTextPdf(text)), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice.Should().NotBeNull();
        outcome.Invoice!.InvoiceNumber.Should().Be("123456789012");
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2026, 9, 24));
        outcome.Invoice.Amount.Should().Be(100m);
        outcome.Invoice.TaxAmount.Should().Be(6m);
        outcome.Invoice.TotalAmount.Should().Be(106m);
        outcome.Invoice.Identity.Should().Be(DocumentIdentity.Create("pdf-candidate"));
    }

    [Fact]
    public async Task Empty_or_scanned_pdf_text_requests_fallback()
    {
        var outcome = await new PdfInvoiceParser().ParseAsync(NewWorkItem(CreateTextPdf(" ")), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.NeedsFallback);
        outcome.Failure!.ReasonCode.Should().Be("PDF_TEXT_INSUFFICIENT");
    }

    [Fact]
    public async Task Malformed_pdf_returns_stable_failure()
    {
        var outcome = await new PdfInvoiceParser().ParseAsync(NewWorkItem(Encoding.ASCII.GetBytes("not a pdf")), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("PDF_INVALID");
    }

    [Fact]
    public async Task Excessive_embedded_text_returns_stable_limit_failure()
    {
        var parser = new PdfInvoiceParser(new PdfTextExtractor(new PdfTextExtractionOptions(MaximumTextCharacters: 8)),
            new PdfInvoiceSegmenter(), new PdfFieldParser());

        var outcome = await parser.ParseAsync(NewWorkItem(CreateTextPdf("Invoice Number: 1234567890")), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Failed);
        outcome.Failure!.ReasonCode.Should().Be("PDF_TEXT_LIMIT_EXCEEDED");
    }

    [Fact]
    public void Segmenter_splits_repeated_invoice_markers_and_preserves_order()
    {
        var pages = new[]
        {
            new PdfPageText(1, "发票号码: 11111111\n销售方名称: Seller A\n发票号码: 22222222\n销售方名称: Seller B"),
            new PdfPageText(2, "发票号码: 33333333\n销售方名称: Seller C"),
        };

        var segments = new PdfInvoiceSegmenter().Segment(pages);

        segments.Select(segment => segment.InvoiceNumber).Should().Equal("11111111", "22222222", "33333333");
        segments.Select(segment => segment.StartPage).Should().Equal(1, 1, 2);
        segments.Select(segment => segment.Text).Should().OnlyContain(text => text.Contains("销售方名称", StringComparison.Ordinal));
    }

    [Fact]
    public void Field_parser_normalizes_chinese_labels_and_red_letter_amounts()
    {
        const string text = "电子发票\n发票号码: 123456789012\n开票日期: 2026年09月24日\n销售方名称: Example Seller\n金额: (1,234.50)\n税额: 0.00\n价税合计: (1,234.50)";

        var fields = new PdfFieldParser().Parse(text);

        fields.InvoiceNumber.Should().Be("123456789012");
        fields.InvoiceDate.Should().Be(new DateOnly(2026, 9, 24));
        fields.Amount.Should().Be(-1234.50m);
        fields.TotalAmount.Should().Be(-1234.50m);
        fields.DocumentType.Should().Be(InvoiceDocumentType.VatInvoice);
    }

    [Fact]
    public void Field_parser_removes_redundant_seller_name_prefix()
    {
        const string text = "发票号码: 123456789012\n开票日期: 2026-09-24\n销售方名称: 名称:北京利通出行科技有限公司\n金额: 10.00";

        var fields = new PdfFieldParser().Parse(text);

        fields.Seller.Should().Be("北京利通出行科技有限公司");
    }

    [Fact]
    public async Task Parses_legacy_invoice_code_and_digital_invoice_title()
    {
        const string text = "VAT Invoice\nInvoice Code: 044001900111\nInvoice Number: 123456789012\nInvoice Date: 2026-09-24\nPurchaser: Example Company\nSeller: Example Seller\nAmount: 100.00\nTax Amount: 6.00\nTotal Amount: 106.00";

        var outcome = await new PdfInvoiceParser().ParseAsync(NewWorkItem(CreateTextPdf(text)), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.Resolved);
        outcome.Invoice!.InvoiceCode.Should().Be("044001900111");
        outcome.Invoice.DocumentType.Should().Be(InvoiceDocumentType.VatInvoice);
    }

    [Fact]
    public async Task Multiple_invoice_segments_request_fallback_instead_of_dropping_later_invoices()
    {
        const string text = "Invoice Number: 111111111111\nInvoice Date: 2026-09-24\nSeller: Seller A\nAmount: 100.00\nInvoice Number: 222222222222\nInvoice Date: 2026-09-25\nSeller: Seller B\nAmount: 200.00";

        var outcome = await new PdfInvoiceParser().ParseAsync(NewWorkItem(CreateTextPdf(text)), CancellationToken.None);

        outcome.Disposition.Should().Be(ParserOutcomeDisposition.NeedsFallback);
        outcome.Invoice.Should().BeNull();
        outcome.Failure!.ReasonCode.Should().Be("PDF_MULTIPLE_INVOICES");
    }

    [Fact]
    public void Field_parser_recognizes_digital_invoice_title()
    {
        var fields = new PdfFieldParser().Parse("数电发票（普通发票）\n发票号码: 123456789012\n开票日期: 2026年09月24日\n销售方名称: Example Seller\n金额: 100.00");

        fields.DocumentType.Should().Be(InvoiceDocumentType.VatInvoice);
    }

    private static ParserWorkItem NewWorkItem(byte[] bytes)
    {
        var identity = DocumentIdentity.Create("pdf-candidate");
        var candidate = new DocumentCandidate(identity, 1, "correlation", "uid-1", "invoice.pdf",
            "application/pdf", bytes.Length, 0, "pdf");
        return new ParserWorkItem(candidate, identity.Value, "pdf", bytes);
    }

    private static byte[] CreateTextPdf(string text)
    {
        var escapedText = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var streamContent = $"BT /F1 10 Tf 30 760 Td ({escapedText}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(streamContent)} >>\nstream\n{streamContent}\nendstream",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}