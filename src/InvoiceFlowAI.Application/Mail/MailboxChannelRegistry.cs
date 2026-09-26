namespace InvoiceFlowAI.Application.Mail;

public sealed record MailboxChannelCapabilities(string Domain, string DisplayName, bool RequiresIdCommand);

public interface IMailboxChannelRegistry
{
    MailboxChannelCapabilities Resolve(string emailAddress);
}

public sealed class MailboxChannelRegistry : IMailboxChannelRegistry
{
    private static readonly MailboxChannelCapabilities Qq = new("qq.com", "QQ Mail", false);
    private static readonly MailboxChannelCapabilities Mail163 = new("163.com", "163 Mail", true);
    private static readonly MailboxChannelCapabilities Unknown = new("unknown", "Unknown", false);

    public MailboxChannelCapabilities Resolve(string emailAddress)
    {
        var domain = ExtractDomain(emailAddress);
        return domain switch
        {
            "qq.com" => Qq,
            "163.com" => Mail163,
            _ => Unknown,
        };
    }

    private static string ExtractDomain(string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return string.Empty;
        }

        var trimmed = emailAddress.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at < 0 || at == trimmed.Length - 1)
        {
            return string.Empty;
        }

        return trimmed[(at + 1)..].Trim().ToLowerInvariant();
    }
}