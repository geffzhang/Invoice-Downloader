using FluentAssertions;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Contracts.Settings;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Rpc;

public sealed class LegacySettingsSnapshotStoreTests
{
    [Fact]
    public async Task Json_snapshot_store_round_trips_persisted_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"invoiceflow-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonUserSettingsSnapshotStore(path);
            var expected = NewSnapshot();

            await store.SaveAsync(expected, CancellationToken.None);
            var actual = await store.LoadAsync(CancellationToken.None);

            actual.Should().BeEquivalentTo(expected);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static UserSettingsSnapshot NewSnapshot() => new(
        4,
        "account-1",
        "invoices@example.com",
        new MailboxFilterRules(true, 1024, 5_000_000, ["billing@example.com"], null, null),
        new PipelineOptionsPatch(MaxInFlightCandidates: 16),
        true,
        "default",
        1,
        "safe-fingerprint",
        new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero),
        "Example Buyer",
        "C:/Invoices");

}