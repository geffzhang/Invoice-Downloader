using System.Globalization;
using System.Runtime.ExceptionServices;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Runs;
using MailKit.Net.Imap;
using MailKit.Security;
using SkiaSharp;

namespace InvoiceFlowAI.Infrastructure.Mail;

public sealed class MailKitMailboxScanner : IMailboxScanner
{
    private const string InboxMailboxName = "INBOX";
    private const string MissingAccountReason = "MAILBOX_ACCOUNT_NOT_FOUND";
    private const string MissingCredentialsReason = "CREDENTIALS_NOT_CONFIGURED";
    private const string LoginFailedReason = "IMAP_LOGIN_FAILED";
    private const string ProtocolFailedReason = "IMAP_PROTOCOL_FAILED";

    private readonly IMailboxAccountReader _accountReader;
    private readonly ISecretStore _secretStore;
    private readonly IMailboxChannelRegistry _channelRegistry;
    private readonly IEmailTierClassifier _tierClassifier;
    private readonly IAttachmentCandidatePolicy _attachmentPolicy;
    private readonly IMailboxSessionFactory _sessionFactory;
    private readonly Func<ReadOnlyMemory<byte>, AttachmentImageInfo?> _imageInfoProvider;

    public MailKitMailboxScanner(
        IMailboxAccountReader accountReader,
        ISecretStore secretStore,
        IMailboxChannelRegistry channelRegistry,
        IEmailTierClassifier tierClassifier,
        IAttachmentCandidatePolicy attachmentPolicy,
        IMailboxSessionFactory sessionFactory,
        Func<ReadOnlyMemory<byte>, AttachmentImageInfo?>? imageInfoProvider = null)
    {
        _accountReader = accountReader ?? throw new ArgumentNullException(nameof(accountReader));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _channelRegistry = channelRegistry ?? throw new ArgumentNullException(nameof(channelRegistry));
        _tierClassifier = tierClassifier ?? throw new ArgumentNullException(nameof(tierClassifier));
        _attachmentPolicy = attachmentPolicy ?? throw new ArgumentNullException(nameof(attachmentPolicy));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _imageInfoProvider = imageInfoProvider ?? DecodeImageInfo;
    }

