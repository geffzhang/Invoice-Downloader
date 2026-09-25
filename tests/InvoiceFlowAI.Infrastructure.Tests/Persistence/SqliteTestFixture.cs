// Shared SQLite test fixture. Per design §11 the integration tests must
// verify EF Core behavior against the real SQLite provider — not the EF
// in-memory provider — so the migration, PRAGMAs, partial indexes, and
// append-only triggers are exercised exactly as in production.

using InvoiceFlowAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class SqliteTestFixture : IAsyncLifetime
{
    private SqliteConnection? _connection;

    public string ConnectionString { get; private set; } = string.Empty;

    public InvoiceFlowDbContext CreateContext()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("SqliteTestFixture not initialized.");
        }

        var options = new DbContextOptionsBuilder<InvoiceFlowDbContext>()
            .UseSqlite(_connection, sqlite => sqlite.MigrationsAssembly("InvoiceFlowAI.Infrastructure"))
            .Options;
        return new InvoiceFlowDbContext(options);
    }

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync().ConfigureAwait(false);

        // The connection lifetime is owned by the fixture; opening the
        // connection before UseSqlite(...) lets multiple DbContext instances
        // share the same in-memory database.
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        ConnectionString = _connection.ConnectionString;
    }

    /// <summary>
    /// Truncates every table the migration created so a test can rely on a
    /// clean slate. Tests share a fixture instance (one in-memory DB per
    /// class); each test calls ResetAsync() before arranging state. The
    /// append-only triggers on AuditEvents are temporarily dropped because
    /// reset is a test-only operation; production never deletes audit rows.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync().ConfigureAwait(false);
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DROP TRIGGER IF EXISTS AuditEvents_NoUpdate;
            DROP TRIGGER IF EXISTS AuditEvents_NoDelete;
            DELETE FROM ManualReviewItems;
            DELETE FROM RunEvents;
            DELETE FROM AuditEvents;
            DELETE FROM MailboxCursors;
            DELETE FROM RunCheckpoints;
            DELETE FROM ArchivedArtifacts;
            DELETE FROM Pairings;
            DELETE FROM InvoiceItems;
            DELETE FROM Invoices;
            DELETE FROM DocumentProcessing;
            DELETE FROM Documents;
            DELETE FROM Runs;
            DELETE FROM UserSettings;
            DELETE FROM MailboxAccounts;
            DELETE FROM RuleSets;
            DELETE FROM LegacyImportState;
            DELETE FROM LegacyArchiveInventory;
            DELETE FROM __EFMigrationsHistory;
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260923_InitialSchema', '10.0.12');
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260924_AddMailboxDefaultMailbox', '10.0.12');
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260925003448_PersistArchivePaths', '10.0.12');
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260925120000_AddUserSettingsCompanyAndOutputDirectory', '10.0.12');
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260925130000_AddArchivedArtifactSourceFileName', '10.0.12');
            INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                VALUES ('20260925133000_AddLegacyArchiveInventory', '10.0.12');
            CREATE TRIGGER AuditEvents_NoUpdate
                BEFORE UPDATE ON AuditEvents
                BEGIN
                    SELECT RAISE(ABORT, 'AuditEvents is append-only');
                END;
            CREATE TRIGGER AuditEvents_NoDelete
                BEFORE DELETE ON AuditEvents
                BEGIN
                    SELECT RAISE(ABORT, 'AuditEvents is append-only');
                END;";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}