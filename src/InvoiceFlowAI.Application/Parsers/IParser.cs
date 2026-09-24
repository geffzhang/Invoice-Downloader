// Parser abstractions (design §3). Each parser is a candidate-isolated
// transformation that takes a document and produces either an invoice
// or a structured failure. The pipeline routes by SourceKind and
// ParserId and resolves conflicts by priority + manual_review.

using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Parsers;

public interface IParser
{
    string ParserId { get; }
    string Version { get; }
    int Priority { get; }
    IReadOnlyList<string> SourceKinds { get; }
    string FailureCode { get; }
    bool CanParse(ParserWorkItem workItem);
    Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken);
}

public sealed record ParserWorkItem(
    DocumentCandidate Candidate,
    string DocumentId,
    string SourceKind,
    ReadOnlyMemory<byte> DocumentBytes,
    string? EmailBody = null,
    string? EmailSubject = null,
    string? EmailSender = null);

public sealed record ParserOutcome(
    string ParserId,
    string Version,
    InvoiceDocument? Invoice,
    IReadOnlyList<string> MissingFields,
    CandidateFailure? Failure,
    ParserOutcomeDisposition Disposition = ParserOutcomeDisposition.Resolved);

public enum ParserOutcomeDisposition
{
    Resolved,
    NeedsFallback,
    Failed,
}

public interface IParserRegistry
{
    IReadOnlyList<ParserDescriptor> List();
    IParser? Resolve(string parserId);
    IReadOnlyList<IParser> ResolveBySourceKind(string sourceKind);
}

public sealed record ParserDescriptor(
    string ParserId,
    string Version,
    int Priority,
    IReadOnlyList<string> SourceKinds,
    string FailureCode);
