using FluentAssertions;
using InvoiceFlowAI.Application.Pipeline.Nodes;
using InvoiceFlowAI.Domain.Runs;
using ZeroPipeline.Core.Execution;
using ZeroPipeline.Core.Graph;
using ZeroPipeline.Core.Nodes;
using ZeroPipeline.Core.Ports;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Pipeline;

public sealed class BuiltInPipelineNodeTests
{
    [Fact]
    public void Validate_request_node_exposes_the_declared_run_input_port()
    {
        var node = new ValidateRequestNode();

        node.InputPorts.Should().ContainSingle()
            .Which.DataType.Should().Be(typeof(RunInput));
        node.OutputPorts.Select(port => (port.Name, port.DataType)).Should().BeEquivalentTo(
            new[]
            {
                ("Valid", typeof(ValidatedRunInput)),
                ("Failure", typeof(RunFailure)),
            });
    }

        [Fact]
        public async Task Validate_request_node_emits_typed_input_through_real_executor()
        {
            var request = new RunInput(
                "run-1",
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 1, 2),
                "output",
                string.Empty,
                "account-1");
            var source = new RunInputSourceNode(request);
            var validate = new ValidateRequestNode();
            var sink = new CaptureNode<ValidatedRunInput>();
            var graph = new PipelineGraph();
            graph.AddNode(source).AddNode(validate).AddNode(sink);
            graph.Connect(source.Output, validate.InputPorts.OfType<InputPort<RunInput>>().Single());
            graph.Connect(validate.OutputPorts.OfType<OutputPort<ValidatedRunInput>>().Single(), sink.Input);

            var executor = new PipelineExecutor(graph);
            var context = new PipelineContext(CancellationToken.None);
            await executor.InitializeAsync(context, CancellationToken.None);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                await executor.ExecuteStepAsync(context, CancellationToken.None);
            }

            sink.Value.Should().Be(new ValidatedRunInput(request));
        }

        private sealed class RunInputSourceNode : PipelineNode
        {
            private readonly RunInput _request;
            private bool _sent;

            public RunInputSourceNode(RunInput request) : base("test-source", "Test source")
            {
                _request = request;
                Output = AddOutputPort<RunInput>("Output");
            }

            public OutputPort<RunInput> Output { get; }

            protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
            {
                if (!_sent)
                {
                    Output.Emit(_request, 1);
                    _sent = true;
                }
                return Task.CompletedTask;
            }
        }

        private sealed class CaptureNode<T> : PipelineNode
        {
            public CaptureNode() : base("test-sink", "Test sink") =>
                Input = AddInputPort<T>("Input", 1, BackpressurePolicy.Block);

            public InputPort<T> Input { get; }
            public T? Value { get; private set; }

            protected override Task OnExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
            {
                if (Input.TryReceive(out var packet) && !packet.IsEndOfStream)
                {
                    Value = packet.Payload;
                }
                return Task.CompletedTask;
            }
        }
}