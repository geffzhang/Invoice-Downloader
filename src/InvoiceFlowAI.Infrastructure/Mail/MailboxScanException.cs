namespace InvoiceFlowAI.Infrastructure.Mail;

public sealed class MailboxScanException : Exception
{
    public MailboxScanException(string reasonCode, string safeMessage)
        : base(safeMessage)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}