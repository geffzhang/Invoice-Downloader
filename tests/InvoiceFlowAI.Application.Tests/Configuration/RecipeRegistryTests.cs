// Tests for RecipeRegistry. The registry must:
//   * Load the canonical invoiceflow.default.v1.json fixture
//   * Validate each node's type / typeVersion / ports against the
//     built-in registry
//   * Reject any node whose type isn't registered
//   * Reject any node with an unknown port
//   * Reject any unknown connection
//   * Build a typed graph consumable by ZeroPipeline

using FluentAssertions;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Contracts.Recipe;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Configuration;

public sealed class RecipeRegistryTests
{
    private const string DefaultFixtureJson = """
        {
          "schemaVersion": "1.0",
          "recipeId": "invoiceflow.default",
          "recipeVersion": "2026-09-23-v1",
          "zeroPipelineRecipeVersion": "1.2.0",
          "nodes": [
            { "nodeId": "validate", "type": "validate-request", "typeVersion": "1.0", "parameters": { "stagingDirectory": "%LOCALAPPDATA%/InvoiceFlowAI/staging", "requireCredentials": true } },
            { "nodeId": "scan", "type": "scan-mailbox", "typeVersion": "1.0", "parameters": { "headerBatchSize": 200, "messageBatchSize": 25, "maxAttempts": 2 } },
            { "nodeId": "candidates", "type": "collect-candidates", "typeVersion": "1.0", "parameters": { "maxAttachmentBytes": 5242880, "allowNestedZip": true } },
            { "nodeId": "recover", "type": "recover-urls", "typeVersion": "1.0", "parameters": { "maxAttempts": 2, "requestTimeoutSeconds": 60, "maxDownloadBytes": 5242880, "allowBrowserFallback": true } },
            { "nodeId": "extract", "type": "extract-documents", "typeVersion": "1.0", "parameters": { "allowOcrFallback": true, "allowVisionFallback": true, "minimumConfidence": 0.85 } },
            { "nodeId": "pair", "type": "pair-artifacts", "typeVersion": "1.0", "parameters": { "autoAcceptScore": 180, "manualReviewScore": 100, "allowCrossMessagePairing": true } },
            { "nodeId": "archive", "type": "archive-documents", "typeVersion": "1.0", "parameters": { "overwriteExisting": false, "preserveOriginal": true, "namingPolicyVersion": "2026-09-23-v1" } },
            { "nodeId": "report", "type": "export-report", "typeVersion": "1.0", "parameters": { "templateVersion": "2026-09-23-v1" } }
          ],
          "connections": [
            { "fromNodeId": "validate", "fromPort": "Valid", "toNodeId": "scan", "toPort": "Input" },
            { "fromNodeId": "scan", "fromPort": "Messages", "toNodeId": "candidates", "toPort": "Messages" },
            { "fromNodeId": "candidates", "fromPort": "Candidates", "toNodeId": "recover", "toPort": "Candidates" },
            { "fromNodeId": "recover", "fromPort": "Candidates", "toNodeId": "extract", "toPort": "Candidates" },
            { "fromNodeId": "extract", "fromPort": "Results", "toNodeId": "pair", "toPort": "Results" },
            { "fromNodeId": "pair", "fromPort": "Pairs", "toNodeId": "archive", "toPort": "Pairs" },
            { "fromNodeId": "archive", "fromPort": "Archived", "toNodeId": "report", "toPort": "Archived" }
          ],
          "execution": {
            "controlCapacity": 1, "mailboxBatchCapacity": 4, "candidateBatchCapacity": 8, "extractionCapacity": 4,
            "resultCapacity": 8, "eventCapacity": 64, "imapConcurrency": 1, "ocrPageConcurrency": 2,
            "ocrLineWorkers": 2, "deepSeekConcurrency": 2, "browserConcurrency": 1, "archiveConcurrency": 2,
            "sqliteWriterConcurrency": 1, "maxInFlightCandidates": 32, "maxReorderItems": 32,
            "maxRetryAttempts": 2, "nodeTimeoutSeconds": 300, "retryGapTimeoutSeconds": 600
          }
        }
        """;

    [Fact]
    public async Task Loads_default_recipe_and_validates_typed_graph()
    {
        var registry = new RecipeRegistry(
            BuiltInNodeTypes.Default,
            BuiltInConnections.Default,
            _ => Task.FromResult(DefaultFixtureJson));
        var recipe = await registry.LoadDefaultAsync(CancellationToken.None);

        recipe.RecipeId.Should().Be("invoiceflow.default");
        recipe.SchemaVersion.Should().Be("1.0");
        recipe.Nodes.Should().HaveCount(8);
        recipe.Connections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Rejects_unknown_node_type()
    {
        var registry = new RecipeRegistry(BuiltInNodeTypes.Default, BuiltInConnections.Default, null);
        var act = async () => await registry.ValidateAsync(
            new RecipeValidationRequest(
                Nodes: new[]
                {
                    new RecipeNode(
                        NodeId: "rogue",
                        Type: "not-a-real-type",
                        TypeVersion: "1.0",
                        Parameters: new Dictionary<string, object?>()),
                },
                Connections: Array.Empty<RecipeConnection>()),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RecipeValidationException>();
        ex.Which.ReasonCode.Should().Be(RpcErrorCodes.RecipeNodeUnknown);
    }

    [Fact]
    public async Task Rejects_unknown_port_in_connection()
    {
        var registry = new RecipeRegistry(BuiltInNodeTypes.Default, BuiltInConnections.Default, null);
        var nodes = new[]
        {
            new RecipeNode("validate", "validate-request", "1.0", new Dictionary<string, object?>()),
            new RecipeNode("scan", "scan-mailbox", "1.0", new Dictionary<string, object?>()),
        };
        var connections = new[]
        {
            new RecipeConnection("validate", "GhostPort", "scan", "Input"),
        };

        var act = async () => await registry.ValidateAsync(
            new RecipeValidationRequest(nodes, connections),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RecipeValidationException>();
        ex.Which.ReasonCode.Should().Be(RpcErrorCodes.RecipePortInvalid);
    }

    [Fact]
    public async Task Rejects_connection_between_ports_with_incompatible_packet_types()
    {
        var registry = new RecipeRegistry(BuiltInNodeTypes.Default, BuiltInConnections.Default, null);
        var nodes = new[]
        {
            new RecipeNode("validate", "validate-request", "1.0", new Dictionary<string, object?>()),
            new RecipeNode("scan", "scan-mailbox", "1.0", new Dictionary<string, object?>()),
        };
        var connections = new[]
        {
            new RecipeConnection("validate", "Failure", "scan", "Input"),
        };

        var act = async () => await registry.ValidateAsync(
            new RecipeValidationRequest(nodes, connections),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RecipeValidationException>();
        ex.Which.ReasonCode.Should().Be(RpcErrorCodes.RecipePortInvalid);
    }
}