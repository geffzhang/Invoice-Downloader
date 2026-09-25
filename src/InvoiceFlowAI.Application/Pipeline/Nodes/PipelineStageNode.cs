using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Nodes;
using ZeroPipeline.Core.Ports;

namespace InvoiceFlowAI.Application.Pipeline.Nodes;

public abstract class PipelineStageNode<TInput, TOutput> : PipelineNode
{
    private readonly string _stage;
    private readonly IPipelineStage<TInput, TOutput> _implementation;
    private readonly InputPort<PipelineItem<TInput>> _input;
    private readonly OutputPort<PipelineItem<TOutput>> _output;
    private readonly OutputPort<RunFailure> _failure;
    private bool _failureEndPending;

    protected PipelineStageNode(
        string id,
        string name,
        string stage,
        IPipelineStage<TInput, TOutput> implementation,
        string inputPort,
        string outputPort)
        : base(id, name)
    {
        _stage = stage;
        _implementation = implementation ?? throw new ArgumentNullException(nameof(implementation));
        _input = AddInputPort<PipelineItem<TInput>>(inputPort, 1, BackpressurePolicy.Block);
        _output = AddOutputPort<PipelineItem<TOutput>>(outputPort);
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
            var output = await _implementation.ExecuteAsync(item.Payload, cancellationToken).ConfigureAwait(false);
            _output.Emit(new PipelineItem<TOutput>(item.RunId, output, item.Sequence, item.IsFinal, item.OutputRoot), packet.SequenceNumber);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failure.Emit(new RunFailure(
                item.RunId,
                _stage,
                $"{_stage.ToUpperInvariant().Replace('-', '_')}_FAILED",
                FailureCategory.Internal,
                Retryable: false,
                SafeMessage: "The pipeline stage failed.",
                ExceptionType: exception.GetType().FullName ?? exception.GetType().Name), packet.SequenceNumber);
            _output.EmitEndOfStream(packet.SequenceNumber);
            _failureEndPending = true;
        }
    }
}