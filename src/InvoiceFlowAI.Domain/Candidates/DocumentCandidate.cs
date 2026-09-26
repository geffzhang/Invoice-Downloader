namespace InvoiceFlowAI.Domain.Candidates;

/// <summary>
/// Immutable candidate produced by the <c>collect-candidates</c> node.
/// <see cref="Sequence"/> is the per-run ordering that downstream nodes must
/// respect via <c>SequenceReorderBuffer</c>; <see cref="SourceMessageUid"/>
/// is the IMAP UID or "url:&lt;host&gt;" marker; <see cref="ProcessingRevision"/>
/// is incremented on retry but never on identity mutation.
/// </summary>
public sealed record DocumentCandidate(
    DocumentIdentity DocumentId,
    long Sequence,
    string CorrelationId,
    string SourceMessageUid,
    string OriginalFileName,
    string ContentType,
    long ContentLength,
    int ProcessingRevision,
    string SourceKind,
    Uri? SourceUrl = null,
    IReadOnlyDictionary<string, string>? Metadata = null);