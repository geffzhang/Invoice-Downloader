using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public static class DirectInvoiceArtifactSelector
{
    public static UrlRecoveryResult Select(
        IReadOnlyDictionary<string, string> expectedFields,
        IReadOnlyList<CapturedUrlArtifact> captures,
        IReadOnlyList<Uri>? sourceUrls = null)
    {
        ArgumentNullException.ThrowIfNull(expectedFields);
        ArgumentNullException.ThrowIfNull(captures);

        var artifacts = captures.ToArray();
        var xmlIndexes = artifacts.Select((artifact, index) => (artifact, index))
            .Where(static item => item.artifact.Kind == RecoveredArtifactKind.Xml)
            .Select(static item => item.index)
            .ToArray();
        var pdfIndexes = artifacts.Select((artifact, index) => (artifact, index))
            .Where(static item => item.artifact.Kind == RecoveredArtifactKind.Pdf)
            .Select(static item => item.index)
            .ToArray();

        var bestXmlFields = new Dictionary<string, string>(StringComparer.Ordinal);
        int? matchedXmlIndex = null;
        string? matchedXmlReason = null;
        var hardMismatch = false;
        foreach (var index in xmlIndexes)
        {
            var fields = artifacts[index].InvoiceFields;
            if (bestXmlFields.Count == 0) bestXmlFields = new Dictionary<string, string>(fields, StringComparer.Ordinal);
            var match = HasResolvedUrlInvoiceMatch(artifacts[index])
                ? (Matched: true, HardMismatch: false, Reason: "invoice_number_from_url")
                : Match(expectedFields, fields, artifacts[index].SanitizedResolvedOrigin,
                    SourceUrl(artifacts[index], sourceUrls));
            if (match.Matched)
            {
                bestXmlFields = new Dictionary<string, string>(fields, StringComparer.Ordinal);
                matchedXmlIndex = index;
                matchedXmlReason = match.Reason;
                break;
            }
            hardMismatch |= match.HardMismatch;
        }

        int? selectedIndex = null;
        string? selectionReason = null;
        foreach (var index in pdfIndexes)
        {
            var mergedFields = new Dictionary<string, string>(bestXmlFields, StringComparer.Ordinal);
            foreach (var pair in artifacts[index].InvoiceFields) mergedFields[pair.Key] = pair.Value;
            var match = HasResolvedUrlInvoiceMatch(artifacts[index])
                ? (Matched: true, HardMismatch: false, Reason: "invoice_number_from_url")
                : Match(expectedFields, mergedFields, artifacts[index].SanitizedResolvedOrigin,
                    SourceUrl(artifacts[index], sourceUrls));
            artifacts[index] = artifacts[index] with
            {
                InvoiceFields = mergedFields,
                ExpectedMatch = match.Matched,
                MatchReasonCode = match.Reason,
            };
            if (match.Matched)
            {
                selectedIndex = index;
                selectionReason = match.Reason;
                break;
            }
            hardMismatch |= match.HardMismatch;
        }

        if (selectedIndex is null && pdfIndexes.Length == 1 && !hardMismatch)
        {
            selectedIndex = pdfIndexes[0];
            selectionReason = "single_pdf_without_conflict";
            artifacts[selectedIndex.Value] = artifacts[selectedIndex.Value] with
            {
                InvoiceFields = Merge(bestXmlFields, artifacts[selectedIndex.Value].InvoiceFields),
                ExpectedMatch = true,
                MatchReasonCode = selectionReason,
            };
        }

        if (selectedIndex is null && pdfIndexes.Length == 0 && matchedXmlIndex is { } explicitXmlMatch)
        {
            selectedIndex = explicitXmlMatch;
            selectionReason = matchedXmlReason;
        }
        else if (selectedIndex is null && pdfIndexes.Length == 0 && xmlIndexes.Length == 1 && !hardMismatch)
        {
            selectedIndex = xmlIndexes[0];
            selectionReason = "single_xml_without_conflict";
        }

        if (selectedIndex is null)
        {
            var reason = hardMismatch
                ? "DIRECT_INVOICE_PDF_ENTITY_MISMATCH"
                : pdfIndexes.Length > 0
                    ? "DIRECT_INVOICE_MULTIPLE_PDF_CANDIDATES"
                    : xmlIndexes.Length > 0
                        ? "DIRECT_INVOICE_XML_ONLY_NO_MATCH"
                        : "DIRECT_INVOICE_NO_VALID_ARTIFACT";
            throw new UrlRecoveryException(reason, "Invoice provider artifacts could not be matched to one invoice document.", false, false);
        }

        if (artifacts[selectedIndex.Value].ExpectedMatch is null)
        {
            artifacts[selectedIndex.Value] = artifacts[selectedIndex.Value] with
            {
                ExpectedMatch = true,
                MatchReasonCode = selectionReason ?? "explicit_match",
            };
        }

        return new UrlRecoveryResult(artifacts, selectedIndex);
    }

    private static (bool Matched, bool HardMismatch, string Reason) Match(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual,
        string? resolvedOrigin,
        Uri? sourceUrl)
    {
        var expectedNumber = Get(expected, "invoice_number");
        var actualNumber = Get(actual, "invoice_number");
        if (expectedNumber.Length > 0)
        {
            if (actualNumber.Length > 0)
            {
                return actualNumber == expectedNumber
                    ? (true, false, "invoice_number")
                    : (false, true, "invoice_number_mismatch");
            }

            if ((!string.IsNullOrEmpty(resolvedOrigin) && resolvedOrigin.Contains(expectedNumber, StringComparison.Ordinal))
                || (sourceUrl is not null && sourceUrl.AbsoluteUri.Contains(expectedNumber, StringComparison.Ordinal)))
            {
                return (true, false, "invoice_number_from_url");
            }
        }

        var expectedSeller = Compact(Get(expected, "seller"));
        var actualSeller = Compact(Get(actual, "seller"));
        if (expectedSeller.Length > 0 && actualSeller.Length > 0
            && !expectedSeller.Contains(actualSeller, StringComparison.Ordinal)
            && !actualSeller.Contains(expectedSeller, StringComparison.Ordinal))
        {
            return (false, true, "seller_mismatch");
        }

        return (false, false, "no_explicit_match");
    }

    private static Dictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var pair in second) merged[pair.Key] = pair.Value;
        return merged;
    }

    private static string Get(IReadOnlyDictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

    private static Uri? SourceUrl(CapturedUrlArtifact artifact, IReadOnlyList<Uri>? sourceUrls)
        => sourceUrls is not null && artifact.SourceUrlOrdinal < sourceUrls.Count
            ? sourceUrls[artifact.SourceUrlOrdinal]
            : null;

    private static bool HasResolvedUrlInvoiceMatch(CapturedUrlArtifact artifact)
        => artifact.ExpectedMatch == true && artifact.MatchReasonCode == "invoice_number_from_url";

    private static string Compact(string value)
        => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));
}