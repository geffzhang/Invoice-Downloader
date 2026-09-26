using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Extraction;

public sealed class InvoiceNormalizerTests
{
    [Fact]
    public void Normalize_preserves_candidate_identity_and_trims_text_fields()
    {
        var identity = DocumentIdentity.Create("candidate-1");
        var invoice = new InvoiceDocument(
            DocumentId: "candidate-1",
            InvoiceDate: new DateOnly(2026, 9, 24),
            Purchaser: "  Example Purchaser  ",
            Seller: "  Example Seller  ",
            Amount: 12.34m,
            TaxAmount: null,
            TotalAmount: 12.34m,
            InvoiceCode: " ",
            InvoiceNumber: " INV-1 ",
            DocumentType: InvoiceDocumentType.Catering,
            Category: " 餐饮 ",
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: "invoice.pdf",
            ContentHash: "hash");

        var result = new InvoiceNormalizer().Normalize(invoice, identity);

        result.Identity.Should().Be(identity);
        result.DocumentId.Should().Be(identity.Value);
        result.Purchaser.Should().Be("Example Purchaser");
        result.Seller.Should().Be("Example Seller");
        result.InvoiceCode.Should().BeEmpty();
        result.InvoiceNumber.Should().Be("INV-1");
        result.Category.Should().Be("餐饮");
        result.Amount.Should().Be(12.34m);
    }
}