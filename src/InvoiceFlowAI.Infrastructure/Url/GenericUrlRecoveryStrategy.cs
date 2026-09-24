using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public sealed class GenericUrlRecoveryStrategy : IUrlRecoveryStrategy
{
    private readonly DirectArtifactProbe _probe;

    public GenericUrlRecoveryStrategy(DirectArtifactProbe probe)
        => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public IReadOnlyCollection<string> ProviderFamilies => Array.Empty<string>();

    public async Task<UrlRecoveryResult> RecoverAsync(UrlCandidateGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        var artifacts = new List<CapturedUrlArtifact>();
        for (var ordinal = 0; ordinal < group.Candidates.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captures = await _probe.ProbeAsync(group.Candidates[ordinal].SourceUrl, ordinal, cancellationToken)
                .ConfigureAwait(false);
            artifacts.AddRange(captures);
        }

        var pdfIndexes = artifacts.Select((artifact, index) => (artifact, index))
            .Where(static item => item.artifact.Kind == RecoveredArtifactKind.Pdf)
            .Select(static item => item.index)
            .ToArray();
        int? selectedIndex = pdfIndexes.Length switch
        {
            1 => pdfIndexes[0],
            > 1 => throw new UrlRecoveryException("GENERIC_URL_AMBIGUOUS_PDF", "More than one invoice PDF was recovered.", false, false),
            _ => null,
        };
        if (selectedIndex is null)
        {
            var xmlIndexes = artifacts.Select((artifact, index) => (artifact, index))
                .Where(static item => item.artifact.Kind == RecoveredArtifactKind.Xml)
                .Select(static item => item.index)
                .ToArray();
            selectedIndex = xmlIndexes.Length switch
            {
                1 => xmlIndexes[0],
                > 1 => throw new UrlRecoveryException("GENERIC_URL_AMBIGUOUS_XML", "More than one invoice XML was recovered.", false, false),
                _ => null,
            };
        }
        if (selectedIndex is null)
        {
            throw new UrlRecoveryException("GENERIC_URL_NO_VALID_ARTIFACT", "No valid invoice document was recovered.", false, false);
        }
        return new UrlRecoveryResult(artifacts, selectedIndex);
    }
}