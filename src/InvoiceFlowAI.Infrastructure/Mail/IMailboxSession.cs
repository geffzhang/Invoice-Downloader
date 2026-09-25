using InvoiceFlowAI.Application.Mail;

namespace InvoiceFlowAI.Infrastructure.Mail;

public interface IMailboxSessionFactory
{
    IMailboxSession Create();
}

public interface IMailboxSession
{
    Task ConnectAsync(MailboxConnectionSettings settings, CancellationToken cancellationToken);

    Task AuthenticateAsync(string userName, string secret, CancellationToken cancellationToken);

    Task IdentifyAsync(CancellationToken cancellationToken);

    Task<MailboxSessionInfo> OpenReadOnlyAsync(string mailboxName, CancellationToken cancellationToken);

    Task<MailboxSearchResult> SearchAsync(MailboxSearchCriteria criteria, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

public sealed class MailboxSessionFactory : IMailboxSessionFactory
{
    public IMailboxSession Create() => new MailKitMailboxSession();
}

public sealed record MailboxSessionInfo(long UidValidity);

public sealed record MailboxSearchCriteria(
    long? SinceUid,
    DateOnly? SinceDate,
    DateOnly? BeforeDateExclusive = null);

public sealed record MailboxFetchedAttachment(
    string FileName,
    string ContentType,
    string ContentDisposition,
    byte[] Payload);

public sealed record MailboxFetchedMessage(
    long Uid,
    DateTimeOffset? SentAtUtc,
    string Subject,
    string FromAddress,
    string BodyText,
    IReadOnlyList<MailboxFetchedAttachment> Attachments)
{
    public string HtmlBody { get; init; } = string.Empty;
}

public sealed record MailboxSearchResult(
    IReadOnlyList<MailboxFetchedMessage> Messages,
    IReadOnlyList<MailboxFetchFailure> FetchFailures);