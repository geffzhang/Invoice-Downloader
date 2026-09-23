// Verifies EmailBodyReceiptService (design §3 / Task 8):
//   * Trace must include InputKind=EmailBodyReceipt
//   * SourceMessageUid, Subject, Sender are recorded in the trace
//   * 401 / 429 / timeout from the chat service do not fail the receipt —
//     the heuristic parser still surfaces an outcome, and the trace
//     records the AI error code so the audit log can correlate.
//   * An unrecognized email body without a working model returns
//     EMAIL_BODY_RECEIPT_UNRECOGNIZED.

using FluentAssertions;
using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Email;
using InvoiceFlowAI.Contracts.Errors;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Email;

public sealed class EmailBodyReceiptServiceTests
{
    [Fact]
    public async Task Receipt_trace_records_input_kind_email_body_receipt()
    {
        var harness = new Harness(chat: new FakeChat());

        var outcome = await harness.Service.ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Trace.Should().ContainKey("InputKind");
        outcome.Trace["InputKind"].Should().Be("EmailBodyReceipt");
    }

    [Fact]
    public async Task Receipt_trace_includes_source_message_uid_subject_sender()
    {
        var harness = new Harness(chat: new FakeChat());

        var outcome = await harness.Service.ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Trace["SourceMessageUid"].Should().Be("uid-100");
        outcome.Trace["Subject"].Should().Contain("百望");
        outcome.Trace["Sender"].Should().Be("notice@baiwang.com");
    }

    [Fact]
    public async Task Baiwang_email_body_extracts_invoice_number_via_heuristic()
    {
        var harness = new Harness(chat: new FakeChat());

        var outcome = await harness.Service.ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Invoice.Should().NotBeNull();
        outcome.Invoice!.InvoiceNumber.Should().Be("12345678");
        outcome.Trace["Parser"].Should().Be("heuristic-baiwang");
        outcome.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Chat_401_does_not_fail_receipt_heuristic_still_produces_invoice()
    {
        var harness = new Harness(chat: new FakeChat(ChatCompletionErrorCode.Unauthorized));

        var outcome = await harness.Service.ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Invoice.Should().NotBeNull();
        outcome.Invoice!.InvoiceNumber.Should().Be("12345678");
        outcome.Trace["AiErrorCode"].Should().Be("Unauthorized");
    }

    [Fact]
    public async Task Chat_429_does_not_fail_receipt_heuristic_still_produces_invoice()
    {
        var harness = new Harness(chat: new FakeChat(ChatCompletionErrorCode.RateLimited));

        var outcome = await harness.Service.ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Invoice.Should().NotBeNull();
        outcome.Trace["AiErrorCode"].Should().Be("RateLimited");
    }

    [Fact]
    public async Task Chat_timeout_does_not_fail_receipt_heuristic_still_produces_invoice()
    {
        var harness = new Harness(chat: new FakeChat(ChatCompletionErrorCode.Timeout));

        var outcome = await harness.Service.ExtractAsync(NewBaiwangRequest(), CancellationToken.None);

        outcome.Invoice.Should().NotBeNull();
        outcome.Trace["AiErrorCode"].Should().Be("Timeout");
    }

    [Fact]
    public async Task Unrecognized_email_with_no_chat_response_yields_failure()
    {
        var harness = new Harness(chat: new FakeChat(returnEmpty: true));

        var outcome = await harness.Service.ExtractAsync(
            new EmailBodyReceiptRequest("uid-200", "Hello friend", "alice@example.com", "Just saying hi"),
            CancellationToken.None);

        outcome.Invoice.Should().BeNull();
        outcome.Failure.Should().NotBeNull();
        outcome.Failure!.ReasonCode.Should().Be("EMAIL_BODY_RECEIPT_UNRECOGNIZED");
        outcome.Trace["Parser"].Should().Be("none");
    }

    private static EmailBodyReceiptRequest NewBaiwangRequest() => new(
        SourceMessageUid: "uid-100",
        Subject: "百望电子发票通知",
        Sender: "notice@baiwang.com",
        BodyText: "金额 100.00 元，价税合计 113.00，发票号码 12345678，开票日期 2026-09-22。");

    private sealed class Harness
    {
        public Harness(IChatCompletionService chat) => Service = new EmailBodyReceiptService(chat);
        public EmailBodyReceiptService Service { get; }
    }

    private sealed class FakeChat : IChatCompletionService
    {
        private readonly ChatCompletionErrorCode? _error;
        private readonly bool _returnEmpty;
        public FakeChat(ChatCompletionErrorCode? error = null, bool returnEmpty = false)
        {
            _error = error;
            _returnEmpty = returnEmpty;
        }
        public Task<ChatCompletionResult> CompleteTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
        {
            if (_error is { } code) throw new ChatCompletionException(code, code.ToString());
            if (_returnEmpty) return Task.FromResult(new ChatCompletionResult("", "deepseek", 0, 0));
            return Task.FromResult(new ChatCompletionResult("{\"invoiceDate\":\"2026-09-22\"}", "deepseek", 10, 5));
        }
        public Task<ChatCompletionResult> CompleteVisionAsync(ChatCompletionRequest request, ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken)
        {
            if (_error is { } code) throw new ChatCompletionException(code, code.ToString());
            return Task.FromResult(new ChatCompletionResult("{}", "deepseek", 10, 5));
        }
    }
}
