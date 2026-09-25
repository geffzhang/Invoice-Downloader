using System.Net;
using System.Text.RegularExpressions;
using InvoiceFlowAI.Application.Mail;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace InvoiceFlowAI.Infrastructure.Mail;

public sealed class MailKitMailboxSession : IMailboxSession
{
    private static readonly TimeZoneInfo ShanghaiTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly Regex HtmlTagPattern = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ImapClient _client;
    private IMailFolder? _folder;

    public MailKitMailboxSession()
        : this(new ImapClient())
    {
    }

    internal MailKitMailboxSession(ImapClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task ConnectAsync(MailboxConnectionSettings settings, CancellationToken cancellationToken)
        => _client.ConnectAsync(
            settings.ImapHost,
            settings.ImapPort,
            settings.UseTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None,
            cancellationToken);

    public Task AuthenticateAsync(string userName, string secret, CancellationToken cancellationToken)
        => _client.AuthenticateAsync(userName, secret, cancellationToken);

    public Task IdentifyAsync(CancellationToken cancellationToken)
        => _client.IdentifyAsync(CreateClientImplementation(), cancellationToken);

    public async Task<MailboxSessionInfo> OpenReadOnlyAsync(string mailboxName, CancellationToken cancellationToken)
    {
        var folder = mailboxName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? _client.Inbox
            : await _client.GetFolderAsync(mailboxName, cancellationToken).ConfigureAwait(false);

        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        _folder = folder;
        return new MailboxSessionInfo(folder.UidValidity);
    }

    public async Task<IReadOnlyList<MailboxFetchedMessage>> SearchAsync(MailboxSearchCriteria criteria, CancellationToken cancellationToken)
    {
        var folder = _folder ?? throw new InvalidOperationException("Mailbox folder has not been opened.");
        var query = BuildSearchQuery(criteria);
        var uids = FilterUidsAfterCursor(
            await folder.SearchAsync(query, cancellationToken).ConfigureAwait(false),
            criteria.SinceUid);
        var summaries = await folder.FetchAsync(
            uids.ToList(),
            new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope),
            cancellationToken).ConfigureAwait(false);
        var dateSummaries = summaries.Select(summary => new MailboxMessageDateSummary(
            summary.UniqueId,
            summary.Envelope?.Date,
            summary.InternalDate)).ToArray();
        var summaryByUid = dateSummaries.ToDictionary(summary => summary.Uid);
        var selectedUids = FilterUidsByDateWindow(uids, dateSummaries, criteria);
        var messages = new List<MailboxFetchedMessage>(selectedUids.Count);

        foreach (var uid in selectedUids)
        {
            summaryByUid.TryGetValue(uid, out var summary);
            var message = await folder.GetMessageAsync(uid, cancellationToken, null).ConfigureAwait(false);
            messages.Add(ProjectMessage(uid.Id, message, summary?.InternalDateUtc));
        }

        return messages;
    }

    internal static IReadOnlyList<UniqueId> FilterUidsAfterCursor(IEnumerable<UniqueId> uids, long? sinceUid)
    {
        ArgumentNullException.ThrowIfNull(uids);

        if (!sinceUid.HasValue || sinceUid.Value < 0)
        {
            return uids as IReadOnlyList<UniqueId> ?? uids.ToArray();
        }

        return uids.Where(uid => uid.Id > sinceUid.Value).ToArray();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => _client.IsConnected
            ? _client.DisconnectAsync(true, cancellationToken)
            : Task.CompletedTask;

    internal static ImapImplementation CreateClientImplementation()
    {
        return new ImapImplementation
        {
            Name = "InvoiceFlowAI",
            Version = typeof(MailKitMailboxSession).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
        };
    }

    internal static MailboxFetchedMessage ProjectMessage(long uid, MimeMessage message, DateTimeOffset? internalDateUtc)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new MailboxFetchedMessage(
            uid,
            ResolveSentAtUtc(message, internalDateUtc),
            message.Subject ?? string.Empty,
            ResolveFromAddress(message),
            ExtractBodyText(message),
            ExtractAttachments(message))
        {
            HtmlBody = message.HtmlBody ?? string.Empty,
        };
    }

    internal static SearchQuery BuildSearchQuery(MailboxSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        SearchQuery query = SearchQuery.All;
        if (criteria.SinceUid.HasValue)
        {
            if (criteria.SinceUid.Value >= uint.MaxValue)
            {
                query = query.And(SearchQuery.Not(SearchQuery.All));
            }
            else if (criteria.SinceUid.Value >= 0)
            {
                var lowerBound = new UniqueId((uint)criteria.SinceUid.Value + 1u);
                query = query.And(SearchQuery.Uids(new UniqueIdRange(lowerBound, UniqueId.MaxValue)));
            }
        }

        return query;
    }

