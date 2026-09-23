namespace InvoiceFlowAI.Contracts.Parsers;

/// <summary>
/// Special-parser entry. <c>SourceKinds</c> selects which source-kind
/// values this parser claims (e.g. <c>pdf</c>, <c>ofd</c>, <c>xml</c>,
/// <c>image</c>, <c>url</c>); <c>FailureCode</c> is the stable reason
/// code surfaced when the parser raises a candidate-level failure.
/// </summary>
public sealed record ParserDefinition(
    string ParserId,
    string Version,
    int Priority,
    IReadOnlyList<string> SourceKinds,
    string FixtureId,
    string FailureCode);