namespace InvoiceFlowAI.Contracts.EmailBody;

/// <summary>
/// Canonical receipt projection parsed from a mail body. Shape mirrors the
/// <c>email-body/baiwang.receipt.json</c> fixture: a flat envelope with
/// <c>marker</c> + <c>providerId</c> + source UID, the raw request envelope
/// passed to the parser, the parsed result envelope, and the evidence
/// fingerprint that the rule pipeline uses for traceability.
/// </summary>
public sealed record EmailBodyReceipt(
    string Marker,
    string ProviderId,
    string SourceMessageUid,
    EmailBodyReceiptRequest Request,
    EmailBodyReceiptResult Result,
    string EvidenceFingerprint);