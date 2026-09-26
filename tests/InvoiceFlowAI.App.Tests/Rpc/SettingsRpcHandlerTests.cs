using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class SettingsRpcHandlerTests
{
    [Fact]
    public async Task Settings_get_returns_typed_snapshot()
    {
        var expected = NewSnapshot();
        var handler = new SettingsGetRpcHandler(new FakeUserSettingsStore(expected));

        var result = await handler.HandleAsync(NewRequest("settings.get", null), CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result!.Value.GetProperty("companyName").GetString().Should().Be("Buyer Ltd");
        result.Result.Value.GetProperty("lastOutputDirectory").GetString().Should().Be("C:/Invoices");
    }

    [Fact]
    public async Task Settings_update_returns_revisioned_snapshot()
    {
        var expected = NewSnapshot() with { Revision = 8, CompanyName = "New Buyer" };
        var handler = new SettingsUpdateRpcHandler(new FakeUserSettingsStore(expected));
        var request = new SettingsUpdateRequest(7, CompanyName: "New Buyer");

        var result = await handler.HandleAsync(NewRequest("settings.update", ToElement(request)), CancellationToken.None);

        result.Error.Should().BeNull();
        result.Result!.Value.GetProperty("revision").GetInt32().Should().Be(8);
        result.Result.Value.GetProperty("companyName").GetString().Should().Be("New Buyer");
    }

    [Fact]
    public async Task Settings_update_maps_revision_conflict_to_stable_rpc_error()
    {
        var handler = new SettingsUpdateRpcHandler(new FakeUserSettingsStore(NewSnapshot(),
            new SettingsRevisionConflictException(expectedRevision: 2, actualRevision: 4)));

        var result = await handler.HandleAsync(
            NewRequest("settings.update", ToElement(new SettingsUpdateRequest(2, CompanyName: "Buyer"))),
            CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("SETTINGS_REVISION_CONFLICT");
        result.Error.UserMessage.Should().NotContain("Buyer");
    }

    [Fact]
    public async Task Settings_update_rejects_unmapped_params()
    {
        var handler = new SettingsUpdateRpcHandler(new FakeUserSettingsStore(NewSnapshot()));
        using var document = JsonDocument.Parse("{\"expectedRevision\":1,\"secret\":\"private-value\"}");

        var result = await handler.HandleAsync(
            NewRequest("settings.update", document.RootElement),
            CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be(RpcDispatcher.InvalidParamsCode);
        result.Error.UserMessage.Should().NotContain("private-value");
    }

    private static RpcRequest<JsonElement?> NewRequest(string method, JsonElement? parameters)
        => new(RpcDispatcher.Protocol, "test-id", method, parameters);

    private static JsonElement? ToElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, JsonOptions.Default);

    private static UserSettingsSnapshot NewSnapshot() => new(
        7,
        null,
        "INBOX",
        new MailboxFilterRules(false, null, null),
        new PipelineOptionsPatch(),
        true,
        "default",
        1,
        "fingerprint",
        DateTimeOffset.UnixEpoch,
        "Buyer Ltd",
        "C:/Invoices");

    private sealed class FakeUserSettingsStore(
        UserSettingsSnapshot snapshot,
        Exception? updateException = null) : IUserSettingsStore
    {
        public Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(snapshot);

        public Task<UserSettingsSnapshot> UpdateAsync(SettingsUpdateRequest request, CancellationToken cancellationToken)
            => updateException is null
                ? Task.FromResult(snapshot)
                : Task.FromException<UserSettingsSnapshot>(updateException);

        public Task ImportSnapshotAsync(UserSettingsSnapshot value, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}