using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;
using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class UrlRecoveryStrategyRegistry : IUrlRecoveryClient
{
    private static readonly HashSet<string> KnownProviderFamilies = new(StringComparer.Ordinal)
    {
        "chinatax_direct_invoice",
        "bwjf_signed_invoice",
        "fpyun_direct_invoice",
        "nuonuo_scan_invoice",
        "pdd_direct_invoice",
        "jdcloud_direct_invoice",
        "kpbyd_direct_invoice",
        "baiwang",
    };

    private readonly IReadOnlyDictionary<string, IUrlRecoveryStrategy> _strategies;
    private readonly IUrlRecoveryStrategy _fallbackStrategy;

    public UrlRecoveryStrategyRegistry(
        IEnumerable<IUrlRecoveryStrategy> strategies,
        IUrlRecoveryStrategy fallbackStrategy)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        _fallbackStrategy = fallbackStrategy ?? throw new ArgumentNullException(nameof(fallbackStrategy));
        var registered = new Dictionary<string, IUrlRecoveryStrategy>(StringComparer.Ordinal);
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            foreach (var family in strategy.ProviderFamilies)
            {
                if (string.IsNullOrWhiteSpace(family))
                {
                    throw new ArgumentException("Provider strategies must declare non-empty provider families.", nameof(strategies));
                }
                if (!registered.TryAdd(family, strategy))
                {
                    throw new ArgumentException($"Multiple URL recovery strategies handle provider family '{family}'.", nameof(strategies));
                }
            }
        }

        _strategies = registered;
    }

    public Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        cancellationToken.ThrowIfCancellationRequested();
        if (_strategies.TryGetValue(group.ProviderFamily, out var strategy))
        {
            return strategy.RecoverAsync(group, cancellationToken);
        }
        if (KnownProviderFamilies.Contains(group.ProviderFamily))
        {
            throw new UrlRecoveryException(
                "URL_RECOVERY_PROVIDER_UNAVAILABLE",
                "Invoice provider recovery is not available.",
                false,
                false);
        }

        return _fallbackStrategy.RecoverAsync(group, cancellationToken);
    }

    public Task<UrlRecoveryResult> RecoverAsync(Uri sourceUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new MailboxUrlCandidate(
            "single-url", "INBOX", "single-url", "single-url", sourceUrl,
            string.Empty, string.Empty, new Dictionary<string, string>(StringComparer.Ordinal), 0);
        var group = new UrlCandidateGroup(
            string.Empty,
            [candidate],
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<ExpectedFieldEvidence>>(StringComparer.Ordinal),
            DocumentIdentity.Create("single-url-recovery"));
        return _fallbackStrategy.RecoverAsync(group, cancellationToken);
    }

    public Task<UrlRecoveryResult> RecoverAsync(MailboxUrlCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var identityValue = string.IsNullOrWhiteSpace(candidate.ProviderGroupId)
            ? "single-url-recovery"
            : candidate.ProviderGroupId;
        var group = new UrlCandidateGroup(
            candidate.ProviderFamily,
            [candidate],
            candidate.ExpectedFields,
            candidate.ExpectedFieldEvidence,
            DocumentIdentity.Create(identityValue));
        return RecoverAsync(group, cancellationToken);
    }
}