    public async Task<MailboxScanResult> ScanAsync(MailboxScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var account = await ReadAccountAsync(request.AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            throw new MailboxScanException(MissingAccountReason, "Mailbox account is not available.");
        }

        if (string.IsNullOrWhiteSpace(account.CredentialName))
        {
            throw new MailboxScanException(MissingCredentialsReason, "Mailbox credentials are not configured.");
        }

        var secret = await ReadSecretAsync(account.CredentialName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new MailboxScanException(MissingCredentialsReason, "Mailbox credentials are not configured.");
        }

        var session = _sessionFactory.Create();
        MailboxScanResult? result = null;
        ExceptionDispatchInfo? pendingFailure = null;

        try
        {
            var capabilities = _channelRegistry.Resolve(account.EmailAddress);
            await session.ConnectAsync(account, cancellationToken).ConfigureAwait(false);
            await session.AuthenticateAsync(account.EmailAddress, secret, cancellationToken).ConfigureAwait(false);

            if (capabilities.RequiresIdCommand)
            {
                await session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
            }

            var mailboxName = string.IsNullOrWhiteSpace(account.DefaultMailbox) ? InboxMailboxName : account.DefaultMailbox!;
            var sessionInfo = await session.OpenReadOnlyAsync(mailboxName, cancellationToken).ConfigureAwait(false);
            var requestedUidValidity = ParseUidValidity(request.UidValidity);
            var normalizedSinceUid = request.SinceUid is >= 0 ? request.SinceUid : null;
            var canReuseUidCursor = normalizedSinceUid.HasValue
                && requestedUidValidity.HasValue
                && requestedUidValidity.Value == sessionInfo.UidValidity;
            var uidValidityChanged = normalizedSinceUid.HasValue && !canReuseUidCursor;
            var criteria = new MailboxSearchCriteria(
                canReuseUidCursor ? normalizedSinceUid : null,
                request.SinceDate,
                request.BeforeDateExclusive);

            var fetchedMessages = await session.SearchAsync(criteria, cancellationToken).ConfigureAwait(false);
            var messages = new List<MailboxMessage>(fetchedMessages.Count);
            var attachments = new List<MailboxAttachmentCandidate>();
            var urlCandidates = new List<MailboxUrlCandidate>();
            long highestUid = canReuseUidCursor ? normalizedSinceUid!.Value : 0;
            long urlSequence = 0;

            foreach (var fetchedMessage in fetchedMessages)
            {
                highestUid = Math.Max(highestUid, fetchedMessage.Uid);

                var attachmentNames = fetchedMessage.Attachments
                    .Where(static attachment => !string.IsNullOrWhiteSpace(attachment.FileName))
                    .Select(static attachment => attachment.FileName)
                    .ToArray();

                messages.Add(new MailboxMessage(
                    mailboxName,
                    fetchedMessage.Uid.ToString(CultureInfo.InvariantCulture),
                    sessionInfo.UidValidity,
                    fetchedMessage.SentAtUtc,
                    fetchedMessage.Subject,
                    fetchedMessage.FromAddress,
                    attachmentNames,
                    fetchedMessage.Attachments.Any(static attachment =>
                        attachment.ContentDisposition.Contains("inline", StringComparison.OrdinalIgnoreCase))));

                var emailTier = _tierClassifier.Classify(fetchedMessage.FromAddress, fetchedMessage.Subject, fetchedMessage.BodyText);
                foreach (var sourceUrl in MailboxUrlCandidateDiscovery.Extract(fetchedMessage.BodyText, fetchedMessage.HtmlBody))
                {
                    var providerFamily = MailboxUrlCandidateDiscovery.DetectProviderFamily(
                        sourceUrl,
                        fetchedMessage.FromAddress,
                        fetchedMessage.Subject);
                    var candidateSequence = urlSequence++;
                    urlCandidates.Add(new MailboxUrlCandidate(
                        request.AccountId,
                        mailboxName,
                        sessionInfo.UidValidity.ToString(CultureInfo.InvariantCulture),
                        fetchedMessage.Uid.ToString(CultureInfo.InvariantCulture),
                        sourceUrl,
                        providerFamily,
                        string.IsNullOrEmpty(providerFamily)
                            ? string.Empty
                            : $"{fetchedMessage.Uid.ToString(CultureInfo.InvariantCulture)}:{providerFamily}",
                        MailboxUrlCandidateDiscovery.ExtractExpectedFields(
                            providerFamily,
                            sourceUrl,
                            fetchedMessage.Subject,
                            fetchedMessage.BodyText),
                        candidateSequence)
                    {
                        ExpectedFieldEvidence = MailboxUrlCandidateDiscovery.ExtractExpectedFieldEvidence(
                            providerFamily,
                            sourceUrl,
                            fetchedMessage.Subject,
                            fetchedMessage.BodyText,
                            candidateSequence),
                    });
                }

                foreach (var attachment in fetchedMessage.Attachments)
                {
                    if (string.IsNullOrWhiteSpace(attachment.FileName))
                    {
                        continue;
                    }

                    var payloadCopy = attachment.Payload.AsMemory().ToArray();
                    var imageInfo = _imageInfoProvider(payloadCopy);
                    var decision = _attachmentPolicy.Classify(new AttachmentCandidateInput(
                        attachment.FileName,
                        payloadCopy.LongLength,
                        emailTier,
                        attachment.ContentType,
                        attachment.ContentDisposition,
                        SourceKind: "mime_attachment",
                        ImageInfo: imageInfo,
                        HasQrCode: null));

                    attachments.Add(new MailboxAttachmentCandidate(
                        mailboxName,
                        fetchedMessage.Uid.ToString(CultureInfo.InvariantCulture),
                        attachment.FileName,
                        attachment.ContentType,
                        attachment.ContentDisposition,
                        payloadCopy,
                        emailTier,
                        decision));
                }
            }

            result = new MailboxScanResult(
                messages,
                attachments,
                highestUid,
                sessionInfo.UidValidity.ToString(CultureInfo.InvariantCulture),
                uidValidityChanged)
            {
                AccountId = request.AccountId,
                UrlCandidates = urlCandidates,
            };
        }
        catch (OperationCanceledException ex)
        {
            pendingFailure = ExceptionDispatchInfo.Capture(ex);
        }
        catch (MailboxScanException ex)
        {
            pendingFailure = ExceptionDispatchInfo.Capture(ex);
        }
        catch (AuthenticationException)
        {
            pendingFailure = ExceptionDispatchInfo.Capture(new MailboxScanException(LoginFailedReason, "Mailbox login failed."));
        }
        catch (ImapCommandException)
        {
            pendingFailure = ExceptionDispatchInfo.Capture(new MailboxScanException(ProtocolFailedReason, "Mailbox scan failed."));
        }
        catch (ImapProtocolException)
        {
            pendingFailure = ExceptionDispatchInfo.Capture(new MailboxScanException(ProtocolFailedReason, "Mailbox scan failed."));
        }
        catch (Exception)
        {
            pendingFailure = ExceptionDispatchInfo.Capture(new MailboxScanException(ProtocolFailedReason, "Mailbox scan failed."));
        }

        Exception? disconnectFailure = null;
        try
        {
            await session.DisconnectAsync(pendingFailure is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pendingFailure is null)
        {
            throw;
        }
        catch (Exception ex)
        {
            disconnectFailure = ex;
        }

        if (pendingFailure is not null)
        {
            pendingFailure.Throw();
        }

        if (disconnectFailure is not null)
        {
            throw new MailboxScanException(ProtocolFailedReason, "Mailbox scan failed.");
        }

        return result!;
    }

    private static long? ParseUidValidity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private async Task<MailboxConnectionSettings?> ReadAccountAsync(string accountId, CancellationToken cancellationToken)
    {
        try
        {
            return await _accountReader.FindAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MailboxScanException(ProtocolFailedReason, "Mailbox scan failed.");
        }
    }

    private async Task<string?> ReadSecretAsync(string credentialName, CancellationToken cancellationToken)
    {
        try
        {
            return await _secretStore.GetAsync(credentialName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MailboxScanException(MissingCredentialsReason, "Mailbox credentials are not configured.");
        }
    }

    private static AttachmentImageInfo? DecodeImageInfo(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        using var codec = SKCodec.Create(new SKMemoryStream(payload.ToArray()));
        if (codec is null)
        {
            return null;
        }

        return new AttachmentImageInfo(codec.Info.Width, codec.Info.Height);
    }
}