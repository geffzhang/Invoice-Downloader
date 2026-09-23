namespace InvoiceFlowAI.Application.Mail;

public sealed class EmailTierClassifier : IEmailTierClassifier
{
    private static readonly string[] TierOneDomains =
    [
        "12306.cn",
        "rails.com.cn",
        "didichuxing.com",
        "gaode.com",
        "marriott.com",
        "hworld.com",
        "cits.com",
        "meituan.com",
        "carlsonwagonlit.com",
        "mycwt.com",
        "citsgbt.com"
    ];

    private static readonly string[] TierTwoKeywords =
    [
        "发票",
        "行程单",
        "账单",
        "receipt",
        "invoice"
    ];

    private static readonly string[] TierThreeKeywords =
    [
        "发票",
        "报销",
        "行程",
        "差旅"
    ];

    public int Classify(string? sender, string? subject, string? bodyText)
    {
        if (HasTierOneSignal(sender)) return 1;
        if (ContainsAny(subject, TierTwoKeywords)) return 2;
        if (ContainsAny(bodyText, TierThreeKeywords)) return 3;
        return 4;
    }

    private static bool HasTierOneSignal(string? sender)
    {
        var senderAddress = ExtractSenderAddress(sender);
        if (string.IsNullOrWhiteSpace(senderAddress))
        {
            return false;
        }

        var atIndex = senderAddress.LastIndexOf('@');
        if (atIndex < 0 || atIndex == senderAddress.Length - 1)
        {
            return false;
        }

        var senderDomain = "@" + senderAddress[(atIndex + 1)..].Trim();

        foreach (var knownDomain in TierOneDomains)
        {
            if (senderDomain.Contains("@" + knownDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractSenderAddress(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender))
        {
            return null;
        }

        var trimmed = sender.Trim();
        var leftAngle = trimmed.IndexOf('<');
        var rightAngle = trimmed.LastIndexOf('>');

        if (leftAngle >= 0 && rightAngle > leftAngle)
        {
            var candidate = trimmed[(leftAngle + 1)..rightAngle].Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return trimmed;
    }

    private static bool ContainsAny(string? text, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}