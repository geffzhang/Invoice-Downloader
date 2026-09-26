using InvoiceFlowAI.Application.Mail;

namespace InvoiceFlowAI.Application.Url;

public interface IUrlRecoveryClient
{
    Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken);

    Task<UrlRecoveryResult> RecoverAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
        => RecoverAsync(candidate.SourceUrl, cancellationToken);

    async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Candidates.Count == 0)
        {
            throw new ArgumentException("URL recovery group must contain candidates.", nameof(group));
        }

        var artifacts = new List<CapturedUrlArtifact>();
        int? selectedIndex = null;
        UrlRecoveryException? lastFailure = null;
        for (var sourceOrdinal = 0; sourceOrdinal < group.Candidates.Count; sourceOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await RecoverAsync(group.Candidates[sourceOrdinal], cancellationToken).ConfigureAwait(false);
                foreach (var artifact in result.Artifacts)
                {
                    var copied = artifact with { SourceUrlOrdinal = sourceOrdinal };
                    if (selectedIndex is null && result.SelectedArtifactIndex == artifacts.Count(a => a.SourceUrlOrdinal == sourceOrdinal))
                    {
                        selectedIndex = artifacts.Count;
                    }
                    artifacts.Add(copied);
                }
            }
            catch (UrlRecoveryException exception)
            {
                lastFailure = exception;
            }
        }

        if (selectedIndex is null && artifacts.Count == 0 && lastFailure is not null)
        {
            throw lastFailure;
        }

        if (selectedIndex is null && artifacts.Count > 0)
        {
            selectedIndex = 0;
        }

        return new UrlRecoveryResult(artifacts, selectedIndex);
    }
}

public sealed record UrlRecoveryResult
{
    public UrlRecoveryResult(IReadOnlyList<CapturedUrlArtifact> artifacts, int? selectedArtifactIndex)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (selectedArtifactIndex is < 0 || selectedArtifactIndex >= artifacts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedArtifactIndex));
        }

        Artifacts = artifacts;
        SelectedArtifactIndex = selectedArtifactIndex;
    }

    public UrlRecoveryResult(ReadOnlyMemory<byte> content, string contentType)
        : this([CreateLegacyArtifact(content, contentType)], 0)
    {
    }

    public IReadOnlyList<CapturedUrlArtifact> Artifacts { get; }
    public int? SelectedArtifactIndex { get; }
    public CapturedUrlArtifact? SelectedArtifact
        => SelectedArtifactIndex is { } index ? Artifacts[index] : null;
    public ReadOnlyMemory<byte> Content => SelectedArtifact?.Content ?? ReadOnlyMemory<byte>.Empty;
    public string ContentType => SelectedArtifact?.ContentType ?? string.Empty;

    private static CapturedUrlArtifact CreateLegacyArtifact(ReadOnlyMemory<byte> content, string contentType)
    {
        var normalizedType = contentType ?? string.Empty;
        var kind = normalizedType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            ? RecoveredArtifactKind.Xml
            : normalizedType.Contains("ofd", StringComparison.OrdinalIgnoreCase)
                ? RecoveredArtifactKind.Ofd
                : RecoveredArtifactKind.Pdf;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content.Span)).ToLowerInvariant();
        return new CapturedUrlArtifact(kind, normalizedType, content, 0, digest, null,
            new Dictionary<string, string>(StringComparer.Ordinal), null, "LEGACY_SINGLE_RESULT");
    }
}

public sealed class UrlRecoveryException : Exception
{
    public UrlRecoveryException(string reasonCode, string safeMessage, bool retryable, bool isTimeout)
        : base(safeMessage)
    {
        ReasonCode = reasonCode;
        SafeMessage = safeMessage;
        Retryable = retryable;
        IsTimeout = isTimeout;
    }

    public string ReasonCode { get; }
    public string SafeMessage { get; }
    public bool Retryable { get; }
    public bool IsTimeout { get; }
}