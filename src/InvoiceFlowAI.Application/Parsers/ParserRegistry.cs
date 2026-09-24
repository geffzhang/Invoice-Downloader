// Default in-memory parser registry. Loads parser descriptors and
// concrete IParser instances from the registry fixture and the four
// parser success/missing-field fixtures.

using System.Text.Json;

namespace InvoiceFlowAI.Application.Parsers;

public sealed class ParserRegistry : IParserRegistry
{
    private readonly Dictionary<string, IParser> _byId;
    private readonly IReadOnlyList<ParserDescriptor> _descriptors;

    public ParserRegistry(IEnumerable<IParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        _byId = parsers.ToDictionary(p => p.ParserId, StringComparer.Ordinal);
        _descriptors = _byId.Values
            .Select(p => new ParserDescriptor(p.ParserId, p.Version, p.Priority, p.SourceKinds, p.FailureCode))
            .ToList();
    }

    public IReadOnlyList<ParserDescriptor> List() => _descriptors;

    public IParser? Resolve(string parserId) =>
        _byId.TryGetValue(parserId, out var p) ? p : null;

    public IReadOnlyList<IParser> ResolveBySourceKind(string sourceKind) =>
        _byId.Values
            .Where(p => p.SourceKinds.Contains(sourceKind, StringComparer.OrdinalIgnoreCase))
            .ToList();

    public static ParserRegistry FromRegistryFixture(
        string registryFixturePath,
        Func<string, string, IParser> factory)
    {
        using var stream = File.OpenRead(registryFixturePath);
        using var doc = JsonDocument.Parse(stream);
        var parsers = new List<IParser>();
        foreach (var entry in doc.RootElement.GetProperty("parsers").EnumerateArray())
        {
            var parserId = entry.GetProperty("parserId").GetString() ?? "";
            var version = entry.GetProperty("version").GetString() ?? "1.0";
            var priority = entry.GetProperty("priority").GetInt32();
            var sourceKinds = entry.GetProperty("sourceKinds").EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .ToList();
            var failureCode = entry.GetProperty("failureCode").GetString() ?? "PARSER_FAILED";
            var fixtureId = entry.GetProperty("fixtureId").GetString() ?? "";
            var parser = factory(parserId, fixtureId);
            parsers.Add(new DescriptorAdapter(parser, version, priority, sourceKinds, failureCode));
        }
        return new ParserRegistry(parsers);
    }

    private sealed class DescriptorAdapter : IParser
    {
        private readonly IParser _inner;
        public string ParserId => _inner.ParserId;
        public string Version { get; }
        public int Priority { get; }
        public IReadOnlyList<string> SourceKinds { get; }
        public string FailureCode { get; }
        public bool CanParse(ParserWorkItem workItem) => _inner.CanParse(workItem);
        public DescriptorAdapter(IParser inner, string version, int priority, IReadOnlyList<string> sourceKinds, string failureCode)
        {
            _inner = inner;
            Version = version;
            Priority = priority;
            SourceKinds = sourceKinds;
            FailureCode = failureCode;
        }
        public Task<ParserOutcome> ParseAsync(ParserWorkItem workItem, CancellationToken cancellationToken)
            => _inner.ParseAsync(workItem, cancellationToken);
    }
}
