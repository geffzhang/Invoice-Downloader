using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Domain.Runs;

namespace InvoiceFlowAI.Application.Pipeline;

public sealed record PipelineItem<T>(string RunId, T Payload, long Sequence, bool IsFinal = false);

public sealed record CandidateWorkItem(
    DocumentCandidate Candidate,
    ReadOnlyMemory<byte> Content,
    MailboxAttachmentCandidate? SourceAttachment = null);

public sealed record CandidateBatch(IReadOnlyList<CandidateWorkItem> Items);

public sealed record ExtractionBatch(IReadOnlyList<CandidateProcessResult> Results);

public sealed record PairingBatch(
    IReadOnlyList<PairingResult> Results,
    IReadOnlyList<CandidateProcessResult> CandidateResults);

public sealed record ArchiveBatch(
    IReadOnlyList<CandidateProcessResult> Results,
    IReadOnlyList<RunFailure> Failures);

public interface IPipelineStage<in TInput, TOutput>
{
    Task<TOutput> ExecuteAsync(TInput input, CancellationToken cancellationToken);
}

public interface ICandidateCollectionStage : IPipelineStage<MailboxScanResult, CandidateBatch> { }

public interface IUrlRecoveryStage : IPipelineStage<CandidateBatch, CandidateBatch> { }

public interface IDocumentExtractionStage : IPipelineStage<CandidateBatch, ExtractionBatch> { }

public interface IArtifactPairingStage : IPipelineStage<ExtractionBatch, PairingBatch> { }

public interface IDocumentArchivingStage : IPipelineStage<PairingBatch, ArchiveBatch> { }

public interface IReportExportStage
{
    Task<RunSummary> ExecuteAsync(PipelineItem<ArchiveBatch> input, CancellationToken cancellationToken);
}