using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Settings;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class UserSettingsStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public UserSettingsStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Load_returns_company_and_last_output_directory_from_singleton_row()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.UserSettings.Add(NewRow(companyName: "Example Buyer", lastOutputDirectory: "C:/Invoices"));
        await context.SaveChangesAsync();
        var store = new EfUserSettingsStore(context, new EfUnitOfWorkFactory(context));

        var snapshot = await store.LoadAsync(CancellationToken.None);

        snapshot.CompanyName.Should().Be("Example Buyer");
        snapshot.LastOutputDirectory.Should().Be("C:/Invoices");
        snapshot.Revision.Should().Be(4);
    }

    [Fact]
    public async Task Update_changes_company_and_output_directory_and_increments_revision()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.UserSettings.Add(NewRow());
        await context.SaveChangesAsync();
        var store = new EfUserSettingsStore(context, new EfUnitOfWorkFactory(context));

        var snapshot = await store.UpdateAsync(
            new SettingsUpdateRequest(4, CompanyName: "Example Buyer", LastOutputDirectory: "C:/Invoices"),
            CancellationToken.None);

        snapshot.Revision.Should().Be(5);
        snapshot.CompanyName.Should().Be("Example Buyer");
        snapshot.LastOutputDirectory.Should().Be("C:/Invoices");
    }

    [Fact]
    public async Task Update_with_stale_revision_throws_settings_revision_conflict()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.UserSettings.Add(NewRow());
        await context.SaveChangesAsync();
        var store = new EfUserSettingsStore(context, new EfUnitOfWorkFactory(context));

        var act = () => store.UpdateAsync(
            new SettingsUpdateRequest(3, CompanyName: "stale"),
            CancellationToken.None);

        await act.Should().ThrowAsync<SettingsRevisionConflictException>()
            .Where(error => error.ReasonCode == "SETTINGS_REVISION_CONFLICT");
    }

    [Fact]
    public async Task Last_output_directory_does_not_change_configuration_fingerprint()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.UserSettings.Add(NewRow());
        await context.SaveChangesAsync();
        var store = new EfUserSettingsStore(context, new EfUnitOfWorkFactory(context));

        var original = await store.LoadAsync(CancellationToken.None);
        var updated = await store.UpdateAsync(
            new SettingsUpdateRequest(original.Revision, LastOutputDirectory: "D:/Invoices"),
            CancellationToken.None);

        updated.ConfigurationFingerprint.Should().Be(original.ConfigurationFingerprint);
    }

    [Fact]
    public async Task Import_snapshot_runs_once_and_does_not_overwrite_existing_settings_afterward()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.UserSettings.Add(NewRow(revision: 1));
        await context.SaveChangesAsync();
        var store = new EfUserSettingsStore(context, new EfUnitOfWorkFactory(context));
        var imported = NewSnapshot("Imported Buyer", "C:/LegacyInvoices");

        await store.ImportSnapshotAsync(imported, CancellationToken.None);
        await store.ImportSnapshotAsync(NewSnapshot("Should Not Replace", "D:/Other"), CancellationToken.None);

        var actual = await store.LoadAsync(CancellationToken.None);
        actual.CompanyName.Should().Be("Imported Buyer");
        actual.LastOutputDirectory.Should().Be("C:/LegacyInvoices");
        (await context.LegacyImportState.FindAsync("settings-snapshot-v1")).Should().NotBeNull();
    }

    private static UserSettingsRow NewRow(
        string companyName = "",
        string? lastOutputDirectory = null,
        int revision = 4) => new()
    {
        SettingsId = "default",
        Revision = revision,
        DefaultMailbox = "INBOX",
        CompanyName = companyName,
        LastOutputDirectory = lastOutputDirectory,
        MailboxFiltersJson = "{\"includeReadMessages\":false,\"minAttachmentBytes\":null,\"maxAttachmentBytes\":null}",
        PipelineOptionsJson = "{}",
        RuleSetId = "default",
        RuleSetVersion = 1,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static UserSettingsSnapshot NewSnapshot(string companyName, string? outputDirectory) => new(
        Revision: 1,
        CurrentAccountId: null,
        DefaultMailbox: "INBOX",
        MailboxFilters: new MailboxFilterRules(false, null, null),
        Pipeline: new PipelineOptionsPatch(),
        AllowVisionFallback: false,
        RuleSetId: "default",
        RuleSetVersion: 1,
        ConfigurationFingerprint: "legacy-fingerprint",
        UpdatedAtUtc: DateTimeOffset.UnixEpoch,
        CompanyName: companyName,
        LastOutputDirectory: outputDirectory);
}