    internal static bool IsInDateWindow(DateTimeOffset? effectiveDate, MailboxSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (!effectiveDate.HasValue)
        {
            return true;
        }

        var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(effectiveDate.Value, ShanghaiTimeZone).DateTime);
        return (!criteria.SinceDate.HasValue || localDay >= criteria.SinceDate.Value)
            && (!criteria.BeforeDateExclusive.HasValue || localDay < criteria.BeforeDateExclusive.Value);
    }

    internal static IReadOnlyList<UniqueId> FilterUidsByDateWindow(
        IReadOnlyList<UniqueId> uids,
        IReadOnlyList<MailboxMessageDateSummary> summaries,
        MailboxSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(uids);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(criteria);

        var byUid = summaries.ToDictionary(summary => summary.Uid);
        return uids.Where(uid =>
        {
            if (!byUid.TryGetValue(uid, out var summary))
            {
                return true;
            }

            return IsInDateWindow(summary.HeaderDateUtc ?? summary.InternalDateUtc, criteria);
        }).ToArray();
    }

    internal static string ExtractBodyText(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            return message.TextBody.Trim();
        }

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            var noTags = HtmlTagPattern.Replace(message.HtmlBody, " ");
            return NormalizeWhitespace(WebUtility.HtmlDecode(noTags));
        }

        return string.Empty;
    }

    private static IReadOnlyList<MailboxFetchedAttachment> ExtractAttachments(MimeMessage message)
    {
        var attachments = new List<MailboxFetchedAttachment>();
        var synthesizedAttachmentIndex = 0;

        foreach (var part in message.BodyParts)
        {
            if (!ShouldProjectAttachment(part))
            {
                continue;
            }

            var fileName = ResolveAttachmentFileName(part, ref synthesizedAttachmentIndex);
            attachments.Add(new MailboxFetchedAttachment(
                fileName,
                part.ContentType?.MimeType ?? "application/octet-stream",
                ResolveDisposition(part),
                ReadPayload(part)));
        }

        return attachments;
    }

    private static bool ShouldProjectAttachment(MimeEntity part)
    {
        return part switch
        {
            MimePart mimePart => !string.IsNullOrWhiteSpace(mimePart.FileName) || mimePart.IsAttachment,
            MessagePart messagePart => !string.IsNullOrWhiteSpace(ResolveEntityFileName(messagePart)) || messagePart.IsAttachment,
            _ => false,
        };
    }

    private static string ResolveAttachmentFileName(MimeEntity part, ref int synthesizedAttachmentIndex)
    {
        var explicitFileName = ResolveEntityFileName(part);
        if (!string.IsNullOrWhiteSpace(explicitFileName))
        {
            return explicitFileName;
        }

        synthesizedAttachmentIndex++;
        return $"attachment-{synthesizedAttachmentIndex:000}{ResolveSyntheticExtension(part)}";
    }

    private static string? ResolveEntityFileName(MimeEntity part)
    {
        return part switch
        {
            MimePart mimePart when !string.IsNullOrWhiteSpace(mimePart.FileName) => mimePart.FileName,
            _ when !string.IsNullOrWhiteSpace(part.ContentDisposition?.FileName) => part.ContentDisposition!.FileName,
            _ when !string.IsNullOrWhiteSpace(part.ContentType?.Name) => part.ContentType.Name,
            _ => null,
        };
    }

    private static string ResolveSyntheticExtension(MimeEntity part)
    {
        if (part is MessagePart)
        {
            return ".eml";
        }

        return part.ContentType?.MimeType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            "application/pdf" => ".pdf",
            _ => ".bin",
        };
    }

    private static byte[] ReadPayload(MimeEntity part)
    {
        using var stream = new MemoryStream();

        switch (part)
        {
            case MimePart mimePart when mimePart.Content is not null:
                mimePart.Content.DecodeTo(stream);
                break;
            case MessagePart messagePart when messagePart.Message is not null:
                messagePart.Message.WriteTo(stream);
                break;
        }

        return stream.ToArray();
    }

    private static string ResolveDisposition(MimeEntity part)
    {
        if (!string.IsNullOrWhiteSpace(part.ContentDisposition?.Disposition))
        {
            return part.ContentDisposition!.Disposition;
        }

        return part.IsAttachment ? ContentDisposition.Attachment : ContentDisposition.Inline;
    }

    private static DateTimeOffset? ResolveSentAtUtc(MimeMessage message, DateTimeOffset? internalDateUtc)
    {
        if (message.Date != default)
        {
            return message.Date.ToUniversalTime();
        }

        return internalDateUtc?.ToUniversalTime();
    }

    private static string ResolveFromAddress(MimeMessage message)
    {
        var mailbox = message.From.Mailboxes.FirstOrDefault();
        return mailbox?.Address ?? message.From.ToString();
    }

    private static string NormalizeWhitespace(string text)
        => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

internal sealed record MailboxMessageDateSummary(
    UniqueId Uid,
    DateTimeOffset? HeaderDateUtc,
    DateTimeOffset? InternalDateUtc);