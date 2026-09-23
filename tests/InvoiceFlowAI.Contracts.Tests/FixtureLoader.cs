// Fixture loader for golden RPC/Recipe/RuleSet/registry fixtures committed under
// docs/superpowers/fixtures. Fixtures are linked into the test output via the
// project file's <None Include="..\..\docs\..." CopyToOutputDirectory="PreserveNewest" />
// so tests resolve paths relative to AppContext.BaseDirectory without copying
// the source. Each helper returns the JSON text + a parsed JsonDocument so tests
// can assert both the raw schema/version fields and the round-tripped record
// equality in one place.

using System.Text;
using System.Text.Json;

namespace InvoiceFlowAI.Contracts.Tests;

public static class FixtureLoader
{
    private const string FixturesRoot = "Fixtures";

    public static string ReadText(string relativePath)
    {
        var path = Path.Combine(FixturesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var full = Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Fixture '{relativePath}' not found at '{full}'. " +
                "Ensure the test project links docs/superpowers/fixtures into its output.");
        }
        return File.ReadAllText(full, Encoding.UTF8);
    }

    public static JsonDocument Read(string relativePath) =>
        JsonDocument.Parse(ReadText(relativePath));

    public static string FixturesDir =>
        Path.Combine(AppContext.BaseDirectory, FixturesRoot);
}