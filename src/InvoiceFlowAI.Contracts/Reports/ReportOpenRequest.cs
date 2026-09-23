// Report open token contracts. The token is one-time and time-bounded so a
// hostile caller that obtains the ReportPath cannot replay it indefinitely.

namespace InvoiceFlowAI.Contracts.Reports;

public sealed record ReportOpenRequest(
    string RunId,
    string ReportPath,
    string ContentHash);

public sealed record ReportOpenToken(
    string TokenId,
    string RunId,
    string RelativePath,
    string ContentHash,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool AlreadyConsumed);