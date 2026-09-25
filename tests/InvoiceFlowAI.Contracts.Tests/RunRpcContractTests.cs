using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using InvoiceFlowAI.Contracts.Rpc;
using Xunit;

namespace InvoiceFlowAI.Contracts.Tests;

public sealed class RunRpcContractTests
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Run_start_request_uses_camel_case_and_contains_no_credentials()
    {
        var request = new RunStartRequest(
            "run-1", "account-1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30),
            "C:\\Invoices", "Example Co", "standard");

        var json = JsonSerializer.Serialize(request, Strict);
        using var document = JsonDocument.Parse(json);

        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("runId", "accountId", "dateFrom", "dateTo", "outputDirectory", "companyName", "runMode");
        json.Should().Contain("\"dateFrom\":\"2026-09-01\"")
            .And.NotContain("authCode").And.NotContain("apiKey").And.NotContain("password");
    }

    [Fact]
    public void Run_progress_round_trips_camel_case_state_and_empty_logs()
    {
        var snapshot = new RunProgressSnapshot(
            RunState.Running, true, true, false, 25, "正在扫描", new RunProgressStats(4, 1, 0),
            Array.Empty<RunLogEntry>(), null, false, null, "build-1", "2026-09-01..2026-09-30", "after:2026/09/01");

        var json = JsonSerializer.Serialize(snapshot, Strict);
        using var document = JsonDocument.Parse(json);
        var roundTrip = JsonSerializer.Deserialize<RunProgressSnapshot>(json, Strict);

        document.RootElement.GetProperty("runState").GetString().Should().Be("running");
        document.RootElement.GetProperty("stats").GetProperty("emails").GetInt32().Should().Be(4);
        document.RootElement.GetProperty("logs").GetArrayLength().Should().Be(0);
        roundTrip.Should().BeEquivalentTo(snapshot);
        json.Should().NotContain("TOP_SECRET_AUTH").And.NotContain("TOP_SECRET_API");
    }

    [Fact]
    public void Run_results_round_trip_empty_collections_and_nullable_paths()
    {
        var snapshot = new RunResultsSnapshot(
            new Dictionary<string, int>(), Array.Empty<RunInvoiceResult>(), Array.Empty<RunErrorInvoiceResult>(),
            Array.Empty<RunGroupedErrorResult>(), null, null, new Dictionary<string, int>(), "build-1",
            null, null, new Dictionary<string, int>(), new Dictionary<string, int>(), false, null, null);

        var json = JsonSerializer.Serialize(snapshot, Strict);
        var roundTrip = JsonSerializer.Deserialize<RunResultsSnapshot>(json, Strict);

        roundTrip.Should().BeEquivalentTo(snapshot);
        json.Should().Contain("\"successInvoices\":[]").And.Contain("\"manualCheckPath\":null")
            .And.NotContain("authCode").And.NotContain("apiKey").And.NotContain("password");
    }

    [Fact]
    public void Run_start_request_rejects_unknown_fields()
    {
        const string json = """{"runId":"r1","accountId":"a1","dateFrom":"2026-09-01","dateTo":"2026-09-30","outputDirectory":"C:\\Invoices","companyName":"Example","runMode":"standard","secret":"injected"}""";

        var act = () => JsonSerializer.Deserialize<RunStartRequest>(json, Strict);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Run_context_and_desktop_actions_have_typed_shapes()
    {
        var context = new RunContextSnapshot(false, false, false, 0, null, null, null, null, null, null);
        var stop = new RunStopRequest("run-1");
        var fileAction = new DesktopFileActionRequest("run-1", "invoice.pdf");
        var actionResult = new DesktopActionResult(true, null, null);
        var startResult = new RunStartResult(true, "run-1", null);

        JsonSerializer.Serialize(context, Strict).Should().Contain("\"autostartEnabled\":false");
        JsonSerializer.Serialize(stop, Strict).Should().Be("{\"runId\":\"run-1\"}");
        JsonSerializer.Serialize(fileAction, Strict).Should().Contain("\"path\":\"invoice.pdf\"");
        JsonSerializer.Serialize(actionResult, Strict).Should().Contain("\"succeeded\":true");
        JsonSerializer.Serialize(startResult, Strict).Should().Contain("\"accepted\":true");
    }
}