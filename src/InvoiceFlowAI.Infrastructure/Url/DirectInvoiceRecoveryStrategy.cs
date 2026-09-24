using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class DirectInvoiceRecoveryStrategy : IUrlRecoveryStrategy
{
    private static readonly string[] Families =
    [
        "chinatax_direct_invoice",
        "bwjf_signed_invoice",
        "fpyun_direct_invoice",
        "pdd_direct_invoice",
        "jdcloud_direct_invoice",
        "kpbyd_direct_invoice",
    ];

    private readonly DirectArtifactProbe _probe;

    public DirectInvoiceRecoveryStrategy(DirectArtifactProbe probe)
        => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public IReadOnlyCollection<string> ProviderFamilies => Families;

    public async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Candidates.Count == 0)
        {
            throw new ArgumentException("URL recovery group must contain candidates.", nameof(group));
        }

        var captures = new List<CapturedUrlArtifact>();
        var sourceUrls = group.Candidates.Select(static candidate => candidate.SourceUrl).ToArray();
        for (var ordinal = 0; ordinal < sourceUrls.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            captures.AddRange(await _probe.ProbeAsync(sourceUrls[ordinal], ordinal, cancellationToken,
                allowFpyunRedirect: group.ProviderFamily == "fpyun_direct_invoice",
                expectedInvoiceNumber: group.ExpectedFields.GetValueOrDefault("invoice_number")).ConfigureAwait(false));
        }

        return DirectInvoiceArtifactSelector.Select(group.ExpectedFields, captures, sourceUrls);
    }
}