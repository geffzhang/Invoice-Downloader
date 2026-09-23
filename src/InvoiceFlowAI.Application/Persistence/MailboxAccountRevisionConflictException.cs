// Thrown by IMailboxAccountStore when the supplied expectedRevision does
// not match the row's current Revision. Per §5 the dispatcher must surface
// this as the stable MAILBOX_ACCOUNT_REVISION_CONFLICT error code.

using InvoiceFlowAI.Contracts.Errors;

namespace InvoiceFlowAI.Application.Persistence;

public sealed class MailboxAccountRevisionConflictException : Exception
{
    public MailboxAccountRevisionConflictException(string accountId)
        : base($"Mailbox account '{accountId}' revision conflict; reload and retry.")
    {
        AccountId = accountId;
        ReasonCode = RpcErrorCodes.MailboxAccountRevisionConflict;
    }

    public string AccountId { get; }
    public string ReasonCode { get; }
}