namespace InvoiceFlowAI.Application.Url;

public sealed record CapturedUrlArtifact(
    RecoveredArtifactKind Kind,
    string ContentType,
    ReadOnlyMemory<byte> Content,
    int SourceUrlOrdinal,
    string Sha256,
    string? SanitizedResolvedOrigin,
    IReadOnlyDictionary<string, string> InvoiceFields,
    bool? ExpectedMatch,
    string MatchReasonCode);