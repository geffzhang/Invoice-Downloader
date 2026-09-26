namespace InvoiceFlowAI.Contracts.Parsers;

public sealed record ParserRegistry(
    string SchemaVersion,
    string RegistryFingerprint,
    IReadOnlyList<ParserDefinition> Parsers);