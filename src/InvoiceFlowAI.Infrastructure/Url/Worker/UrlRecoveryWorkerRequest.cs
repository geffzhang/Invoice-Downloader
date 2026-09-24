using InvoiceFlowAI.Application.Mail;

namespace InvoiceFlowAI.Infrastructure.Url.Worker;

public sealed record UrlRecoveryWorkerRequest(
    int SchemaVersion,
    UrlCandidateGroup Group,
    string JobDirectory,
    int MaxResponseBytes,
    int MaxArtifacts,
    string ResultManifestPath);