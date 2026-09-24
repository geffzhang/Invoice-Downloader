// Mailbox scanner abstraction (design §3 / Task 10). The scanner
// is responsible for fetching new messages from the IMAP server
// since the last UID cursor and yielding attachment candidates. The
// real implementation lives in Infrastructure and uses MailKit; the
// abstraction here is the testable seam used by the pipeline.

using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Mail;

public interface IMailboxScanner
{
    Task<MailboxScanResult> ScanAsync(MailboxScanRequest request, CancellationToken cancellationToken);
}

public interface IEmailTierClassifier
{
    int Classify(string? sender, string? subject, string? bodyText);
}

public interface IAttachmentCandidatePolicy
{
    AttachmentCandidateDecision Classify(AttachmentCandidateInput input);
}

public sealed record MailboxScanRequest(
    string AccountId,
    DateOnly? SinceDate,
    long? SinceUid,
    string? UidValidity);

public sealed record MailboxScanResult(
    IReadOnlyList<MailboxMessage> Messages,
    IReadOnlyList<MailboxAttachmentCandidate> Attachments,
    long HighestUid,
    string UidValidity,
    bool UidValidityChanged)
{
    public string AccountId { get; init; } = string.Empty;
    public IReadOnlyList<MailboxUrlCandidate> UrlCandidates { get; init; } = Array.Empty<MailboxUrlCandidate>();
}

public interface IAttachmentLimiter
{
    bool IsWithinLimit(string fileName, long contentLength);
}

public sealed class AttachmentLimiter : IAttachmentLimiter
{
    public const long DefaultMaxBytes = 25 * 1024 * 1024; // 25 MiB
    public const int DefaultMaxFiles = 50;

    private readonly long _maxBytes;
    private readonly int _maxFiles;

    public AttachmentLimiter(long? maxBytes = null, int? maxFiles = null)
    {
        _maxBytes = maxBytes ?? DefaultMaxBytes;
        _maxFiles = maxFiles ?? DefaultMaxFiles;
    }

    public bool IsWithinLimit(string fileName, long contentLength)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        if (contentLength <= 0) return false;
        return contentLength <= _maxBytes;
    }

    public bool IsWithinFileCount(int currentCount) => currentCount < _maxFiles;
}

public sealed record AttachmentImageInfo(int Width, int Height);

public sealed record AttachmentCandidateInput(
    string FileName,
    long ContentLength,
    int EmailTier,
    string ContentType,
    string ContentDisposition,
    string SourceKind,
    AttachmentImageInfo? ImageInfo = null,
    bool? HasQrCode = null);

public sealed record AttachmentCandidateDecision(
    string Bucket,
    string Action,
    string ReasonCode,
    IReadOnlyList<string> StrongNegativeSignals,
    IReadOnlyList<string> WeakNegativeSignals,
    string? ExtremeNegativeSignal,
    AttachmentImageInfo? ImageInfo);

public sealed record MailboxAttachmentCandidate(
    string Mailbox,
    string MessageUid,
    string FileName,
    string ContentType,
    string ContentDisposition,
    ReadOnlyMemory<byte> Payload,
    int EmailTier,
    AttachmentCandidateDecision Decision);
