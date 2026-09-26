using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Runs;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Nodes;
using ZeroPipeline.Core.Ports;

namespace InvoiceFlowAI.Application.Pipeline.Nodes;

public sealed record ValidatedRunInput(RunInput Value);

public sealed class ValidateRequestNode : PipelineNode
{
    private readonly InputPort<RunInput> _input;
    private readonly OutputPort<ValidatedRunInput> _valid;
    private readonly OutputPort<RunFailure> _failure;
    private bool _failureEndPending;

    public ValidateRequestNode(string id = "validate-request", string name = "Validate request")
        : base(id, name)
    {
        _input = AddInputPort<RunInput>("Input", 1, BackpressurePolicy.Block);
        _valid = AddOutputPort<ValidatedRunInput>("Valid");
        _failure = AddOutputPort<RunFailure>("Failure");
    }

    public InputPort<RunInput> Input => _input;

    protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failureEndPending)
        {
            _failure.EmitEndOfStream(context.CycleId);
            _failureEndPending = false;
            return Task.CompletedTask;
        }
        if (!_input.TryReceive(out var packet)) return Task.CompletedTask;
        if (packet.IsEndOfStream)
        {
            _valid.EmitEndOfStream(packet.SequenceNumber);
            _failure.EmitEndOfStream(packet.SequenceNumber);
            return Task.CompletedTask;
        }

        var request = packet.Payload;
        var reason = Validate(request);
        if (reason is null)
        {
            _valid.Emit(new ValidatedRunInput(request), packet.SequenceNumber);
        }
        else
        {
            _failure.Emit(new RunFailure(
                request.RunId ?? string.Empty,
                "validate-request",
                "RUN_INPUT_INVALID",
                FailureCategory.Validation,
                Retryable: false,
                reason), packet.SequenceNumber);
            _valid.EmitEndOfStream(packet.SequenceNumber);
            _failureEndPending = true;
        }

        return Task.CompletedTask;
    }

    private static string? Validate(RunInput? request)
    {
        if (request is null) return "Run input is required.";
        if (string.IsNullOrWhiteSpace(request.RunId)) return "Run ID is required.";
        if (string.IsNullOrWhiteSpace(request.AccountId)) return "Mailbox account is required.";
        if (request.DateFrom > request.DateTo) return "Run start date must not be after its end date.";
        return null;
    }
}