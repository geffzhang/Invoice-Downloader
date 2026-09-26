namespace InvoiceFlowAI.Application.Configuration;

public interface IConnectionPolicy
{
    /// <summary>True if (fromNode, fromPort) -> (toNode, toPort) is structurally allowed.</summary>
    bool AllowsConnection(string fromNodeId, string fromPort, string toNodeId, string toPort, NodeTypeDefinition fromType, NodeTypeDefinition toType);
}