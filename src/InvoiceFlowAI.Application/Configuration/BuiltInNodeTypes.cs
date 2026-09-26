using InvoiceFlowAI.Contracts.Recipe;

namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Built-in node-type registry. Per design §5 the first release pins
/// exactly 8 node types; each registers its ports so the recipe graph
/// validator can catch typo'd connections before any data flows.
/// </summary>
public sealed class BuiltInNodeTypes : INodeTypeRegistry
{
    public static readonly BuiltInNodeTypes Default = new();

    private readonly Dictionary<string, NodeTypeDefinition> _byKey;

    public BuiltInNodeTypes()
    {
        _byKey = new Dictionary<string, NodeTypeDefinition>(StringComparer.Ordinal)
        {
            ["validate-request|1.0"] = new NodeTypeDefinition(
                "validate-request", "1.0",
                new[]
                {
                    new NodePortDefinition("Input", "Input", "RunInput"),
                    new NodePortDefinition("Valid", "Output", "ValidatedRunInput"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["scan-mailbox|1.0"] = new NodeTypeDefinition(
                "scan-mailbox", "1.0",
                new[]
                {
                    new NodePortDefinition("Input", "Input", "ValidatedRunInput"),
                    new NodePortDefinition("Messages", "Output", "PipelineItem<MailboxScanResult>"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["collect-candidates|1.0"] = new NodeTypeDefinition(
                "collect-candidates", "1.0",
                new[]
                {
                    new NodePortDefinition("Messages", "Input", "PipelineItem<MailboxScanResult>"),
                    new NodePortDefinition("Candidates", "Output", "PipelineItem<CandidateBatch>"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["recover-urls|1.0"] = new NodeTypeDefinition(
                "recover-urls", "1.0",
                new[]
                {
                    new NodePortDefinition("Candidates", "Input", "PipelineItem<CandidateBatch>"),
                    new NodePortDefinition("Candidates", "Output", "PipelineItem<CandidateBatch>"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["extract-documents|1.0"] = new NodeTypeDefinition(
                "extract-documents", "1.0",
                new[]
                {
                    new NodePortDefinition("Candidates", "Input", "PipelineItem<CandidateBatch>"),
                    new NodePortDefinition("Results", "Output", "PipelineItem<ExtractionBatch>"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["pair-artifacts|1.0"] = new NodeTypeDefinition(
                "pair-artifacts", "1.0",
                new[]
                {
                    new NodePortDefinition("Results", "Input", "PipelineItem<ExtractionBatch>"),
                    new NodePortDefinition("Pairs", "Output", "PipelineItem<PairingBatch>"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["archive-documents|1.0"] = new NodeTypeDefinition(
                "archive-documents", "1.0",
                new[]
                {
                    new NodePortDefinition("Pairs", "Input", "PipelineItem<PairingBatch>"),
                    new NodePortDefinition("Archived", "Output", "PipelineItem<ArchiveBatch>"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
            ["export-report|1.0"] = new NodeTypeDefinition(
                "export-report", "1.0",
                new[]
                {
                    new NodePortDefinition("Archived", "Input", "PipelineItem<ArchiveBatch>"),
                    new NodePortDefinition("Completed", "Output", "RunSummary"),
                    new NodePortDefinition("Failure", "Output", "RunFailure"),
                }),
        };
    }

    public bool TryGet(string type, string typeVersion, out NodeTypeDefinition definition)
    {
        return _byKey.TryGetValue($"{type}|{typeVersion}", out definition!);
    }
}

/// <summary>
/// Built-in connection validator. The first release uses a permissive
/// default: any source Output port may drive any sink Input port of a
/// compatible node. Tightening the policy (e.g. refusing cross-stage
/// loops) lands in a future revision as a parameterized check here so
/// tests can pin behaviour per release.
/// </summary>
public sealed class BuiltInConnections : IConnectionPolicy
{
    public static readonly BuiltInConnections Default = new();

    public bool AllowsConnection(
        string fromNodeId, string fromPort,
        string toNodeId, string toPort,
        NodeTypeDefinition fromType, NodeTypeDefinition toType)
    {
        _ = fromNodeId;
        _ = toNodeId;
        var fromDef = fromType.Ports.FirstOrDefault(p =>
            string.Equals(p.Name, fromPort, StringComparison.Ordinal) &&
            string.Equals(p.Direction, "Output", StringComparison.Ordinal));
        var toDef = toType.Ports.FirstOrDefault(p =>
            string.Equals(p.Name, toPort, StringComparison.Ordinal) &&
            string.Equals(p.Direction, "Input", StringComparison.Ordinal));
        if (fromDef is null || toDef is null) return false;
        return string.Equals(fromDef.PacketType, toDef.PacketType, StringComparison.Ordinal);
    }
}