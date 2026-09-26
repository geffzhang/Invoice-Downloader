using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url;

public static class BaiwangArtifactSelector
{
    public static UrlRecoveryResult Select(
        IReadOnlyDictionary<string, string> expectedFields,
        IReadOnlyList<CapturedUrlArtifact> captures)
    {
        ArgumentNullException.ThrowIfNull(expectedFields);
        ArgumentNullException.ThrowIfNull(captures);

        var artifacts = captures.ToArray();
        var matchedXmlIndexes = new List<int>();
        var candidatePdfIndexes = new List<int>();
        var matchedPdfIndexes = new List<int>();
        for (var index = 0; index < artifacts.Length; index++)
        {
            var artifact = artifacts[index];
            if (IsWrapper(artifact))
            {
                artifacts[index] = artifact with { ExpectedMatch = false, MatchReasonCode = "wrapper_detected" };
                continue;
            }

            var (matched, matchReason) = Match(expectedFields, artifact.InvoiceFields);
            artifacts[index] = artifact with { ExpectedMatch = matched, MatchReasonCode = matchReason };
            if (artifact.Kind is RecoveredArtifactKind.Xml or RecoveredArtifactKind.Ofd && matched)
            {
                matchedXmlIndexes.Add(index);
            }
            if (artifact.Kind == RecoveredArtifactKind.Pdf)
            {
                candidatePdfIndexes.Add(index);
                if (matched) matchedPdfIndexes.Add(index);
            }
        }

        int? selectedIndex = matchedPdfIndexes.Count > 0 ? matchedPdfIndexes[0] : null;
        string? selectedReason = selectedIndex is null ? null : "matched_pdf";
        if (selectedIndex is null && matchedXmlIndexes.Count > 0)
        {
            var preferredSource = artifacts[matchedXmlIndexes[0]].SourceUrlOrdinal;
            selectedIndex = candidatePdfIndexes.FirstOrDefault(index => artifacts[index].SourceUrlOrdinal == preferredSource, -1);
            if (selectedIndex < 0)
            {
                selectedIndex = candidatePdfIndexes.Count == 1 ? candidatePdfIndexes[0] : null;
                selectedReason = selectedIndex is null ? null : "xml_then_single_pdf";
            }
            else
            {
                selectedReason = "xml_then_pdf_same_source";
            }
        }

        if (selectedIndex is null && !expectedFields.Values.Any(static value => !string.IsNullOrWhiteSpace(value))
            && candidatePdfIndexes.Count > 0)
        {
            selectedIndex = candidatePdfIndexes[0];
            selectedReason = "pdf_without_expected_fields";
        }

        if (selectedIndex is null)
        {
            var reason = matchedXmlIndexes.Count > 0 || candidatePdfIndexes.Count > 0
                ? "BAIWANG_PDF_ENTITY_MISMATCH"
                : "BAIWANG_NO_VALID_PDF";
            throw new UrlRecoveryException(reason, "Baiwang artifacts could not be matched to one invoice PDF.", false, false);
        }

        var chosenIndex = selectedIndex.Value;
        var chosen = artifacts[chosenIndex] with
        {
            ExpectedMatch = true,
            MatchReasonCode = selectedReason ?? "matched_pdf",
        };
        if (selectedReason is "xml_then_pdf_same_source" or "xml_then_single_pdf")
        {
            var relatedXml = artifacts[matchedXmlIndexes[0]];
            var fields = new Dictionary<string, string>(relatedXml.InvoiceFields, StringComparer.Ordinal);
            foreach (var field in chosen.InvoiceFields) fields[field.Key] = field.Value;
            chosen = chosen with { InvoiceFields = fields };
        }
        artifacts[chosenIndex] = chosen;
        return new UrlRecoveryResult(artifacts, chosenIndex);
    }

    private static bool IsWrapper(CapturedUrlArtifact artifact)
        => artifact.MatchReasonCode.Equals("BAIWANG_WRAPPER_DETECTED", StringComparison.Ordinal);

    private static (bool Matched, string Reason) Match(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        if (!expected.Values.Any(static value => !string.IsNullOrWhiteSpace(value))) return (true, "no_expected_fields");

        var expectedNumber = Get(expected, "invoice_number");
        if (expectedNumber.Length > 0)
        {
            return (Get(actual, "invoice_number") is { Length: > 0 } actualNumber && expectedNumber == actualNumber,
                "invoice_number");
        }

        var expectedSeller = Compact(Get(expected, "seller"));
        var actualSeller = Compact(Get(actual, "seller"));
        var expectedAmount = NormalizeAmount(Get(expected, "amount"));
        var actualAmount = NormalizeAmount(Get(actual, "amount"));
        var expectedDate = NormalizeDate(Get(expected, "invoice_date"));
        var actualDate = NormalizeDate(Get(actual, "invoice_date"));
        var sellerMatches = expectedSeller.Length == 0
            || actualSeller.Contains(expectedSeller, StringComparison.OrdinalIgnoreCase)
            || expectedSeller.Contains(actualSeller, StringComparison.OrdinalIgnoreCase);
        var amountMatches = expectedAmount.Length == 0 || expectedAmount == actualAmount;
        var dateMatches = expectedDate.Length == 0 || expectedDate == actualDate;
        return (sellerMatches && amountMatches && dateMatches, "seller_amount_date");
    }

    private static string Get(IReadOnlyDictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

    private static string Compact(string value) => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    private static string NormalizeAmount(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+(?:\.\d{2})?");
        if (!match.Success) return string.Empty;
        var parts = match.Value.Split('.', 2);
        return parts[0] + "." + (parts.Length == 1 ? "00" : parts[1].PadRight(2, '0')[..2]);
    }

    private static string NormalizeDate(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(20\d{2})[-/.年](\d{1,2})[-/.月](\d{1,2})");
        if (!match.Success) return string.Empty;
        return $"{match.Groups[1].Value}-{int.Parse(match.Groups[2].Value):D2}-{int.Parse(match.Groups[3].Value):D2}";
    }
}