namespace InvoiceFlowAI.Domain.Runs;

/// <summary>
/// Request body for a new run. The <see cref="CustomRules"/> field is
/// reserved for the future; first release rejects any non-empty value with
/// <c>RPC_INVALID_PARAMS</c> — rules are owned by <c>ruleset.save</c>.
/// </summary>
public sealed record RunInput(
    string RunId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string SavePath,
    string CustomRules,
    string AccountId,
    string Mailbox = "INBOX",
    string RunMode = "interactive");