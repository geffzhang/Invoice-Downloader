// Email body receipt pipeline. Some providers (notably 百望 baiwang)
// ship a structured invoice notification inside the email subject /
// body rather than as a PDF or OFD attachment. The trace must record
// InputKind=EmailBodyReceipt so the archive + report can distinguish
// these from attachment-derived invoices.
//
// The service parses recognized receipt layouts deterministically. Email
// content is transient and never copied into trace fields.

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
    CandidateFailure? Failure,
    bool ContinueWithAttachments = false);

public sealed class EmailBodyReceiptService : IEmailBodyReceiptService
{
    public const string InputKind = "EmailBodyReceipt";

    public Task<EmailBodyReceiptOutcome> ExtractAsync(
        EmailBodyReceiptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var trace = new Dictionary<string, string>
        {
            ["InputKind"] = InputKind,
            ["SourceMessageUid"] = request.SourceMessageUid,
        };
        var heuristic = HeuristicParse(request);

        if (heuristic.IsComplete)
        {
            trace["Parser"] = "deterministic-baiwang";
            trace["Status"] = "resolved";
            trace["ReceiptContract"] = "EMAIL_BODY_RECEIPT_CANONICAL";
            return Task.FromResult(new EmailBodyReceiptOutcome(heuristic.ToInvoice(request.SourceMessageUid), trace, Failure: null));
        }

        trace["Parser"] = "none";
        trace["Status"] = "receipt_miss_continue_attachments";
        return Task.FromResult(new EmailBodyReceiptOutcome(
            Invoice: null,
            Trace: trace,
            Failure: new CandidateFailure(
                ReasonCode: "EMAIL_BODY_RECEIPT_UNRECOGNIZED",
                Scope: FailureScope.Candidate,
                Category: FailureCategory.Document,
                Retryable: false,
                SafeMessage: "Email body did not match any known receipt layout."),
            ContinueWithAttachments: true));
    }

    private static HeuristicResult HeuristicParse(EmailBodyReceiptRequest request)
    {
        var text = $"{request.Subject}\n{request.Sender}\n{request.BodyText}";
        var result = new HeuristicResult();

        if (request.Sender.Contains("baiwang", StringComparison.OrdinalIgnoreCase)
            || text.Contains("百望", StringComparison.OrdinalIgnoreCase))
        {
            // Heuristic: capture invoice number (8+ digits), amount, date.
            var numberMatch = System.Text.RegularExpressions.Regex.Match(text,
                @"(?:发票号码|Invoice\s*(?:No\.?|Number))\s*[:：#]?\s*(\d{8,20})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (numberMatch.Success)
            {
                result.InvoiceNumber = numberMatch.Groups[1].Value;
            }
            result.Amount = ExtractAmount(text) ?? 0m;
            var dateMatch = System.Text.RegularExpressions.Regex.Match(
                text, @"开票日期\s*[:：]?\s*(20\d{2})[-/.年](\d{1,2})[-/.月](\d{1,2})日?",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (dateMatch.Success
                && int.TryParse(dateMatch.Groups[1].Value, out var y)
                && int.TryParse(dateMatch.Groups[2].Value, out var mo)
                && int.TryParse(dateMatch.Groups[3].Value, out var d))
            {
                try { result.InvoiceDate = new DateOnly(y, mo, d); }
                catch (ArgumentOutOfRangeException) { }
            }

            result.Purchaser = ExtractLabeledValue(text, "购买方名称", "购买方", "Purchaser");
            result.Seller = ExtractLabeledValue(text, "销售方名称", "销售方", "Seller");
            var typeName = ExtractLabeledValue(text, "发票类型", "单据类型", "Document Type");
            if (Enum.TryParse<InvoiceDocumentType>(typeName, ignoreCase: true, out var documentType)
                && Enum.IsDefined(documentType))
            {
                result.DocumentType = documentType;
            }
        }
        return result;
    }

    private static string ExtractLabeledValue(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text,
                $@"{System.Text.RegularExpressions.Regex.Escape(label)}\s*[:：]?\s*(?<value>[^，,。;；\r\n]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static decimal? ExtractAmount(string text)
    {
        foreach (var label in new[] { "价税合计", "合计", "金额" })
        {
            var amountMatch = System.Text.RegularExpressions.Regex.Match(text,
                $@"{label}\s*[:：]?\s*[¥￥]?\s*([0-9,]+(?:\.[0-9]{{1,2}})?)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (amountMatch.Success
                && decimal.TryParse(amountMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var amount))
            {
                return amount;
            }
        }

        return null;
    }

    private sealed class HeuristicResult
    {
        public string? InvoiceNumber { get; set; }
        public decimal Amount { get; set; }
        public DateOnly InvoiceDate { get; set; }
        public string Purchaser { get; set; } = string.Empty;
        public string Seller { get; set; } = string.Empty;
        public InvoiceDocumentType DocumentType { get; set; } = InvoiceDocumentType.Other;
        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(InvoiceNumber)
            && InvoiceDate != default
            && !string.IsNullOrWhiteSpace(Purchaser)
            && !string.IsNullOrWhiteSpace(Seller)
            && Amount > 0m
            && DocumentType is not InvoiceDocumentType.Other and not InvoiceDocumentType.Unrecognized;

        public InvoiceDocument ToInvoice(string sourceMessageUid) => new(
            DocumentId: sourceMessageUid,
            InvoiceDate: InvoiceDate == default ? null : InvoiceDate,
            Purchaser: Purchaser,
            Seller: Seller,
            Amount: Amount,
            TaxAmount: 0m,
            TotalAmount: Amount,
            InvoiceCode: null,
            InvoiceNumber: InvoiceNumber,
            DocumentType: DocumentType,
            Category: null,
            Route: InvoiceRoute.Inbound,
            Items: Array.Empty<InvoiceItem>(),
            SourceFileName: "",
            ContentHash: "",
            Trace: new Dictionary<string, string>
            {
                ["ParsedBy"] = "deterministic-baiwang",
                ["Confidence"] = "0.70",
                ["ReceiptContract"] = "EMAIL_BODY_RECEIPT_CANONICAL",
                ["SourceKind"] = "email-body",
            })
        {
            Identity = DocumentIdentity.Create(sourceMessageUid),
            Confidence = 0.70m,
            ParserName = "deterministic-baiwang",
        };
    }
}
