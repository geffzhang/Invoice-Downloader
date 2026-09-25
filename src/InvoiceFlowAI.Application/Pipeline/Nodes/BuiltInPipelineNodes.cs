using InvoiceFlowAI.Application.Mail;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Nodes;
using ZeroPipeline.Core.Ports;

namespace InvoiceFlowAI.Application.Pipeline.Nodes;

public sealed class ScanMailboxNode : PipelineNode
{
    private readonly IMailboxScanner _scanner;
    private readonly InputPort<ValidatedRunInput> _input;
    private readonly OutputPort<PipelineItem<MailboxScanResult>> _messages;
    private readonly OutputPort<RunFailure> _failure;
    private bool _failureEndPending;

    public ScanMailboxNode(IMailboxScanner scanner, string id = "scan-mailbox", string name = "Scan mailbox")
        : base(id, name)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _input = AddInputPort<ValidatedRunInput>("Input", 1, BackpressurePolicy.Block);
        _messages = AddOutputPort<PipelineItem<MailboxScanResult>>("Messages");
        _failure = AddOutputPort<RunFailure>("Failure");
    }

    protected override async Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failureEndPending)
        {
            _failure.EmitEndOfStream(context.CycleId);
            _failureEndPending = false;
            return;
        }
        if (!_input.TryReceive(out var packet)) return;
        if (packet.IsEndOfStream)
        {
            _messages.EmitEndOfStream(packet.SequenceNumber);
            _failure.EmitEndOfStream(packet.SequenceNumber);
            return;
        }

        var request = packet.Payload.Value;
        try
        {
            var result = await _scanner.ScanAsync(
                new MailboxScanRequest(request.AccountId, request.DateFrom, SinceUid: null, UidValidity: null),
                cancellationToken).ConfigureAwait(false);
            _messages.Emit(new PipelineItem<MailboxScanResult>(request.RunId, result, packet.SequenceNumber, IsFinal: true, OutputRoot: request.SavePath), packet.SequenceNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failure.Emit(new RunFailure(
                request.RunId,
                "scan-mailbox",
                "MAILBOX_SCAN_FAILED",
                FailureCategory.Network,
                Retryable: true,
                SafeMessage: "The mailbox could not be scanned.",
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name), packet.SequenceNumber);
            _messages.EmitEndOfStream(packet.SequenceNumber);
            _failureEndPending = true;
        }
    }
}

public sealed class CollectCandidatesNode : PipelineStageNode<MailboxScanResult, CandidateBatch>
{
    public CollectCandidatesNode(ICandidateCollectionStage stage, string id = "collect-candidates", string name = "Collect candidates")
        : base(id, name, "collect-candidates", stage, "Messages", "Candidates") { }
}

public sealed class RecoverUrlsNode : PipelineStageNode<CandidateBatch, CandidateBatch>
{
    public RecoverUrlsNode(IUrlRecoveryStage stage, string id = "recover-urls", string name = "Recover URLs")
        : base(id, name, "recover-urls", stage, "Candidates", "Candidates") { }
}

public sealed class ExtractDocumentsNode : PipelineStageNode<CandidateBatch, ExtractionBatch>
{
    public ExtractDocumentsNode(IDocumentExtractionStage stage, string id = "extract-documents", string name = "Extract documents")
        : base(id, name, "extract-documents", stage, "Candidates", "Results") { }
}

public sealed class PairArtifactsNode : PipelineStageNode<ExtractionBatch, PairingBatch>
{
    public PairArtifactsNode(IArtifactPairingStage stage, string id = "pair-artifacts", string name = "Pair artifacts")
        : base(id, name, "pair-artifacts", stage, "Results", "Pairs") { }
}

public sealed class ArchiveDocumentsNode : PipelineNode
{
    private readonly IDocumentArchivingStage _stage;
    private readonly InputPort<PipelineItem<PairingBatch>> _input;
    private readonly OutputPort<PipelineItem<ArchiveBatch>> _output;
    private readonly OutputPort<RunFailure> _failure;
    private bool _failureEndPending;

    public ArchiveDocumentsNode(IDocumentArchivingStage stage, string id = "archive-documents", string name = "Archive documents")
        : base(id, name)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _input = AddInputPort<PipelineItem<PairingBatch>>("Pairs", 1, BackpressurePolicy.Block);
        _output = AddOutputPort<PipelineItem<ArchiveBatch>>("Archived");
        _failure = AddOutputPort<RunFailure>("Failure");
    }

    protected override async Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failureEndPending)
        {
            _failure.EmitEndOfStream(context.CycleId);
            _failureEndPending = false;
            return;
        }
        if (!_input.TryReceive(out var packet)) return;
        if (packet.IsEndOfStream)
        {
            _output.EmitEndOfStream(packet.SequenceNumber);
            _failure.EmitEndOfStream(packet.SequenceNumber);
            return;
        }

        var item = packet.Payload;
        try
        {
            var output = await _stage.ExecuteAsync(
                new ArchiveStageRequest(item.RunId, item.OutputRoot ?? string.Empty, item.Payload),
                cancellationToken).ConfigureAwait(false);
            _output.Emit(new PipelineItem<ArchiveBatch>(item.RunId, output, item.Sequence, item.IsFinal, item.OutputRoot), packet.SequenceNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failure.Emit(new RunFailure(
                item.RunId,
                "archive-documents",
                "ARCHIVE_DOCUMENTS_FAILED",
                FailureCategory.Internal,
                Retryable: false,
                SafeMessage: "The documents could not be archived.",
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name), packet.SequenceNumber);
            _output.EmitEndOfStream(packet.SequenceNumber);
            _failureEndPending = true;
        }
    }
}

public sealed class ExportReportNode : PipelineNode
{
    private readonly IReportExportStage _stage;
    private readonly InputPort<PipelineItem<ArchiveBatch>> _input;
    private readonly OutputPort<RunSummary> _completed;
    private readonly OutputPort<RunFailure> _failure;
    private bool _failureEndPending;

    public ExportReportNode(IReportExportStage stage, string id = "export-report", string name = "Export report")
        : base(id, name)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _input = AddInputPort<PipelineItem<ArchiveBatch>>("Archived", 1, BackpressurePolicy.Block);
        _completed = AddOutputPort<RunSummary>("Completed");
        _failure = AddOutputPort<RunFailure>("Failure");
    }

    protected override async Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failureEndPending)
        {
            _failure.EmitEndOfStream(context.CycleId);
            _failureEndPending = false;
            return;
        }
        if (!_input.TryReceive(out var packet)) return;
        if (packet.IsEndOfStream)
        {
            _completed.EmitEndOfStream(packet.SequenceNumber);
            _failure.EmitEndOfStream(packet.SequenceNumber);
            return;
        }

        try
        {
            var summary = await _stage.ExecuteAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
            _completed.Emit(summary, packet.SequenceNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failure.Emit(new RunFailure(
                packet.Payload.RunId,
                "export-report",
                "REPORT_EXPORT_FAILED",
                FailureCategory.Persistence,
                Retryable: false,
                SafeMessage: "The run report could not be exported.",
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name), packet.SequenceNumber);
            _completed.EmitEndOfStream(packet.SequenceNumber);
            _failureEndPending = true;
        }
    }
}