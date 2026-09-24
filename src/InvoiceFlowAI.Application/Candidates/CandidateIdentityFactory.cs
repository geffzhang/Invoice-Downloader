using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Candidates;

public sealed class CandidateIdentityFactory : ICandidateIdentityFactory
{
    private readonly ICandidateIdentityKeyProvider _keyProvider;

    public CandidateIdentityFactory(ICandidateIdentityKeyProvider keyProvider)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
    }

    public Task<DocumentIdentity> CreateAttachmentAsync(
        string accountId,
        string mailbox,
        string uidValidity,
        MailboxAttachmentCandidate attachment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        cancellationToken.ThrowIfCancellationRequested();
        var contentHash = SHA256.HashData(attachment.Payload.Span);
        var value = BuildIdentity("a1", accountId, mailbox, uidValidity, attachment.MessageUid, "attachment", contentHash);
        return Task.FromResult(DocumentIdentity.Create(value));
    }

    public async Task<DocumentIdentity> CreateUrlAsync(
        MailboxUrlCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (!candidate.SourceUrl.IsAbsoluteUri
            || (candidate.SourceUrl.Scheme != Uri.UriSchemeHttp && candidate.SourceUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("URL candidate must use an absolute HTTP(S) URI.", nameof(candidate));
        }

        var key = await _keyProvider.GetCurrentKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key.Version) || key.Version.Length > 8 || key.KeyBytes.Length < 32)
        {
            throw new CryptographicException("Candidate identity key configuration is invalid.");
        }

        var canonicalUrl = Canonicalize(candidate.SourceUrl);
        var resourceFingerprint = HMACSHA256.HashData(key.KeyBytes.Span, Encoding.UTF8.GetBytes(canonicalUrl));
        var value = BuildIdentity(
            $"u{key.Version}",
            candidate.AccountId,
            candidate.Mailbox,
            candidate.UidValidity,
            candidate.MessageUid,
            "url",
            resourceFingerprint);
        return DocumentIdentity.Create(value);
    }

    public async Task<DocumentIdentity> CreateUrlGroupAsync(
        string providerFamily,
        IReadOnlyList<MailboxUrlCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerFamily);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count == 0)
        {
            throw new ArgumentException("URL group must contain at least one candidate.", nameof(candidates));
        }

        var first = candidates[0];
        if (candidates.Any(candidate =>
                !candidate.AccountId.Equals(first.AccountId, StringComparison.Ordinal)
                || !candidate.Mailbox.Equals(first.Mailbox, StringComparison.Ordinal)
                || !candidate.UidValidity.Equals(first.UidValidity, StringComparison.Ordinal)
                || !candidate.MessageUid.Equals(first.MessageUid, StringComparison.Ordinal)))
        {
            throw new ArgumentException("URL group candidates must share one mailbox message scope.", nameof(candidates));
        }

        var key = await _keyProvider.GetCurrentKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key.Version) || key.Version.Length > 8 || key.KeyBytes.Length < 32)
        {
            throw new CryptographicException("Candidate identity key configuration is invalid.");
        }

        var resource = providerFamily;
        if (string.IsNullOrEmpty(first.ProviderFamily))
        {
            if (candidates.Count != 1 || !first.SourceUrl.IsAbsoluteUri
                || (first.SourceUrl.Scheme != Uri.UriSchemeHttp && first.SourceUrl.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Unknown-provider URL candidates must be grouped individually.", nameof(candidates));
            }

            resource = Canonicalize(first.SourceUrl);
        }

        var fingerprint = HMACSHA256.HashData(key.KeyBytes.Span, Encoding.UTF8.GetBytes(resource));
        var value = BuildIdentity(
            $"g{key.Version}",
            first.AccountId,
            first.Mailbox,
            first.UidValidity,
            first.MessageUid,
            "url-group",
            fingerprint);
        return DocumentIdentity.Create(value);
    }

    public async Task<DocumentIdentity> CreateRecoveredArtifactAsync(
        DocumentIdentity sourceGroupIdentity,
        RecoveredArtifactKind kind,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sourceGroupIdentity.Value))
        {
            throw new ArgumentException("Source group identity must be non-empty.", nameof(sourceGroupIdentity));
        }
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var key = await _keyProvider.GetCurrentKeyAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key.Version) || key.Version.Length > 8 || key.KeyBytes.Length < 32)
        {
            throw new CryptographicException("Candidate identity key configuration is invalid.");
        }

        var contentDigest = SHA256.HashData(content.Span);
        var identityMaterial = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            sourceGroupIdentity.Value,
            kind.ToString(),
            Convert.ToHexString(contentDigest),
        });
        var fingerprint = HMACSHA256.HashData(key.KeyBytes.Span, identityMaterial);
        return DocumentIdentity.Create(BuildIdentity(
            $"r{key.Version}",
            "url-recovery",
            "selected-artifact",
            key.Version,
            sourceGroupIdentity.Value,
            kind.ToString(),
            fingerprint));
    }

    private static string Canonicalize(Uri sourceUrl)
    {
        var builder = new UriBuilder(sourceUrl)
        {
            Scheme = sourceUrl.Scheme.ToLowerInvariant(),
            Host = sourceUrl.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
        };
        return builder.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    private static string BuildIdentity(
        string versionPrefix,
        string accountId,
        string mailbox,
        string uidValidity,
        string messageUid,
        string resourceKind,
        ReadOnlySpan<byte> resourceFingerprint)
    {
        var components = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            accountId,
            mailbox,
            uidValidity,
            messageUid,
            resourceKind,
            Convert.ToHexString(resourceFingerprint),
        });
        var digest = SHA256.HashData(components);
        var encoded = Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{versionPrefix}_{encoded}";
    }
}