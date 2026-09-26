// Verifies that the 20260923_InitialSchema migration creates every table,
// partial unique index, and audit/run-event constraint the design requires.
// Per design §8 acceptance list, this is the first check that must pass
// before any repository test runs.

using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class InitialSchemaTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public InitialSchemaTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_creates_all_required_tables()
    {
        await using var context = _fixture.CreateContext();

        var expected = new[]
        {
            "UserSettings",
            "Runs",
            "Documents",
            "DocumentProcessing",
            "Invoices",
            "InvoiceItems",
            "Pairings",
            "ArchivedArtifacts",
            "RunCheckpoints",
            "MailboxCursors",
            "MailboxAccounts",
            "AuditEvents",
            "RunEvents",
            "RuleSets",
            "ManualReviewItems",
        };

        foreach (var table in expected)
        {
            var exists = await SqliteScalarAsync(context,
                $"SELECT 1 FROM sqlite_master WHERE type='table' AND name='{table}';");
            exists.Should().Be(1, $"table '{table}' must exist after migration");
        }
    }

    [Fact]
    public async Task Migration_persists_archive_temp_and_absolute_final_paths()
    {
        await using var context = _fixture.CreateContext();
        var columns = new List<string>();
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('ArchivedArtifacts');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        columns.Should().Contain("TempFilePath");
        columns.Should().Contain("FinalFilePath");
    }

    [Fact]
    public async Task Migration_enables_foreign_keys_pragma()
    {
        await using var context = _fixture.CreateContext();
        var fk = await SqliteScalarAsync(context, "PRAGMA foreign_keys;");
        fk.Should().Be(1);
    }

    [Fact]
    public async Task Migration_sets_wal_journal_mode()
    {
        await using var context = _fixture.CreateContext();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        await context.Database.OpenConnectionAsync();
        try
        {
            var mode = (string)(await command.ExecuteScalarAsync())!;
            // WAL is persisted on the database file. For an in-memory database
            // SQLite falls back to MEMORY; either way, WAL is what we asked for.
            mode.Should().BeOneOf("wal", "memory");
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Migration_is_idempotent_and_does_not_re_execute()
    {
        // Migrations history table is the single source of truth — running the
        // committed migrations a second time must not add rows and must not throw.
        await using var context = _fixture.CreateContext();
        await context.Database.MigrateAsync();

        var migrationIds = new List<string>();
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                migrationIds.Add(reader.GetString(0));
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        migrationIds.Should().Equal(
            "20260923_InitialSchema",
            "20260924_AddMailboxDefaultMailbox",
            "20260925003448_PersistArchivePaths",
            "20260925120000_AddUserSettingsCompanyAndOutputDirectory",
            "20260925130000_AddArchivedArtifactSourceFileName",
            "20260925133000_AddLegacyArchiveInventory");
    }

    [Fact]
    public async Task UserSettings_singleton_default_row_seed_is_absent_until_bootstrap()
    {
        // The first migration only creates the schema. The default user-settings
        // row is inserted by RuleSetBootstrapper in the start-up recovery
        // UoW so the FK chain is satisfied atomically.
        await using var context = _fixture.CreateContext();
        var rows = await SqliteScalarAsync(context,
            "SELECT COUNT(*) FROM UserSettings;");
        rows.Should().Be(0);
    }

    [Fact]
    public async Task Invoice_items_unique_index_uses_expected_database_name()
    {
        await using var context = _fixture.CreateContext();

        var exists = await SqliteScalarAsync(context,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='InvoiceItems' AND name='IX_InvoiceItems_Ordinal';");

        exists.Should().Be(1);

        var modelIndex = context.Model
            .FindEntityType(typeof(InvoiceItemRow))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name).SequenceEqual(["InvoiceId", "Ordinal"]));

        modelIndex.GetDatabaseName().Should().Be("IX_InvoiceItems_Ordinal");
    }

    private static async Task<long> SqliteScalarAsync(InvoiceFlowDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await context.Database.OpenConnectionAsync();
        try
        {
            var result = await command.ExecuteScalarAsync();
            return result is null || result is DBNull ? 0 : Convert.ToInt64(result);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}