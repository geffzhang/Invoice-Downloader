using FluentAssertions;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Extraction;

public sealed class InvoiceAcceptanceServiceTests
{
    [Fact]
    public void Identity_mismatch_is_rejected()
    {
        var candidate = NewCandidate("candidate-1");
        var invoice = NewInvoice("different-document");

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("IDENTITY_MISMATCH");
    }

    [Fact]
    public void Unknown_purchaser_requires_manual_review_for_non_exempt_invoice()
    {
        var candidate = NewCandidate("candidate-2");
        var invoice = NewInvoice("candidate-2") with
        {
            Purchaser = "未知购买方",
        };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("PURCHASER_UNKNOWN");
    }

    [Fact]
    public void Non_target_purchaser_is_retained_not_rejected()
    {
        var candidate = NewCandidate("candidate-nontarget");
        var invoice = NewInvoice("candidate-nontarget") with { Purchaser = "Other Company Ltd" };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Retained);
        result.Document!.DocumentType.Should().Be(InvoiceDocumentType.NonTargetCompanyInvoice);
        result.ReasonCode.Should().Be("PURCHASER_NOT_TARGET");
    }

    [Fact]
    public void Transport_invoice_uses_departure_date_as_effective_invoice_date()
    {
        var candidate = NewCandidate("candidate-train-date");
        var invoice = NewInvoice("candidate-train-date") with
        {
            DocumentType = InvoiceDocumentType.TrainTicket,
            InvoiceDate = new DateOnly(2026, 9, 20),
            Route = new InvoiceRoute(InvoiceRouteDirection.Inbound, new DateOnly(2026, 9, 24)),
        };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Accepted);
        result.Document!.InvoiceDate.Should().Be(new DateOnly(2026, 9, 24));
    }

    [Fact]
    public void Negative_amount_without_credit_flag_is_rejected()
    {
        var candidate = NewCandidate("candidate-3");
        var invoice = NewInvoice("candidate-3") with { Amount = -10m, TotalAmount = -10m };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("INVOICE_AMOUNT_INVALID");
    }

    [Fact]
    public void Negative_amount_without_credit_flag_stays_rejected_when_policy_is_relaxed()
    {
        var candidate = NewCandidate("candidate-relaxed-negative");
        var invoice = NewInvoice("candidate-relaxed-negative") with { Amount = -10m, TotalAmount = -10m };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(AllowNegativeAmountOnlyWithCreditFlag: false),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("INVOICE_AMOUNT_INVALID");
    }

    [Fact]
    public void Zero_amount_regular_invoice_is_rejected()
    {
        var candidate = NewCandidate("candidate-zero");
        var invoice = NewInvoice("candidate-zero") with { Amount = 0m, TotalAmount = 0m };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("INVOICE_AMOUNT_ZERO");
    }

    [Theory]
    [InlineData(InvoiceFlags.CreditNote)]
    [InlineData(InvoiceFlags.Cancellation)]
    public void Zero_amount_credit_or_cancellation_invoice_requires_manual_review(InvoiceFlags flag)
    {
        var candidate = NewCandidate("candidate-zero-credit");
        var invoice = NewInvoice("candidate-zero-credit") with
        {
            Amount = 0m,
            TotalAmount = 0m,
            Flags = flag,
        };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("INVOICE_AMOUNT_ZERO");
    }

    [Fact]
    public void Negative_amount_with_credit_flag_is_accepted()
    {
        var candidate = NewCandidate("candidate-4");
        var invoice = NewInvoice("candidate-4") with
        {
            Amount = -10m,
            TotalAmount = -10m,
            Flags = InvoiceFlags.CreditNote,
        };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Accepted);
    }

    [Fact]
    public void Low_confidence_is_manual_review()
    {
        var candidate = NewCandidate("candidate-5");
        var invoice = NewInvoice("candidate-5") with { Confidence = 0.59m };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("ACCEPTANCE_LOW_CONFIDENCE");
    }

    [Fact]
    public void Tax_total_mismatch_is_rejected()
    {
        var candidate = NewCandidate("candidate-6");
        var invoice = NewInvoice("candidate-6") with { TaxAmount = 2m, TotalAmount = 15m };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("TAX_TOTAL_MISMATCH");
    }

    [Fact]
    public void Missing_seller_is_rejected_for_catering_invoice()
    {
        var candidate = NewCandidate("candidate-7");
        var invoice = NewInvoice("candidate-7") with { Seller = "" };

        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("INVOICE_SELLER_MISSING");
    }

    [Fact]
    public void Invalid_date_is_rejected()
    {
        var candidate = NewCandidate("candidate-8");
        var invoice = NewInvoice("candidate-8") with { InvoiceDate = DateOnly.MinValue };

        var result = Evaluate(candidate, invoice);

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("INVOICE_DATE_INVALID");
    }

    [Fact]
    public void Unsupported_document_type_is_rejected()
    {
        var candidate = NewCandidate("candidate-9");
        var invoice = NewInvoice("candidate-9") with { DocumentType = (InvoiceDocumentType)500 };

        var result = Evaluate(candidate, invoice);

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("DOCUMENT_TYPE_UNKNOWN");
    }

    [Fact]
    public void Non_invoice_content_is_rejected()
    {
        var candidate = NewCandidate("candidate-10");
        var invoice = NewInvoice("candidate-10") with { IsInvoice = false };

        var result = Evaluate(candidate, invoice);

        result.Disposition.Should().Be(AcceptanceDisposition.Rejected);
        result.ReasonCode.Should().Be("DOCUMENT_NOT_INVOICE");
    }

    [Fact]
    public void Zero_amount_credit_note_requires_manual_review()
    {
        var candidate = NewCandidate("candidate-11");
        var invoice = NewInvoice("candidate-11") with
        {
            Amount = 0m,
            TotalAmount = 0m,
            Flags = InvoiceFlags.CreditNote,
        };

        var result = Evaluate(candidate, invoice);

        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("INVOICE_AMOUNT_ZERO");
    }

    [Fact]
    public void Vision_fallback_requires_manual_review_without_field_provenance()
    {
        var result = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            NewCandidate("candidate-12"),
            NewInvoice("candidate-12"),
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: true,
            SourceParser: "deepseek-vision"));

        result.Disposition.Should().Be(AcceptanceDisposition.ManualReview);
        result.ReasonCode.Should().Be("ACCEPTANCE_LOW_CONFIDENCE");
    }

    private static InvoiceAcceptanceResult Evaluate(DocumentCandidate candidate, InvoiceDocument invoice) =>
        new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate,
            invoice,
            new InvoiceAcceptancePolicy(),
            CompanyName: "Example Company",
            IsVisionFallback: false,
            SourceParser: "test"));

    private static DocumentCandidate NewCandidate(string id) => new(
        DocumentIdentity.Create(id), 1, $"corr-{id}", "uid-1", "invoice.pdf",
        "application/pdf", 100, 0, "attachment");

    private static InvoiceDocument NewInvoice(string id) => new(
        DocumentId: id,
        InvoiceDate: new DateOnly(2026, 9, 24),
        Purchaser: "Example Company",
        Seller: "Example Seller",
        Amount: 10m,
        TaxAmount: 0m,
        TotalAmount: 10m,
        InvoiceCode: null,
        InvoiceNumber: "INV-1",
        DocumentType: InvoiceDocumentType.Catering,
        Category: "餐饮",
        Route: InvoiceRoute.Inbound,
        Items: Array.Empty<InvoiceItem>(),
        SourceFileName: "invoice.pdf",
        ContentHash: "hash")
    {
        Confidence = 0.95m,
    };
}