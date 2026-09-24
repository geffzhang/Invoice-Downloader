using InvoiceFlowAI.Application.Url;

namespace InvoiceFlowAI.Infrastructure.Url.Worker;

public sealed record UrlRecoveryWorkerResponse(
    int SchemaVersion,
    string? FailureReasonCode,
    int? SelectedArtifactIndex,
    IReadOnlyList<UrlRecoveryWorkerArtifactManifest> Artifacts);

public sealed record UrlRecoveryWorkerArtifactManifest(
    string RelativePath,
    RecoveredArtifactKind Kind,
    string ContentType,
    long ByteLength,
    string Sha256,
    int SourceUrlOrdinal,
    string? SanitizedResolvedOrigin,
    IReadOnlyDictionary<string, string> InvoiceFields,
    bool? ExpectedMatch,
    string MatchReasonCode);

public sealed class UrlRecoveryWorkerProtocolException : Exception
{
    public const string ProtocolInvalidReasonCode = "URL_RECOVERY_WORKER_PROTOCOL_INVALID";

    public UrlRecoveryWorkerProtocolException()
        : base("URL recovery worker protocol validation failed.")
    {
    }

    public string ReasonCode => ProtocolInvalidReasonCode;
}