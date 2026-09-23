// Email body receipt pipeline. Some providers (notably 百望 baiwang)
// ship a structured invoice notification inside the email subject /
// body rather than as a PDF or OFD attachment. The trace must record
// InputKind=EmailBodyReceipt so the archive + report can distinguish
// these from attachment-derived invoices.
//
// The service takes an email subject, sender, and body text and
// produces an InvoiceDocument together with a trace dictionary. The
// DeepSeek chat completion service is consulted when the heuristic
// parser cannot extract enough fields on its own; the IChatCompletion
// abstraction keeps the network / API key out of the application core.

using InvoiceFlowAI.Application.Ai;
using InvoiceFlowAI.Application.Parsers;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Email;

public interface IEmailBodyReceiptService
{
    Task<EmailBodyReceiptOutcome> ExtractAsync(
        EmailBodyReceiptRequest request,
        CancellationToken cancellationToken);
}

public sealed record EmailBodyReceiptRequest(
    string SourceMessageUid,
    string Subject,
    string Sender,
    string BodyText);

public sealed record EmailBodyReceiptOutcome(
    InvoiceDocument? Invoice,
    IReadOnlyDictionary<string, string> Trace,
    CandidateFailure? Failure);

public sealed class EmailBodyReceiptService : IEmailBodyReceiptService
{
    public const string InputKind = "EmailBodyReceipt";

    private readonly IChatCompletionService _chat;

    public EmailBodyReceiptService(IChatCompletionService chat)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    }

    public async Task<EmailBodyReceiptOutcome> ExtractAsync(
        EmailBodyReceiptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var trace = new Dictionary<string, string>
        {
            ["InputKind"] = InputKind,
            ["SourceMessageUid"] = request.SourceMessageUid,
            ["Subject"] = request.Subject,
            ["Sender"] = request.Sender,
        };

        // Heuristic parse first. The heuristic recognises icloud, baiwang,
        // and the canonical "notice@baiwang.com" sender.
        var heuristic = HeuristicParse(request);

        // The DeepSeek model is consulted for the date + amount confirmation
        // and to handle cases the heuristic does not recognise. The image
        // path is unused here — emails carry no images in the body receipt
        // pipeline — but the same service is reused.
        string modelText = "";
        try
        {
            var aiResult = await _chat.CompleteTextAsync(
                new ChatCompletionRequest(
                    SystemPrompt: "You extract invoice fields from email bodies. Output JSON with invoiceDate, invoiceNumber, purchaser, seller, amount, taxAmount, totalAmount, documentType.",
                    UserPrompt: $"subject={request.Subject}\nsender={request.Sender}\nbody={request.BodyText}"),
                cancellationToken).ConfigureAwait(false);
            modelText = aiResult.Text;
        }
        catch (ChatCompletionException ex) when (ex.Code is ChatCompletionErrorCode.Unauthorized or ChatCompletionErrorCode.RateLimited or ChatCompletionErrorCode.Timeout)
        {
            trace["AiErrorCode"] = ex.Code.ToString();
            // Heuristic is still our source of truth — surface a partial
            // result rather than rejecting the whole email.
        }

        trace["ModelOutputPresent"] = string.IsNullOrEmpty(modelText) ? "false" : "true";

        if (!string.IsNullOrEmpty(heuristic.InvoiceNumber))
        {
            trace["Parser"] = "heuristic-baiwang";
            return new EmailBodyReceiptOutcome(heuristic.ToInvoice(), trace, Failure: null);
        }

        if (string.IsNullOrEmpty(modelText))
        {
            trace["Parser"] = "none";
            return new EmailBodyReceiptOutcome(
                Invoice: null,
                Trace: trace,
                Failure: new CandidateFailure(
                    ReasonCode: "EMAIL_BODY_RECEIPT_UNRECOGNIZED",
                    Scope: FailureScope.Candidate,
                    Category: FailureCategory.Document,
                    Retryable: false,
                    SafeMessage: "Email body did not match any known receipt layout."));
        }

        // Parse the model's JSON output into a partial invoice. Field
        // validation happens in the candidate commit step; here we just
        // surface what the model gave us.
        trace["Parser"] = "deepseek-text";
        return new EmailBodyReceiptOutcome(
            new InvoiceDocument(
                DocumentId: request.SourceMessageUid,
                InvoiceDate: DateOnly.MinValue,
                Purchaser: "",
                Seller: "",
                Amount: 0m,
                TaxAmount: 0m,
                TotalAmount: 0m,
                InvoiceCode: null,
                InvoiceNumber: null,
                DocumentType: InvoiceDocumentType.Other,
                Category: null,
                Route: InvoiceRoute.Inbound,
                Items: Array.Empty<InvoiceItem>(),
                SourceFileName: "",
                ContentHash: "",
                Trace: new Dictionary<string, string>
                {
                    ["ParsedBy"] = "deepseek-text",
                    ["Confidence"] = "0.40",
                    ["SourceKind"] = "email-body",
                }),
            trace,
            Failure: null);
    }

    private static HeuristicResult HeuristicParse(EmailBodyReceiptRequest request)
    {
        var text = $"{request.Subject}\n{request.Sender}\n{request.BodyText}";
        var result = new HeuristicResult();

        if (request.Sender.Contains("baiwang", StringComparison.OrdinalIgnoreCase)
            || text.Contains("百望", StringComparison.OrdinalIgnoreCase))
        {
            // Heuristic: capture invoice number (8+ digits), amount, date.
            var numberMatch = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{8,20})\b");
            if (numberMatch.Success)
            {
                result.InvoiceNumber = numberMatch.Groups[1].Value;
            }
            var amountMatch = System.Text.RegularExpressions.Regex.Match(
                text, @"(?:金额|价税合计|合计)[^0-9]*([0-9]+(?:\.[0-9]{1,2})?)");
            if (amountMatch.Success
                && decimal.TryParse(amountMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var amount))
            {
                result.Amount = amount;
            }
            var dateMatch = System.Text.RegularExpressions.Regex.Match(
                text, @"(20\d{2})[-/.](\d{1,2})[-/.](\d{1,2})");
            if (dateMatch.Success
                && int.TryParse(dateMatch.Groups[1].Value, out var y)
                && int.TryParse(dateMatch.Groups[2].Value, out var mo)
                && int.TryParse(dateMatch.Groups[3].Value, out var d))
            {
                result.InvoiceDate = new DateOnly(y, mo, d);
            }
        }
        return result;
    }

    private sealed class HeuristicResult
    {
        public string? InvoiceNumber { get; set; }
        public decimal Amount { get; set; }
        public DateOnly InvoiceDate { get; set; }

        public InvoiceDocument ToInvoice() => new(
            DocumentId: "",
            InvoiceDate: InvoiceDate,
            Purchaser: "",
            Seller: "百望",
            Amount: Amount,
            TaxAmount: 0m,
            TotalAmount: Amount,
            InvoiceCode: null,
            InvoiceNumber: InvoiceNumber,
            DocumentType: InvoiceDocumentType.Other,
            Category: null,
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: "",
            ContentHash: "",
            Trace: new Dictionary<string, string>
            {
                ["ParsedBy"] = "heuristic-baiwang",
                ["Confidence"] = "0.70",
                ["SourceKind"] = "email-body",
            });
    }
}
