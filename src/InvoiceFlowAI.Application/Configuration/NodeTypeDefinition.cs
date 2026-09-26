namespace InvoiceFlowAI.Application.Configuration;

/// <summary>
/// Declares the ports a node type exposes. The registry verifies every
/// connection's port appears in the source / sink node's declaration.
/// </summary>
public sealed record NodePortDefinition(string Name, string Direction, string PacketType);

/// <summary>
/// Declares a node type and its parameter / port contract. The first
/// release pins exactly 8 node types per design §5.
/// </summary>
public sealed record NodeTypeDefinition(
    string Type,
    string TypeVersion,
    IReadOnlyList<NodePortDefinition> Ports);

public interface INodeTypeRegistry
{
    bool TryGet(string type, string typeVersion, out NodeTypeDefinition definition);
}