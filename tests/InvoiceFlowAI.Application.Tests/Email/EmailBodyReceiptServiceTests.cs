// Verifies EmailBodyReceiptService (design §3 / Task 8):
//   * Trace must include InputKind=EmailBodyReceipt
//   * Email content is excluded from traces and no model is required.
//   * An unrecognized email body without a working model returns
//     EMAIL_BODY_RECEIPT_UNRECOGNIZED.

using FluentAssertions;
using InvoiceFlowAI.Application.Email;
using InvoiceFlowAI.Application.Extraction;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Email;

public sealed class EmailBodyReceiptServiceTests
{
    [Fact]
    public async Task Receipt_miss_continues_to_attachments_without_leaking_email_content_to_trace()
    {
        var service = new EmailBodyReceiptService();
        const string subject = "private-subject-sentinel";
        const string sender = "private-sender-sentinel@example.com";
        const string body = "private-body-sentinel";

        var outcome = await service.ExtractAsync(
            new EmailBodyReceiptRequest("uid-private", subject, sender, body), CancellationToken.None);

        outcome.Invoice.Should().BeNull();
        outcome.Failure!.ReasonCode.Should().Be("EMAIL_BODY_RECEIPT_UNRECOGNIZED");
        outcome.ContinueWithAttachments.Should().BeTrue();
        outcome.Trace.Values.Should().NotContain(value =>
            value.Contains(subject, StringComparison.Ordinal)
            || value.Contains(sender, StringComparison.Ordinal)
            || value.Contains(body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receipt_trace_records_input_kind_email_body_receipt()
    {
        var outcome = await new EmailBodyReceiptService().ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Trace.Should().ContainKey("InputKind");
        outcome.Trace["InputKind"].Should().Be("EmailBodyReceipt");
    }

    [Fact]
    public async Task Canonical_baiwang_receipt_is_complete_for_normal_acceptance_without_model_dependency()
    {
        var request = new EmailBodyReceiptRequest(
            "uid-100", "百望电子发票通知", "notice@baiwang.com",
            "发票号码 12345678，开票日期 2026-09-22，购买方名称：Example Co，销售方名称：Example Seller，金额 100.00，发票类型：Catering。");
        var outcome = await new EmailBodyReceiptService().ExtractAsync(request, CancellationToken.None);

        outcome.Invoice.Should().NotBeNull();
        outcome.Invoice!.InvoiceNumber.Should().Be("12345678");
        outcome.Invoice.InvoiceDate.Should().Be(new DateOnly(2026, 9, 22));
        outcome.Invoice.Purchaser.Should().Be("Example Co");
        outcome.Invoice.Seller.Should().Be("Example Seller");
        outcome.Invoice.Amount.Should().Be(100m);
        outcome.Invoice.DocumentType.Should().Be(InvoiceDocumentType.Catering);
        outcome.Invoice.Identity.Should().Be(DocumentIdentity.Create("uid-100"));
        outcome.Invoice.Confidence.Should().BeGreaterThanOrEqualTo(0.60m);
        outcome.Trace["Parser"].Should().Be("deterministic-baiwang");
        outcome.Failure.Should().BeNull();

        var candidate = new DocumentCandidate(
            DocumentIdentity.Create("uid-100"), 1, "corr-1", "uid-100", "", "message/rfc822", 0, 0, "email-body");
        var acceptance = new InvoiceAcceptanceService().Evaluate(new InvoiceAcceptanceRequest(
            candidate, outcome.Invoice, new InvoiceAcceptancePolicy(), "Example Co", false, "deterministic-baiwang"));
        acceptance.Disposition.Should().Be(AcceptanceDisposition.Accepted);
    }

    [Fact]
    public async Task Baiwang_receipt_prefers_tax_inclusive_total_over_pre_tax_amount()
    {
        var outcome = await new EmailBodyReceiptService().ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Invoice!.TotalAmount.Should().Be(113m);
    }

    [Fact]
    public async Task Receipt_trace_records_uid_but_never_subject_sender_or_body()
    {
        var request = new EmailBodyReceiptRequest(
            "uid-100", "subject-private-marker 百望", "sender-private-marker@baiwang.com", "body-private-marker");

        var outcome = await new EmailBodyReceiptService().ExtractAsync(request, CancellationToken.None);

        outcome.Trace["SourceMessageUid"].Should().Be("uid-100");
        outcome.Trace.Values.Should().NotContain(value =>
            value.Contains("private-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unrecognized_email_yields_failure_and_continues_to_attachment_parsing()
    {
        var outcome = await new EmailBodyReceiptService().ExtractAsync(
            new EmailBodyReceiptRequest("uid-200", "Hello friend", "alice@example.com", "Just saying hi"),
            CancellationToken.None);

        outcome.Invoice.Should().BeNull();
        outcome.Failure.Should().NotBeNull();
        outcome.Failure!.ReasonCode.Should().Be("EMAIL_BODY_RECEIPT_UNRECOGNIZED");
        outcome.Trace["Parser"].Should().Be("none");
        outcome.ContinueWithAttachments.Should().BeTrue();
    }

    [Fact]
    public async Task Incomplete_baiwang_receipt_continues_to_attachment_parsing()
    {
        var outcome = await new EmailBodyReceiptService().ExtractAsync(
            new EmailBodyReceiptRequest("uid-300", "百望电子发票通知", "notice@baiwang.com",
                "发票号码 12345678，开票日期 2026-09-22，金额 100.00。"),
            CancellationToken.None);

        outcome.Invoice.Should().BeNull();
        outcome.Failure!.ReasonCode.Should().Be("EMAIL_BODY_RECEIPT_UNRECOGNIZED");
        outcome.ContinueWithAttachments.Should().BeTrue();
    }

    private static EmailBodyReceiptRequest NewBaiwangRequest() => new(
        SourceMessageUid: "uid-100",
        Subject: "百望电子发票通知",
        Sender: "notice@baiwang.com",
        BodyText: "金额 100.00 元，价税合计 113.00，发票号码 12345678，开票日期 2026-09-22，购买方名称：Example Co，销售方名称：Example Seller，发票类型：Catering。");
}
