// Verifies the UserSettings.RuleSetId/RuleSetVersion foreign key from
// design §8: UserSettings.RuleSetId/Version must reference an existing
// RuleSets row, otherwise the bootstrapper must fail (no silent memory
// fallback per the global constraints).

using FluentAssertions;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Rules;
using InvoiceFlowAI.Contracts.Errors;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class UserSettingsForeignKeyTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public UserSettingsForeignKeyTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Bootstrap_inserts_default_RuleSet_and_UserSettings_with_FK()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();

        // Inject an in-memory bootstrap store backed by the same DbContext so
        // the bootstrapper can persist directly to SQLite.
        var store = new EfRuleSetBootstrapStore(context);
        var bootstrapper = new RuleSetBootstrapper(store, new RuleSetValidator());

        await using var uow = await BeginAsync(context);
        var result = await bootstrapper.EnsureDefaultInTransactionAsync(uow, CancellationToken.None)
            ;
        await uow.CommitAsync(CancellationToken.None);

        result.RuleSetId.Should().Be("default");
        result.Version.Should().Be(1);

        var rulesCount = await ReadScalarAsync(context, "SELECT COUNT(*) FROM RuleSets;");
        rulesCount.Should().BeGreaterOrEqualTo(1);

        var settingsCount = await ReadScalarAsync(context, "SELECT COUNT(*) FROM UserSettings;");
        settingsCount.Should().Be(1);
    }

    [Fact]
    public async Task Bootstrap_rejects_tampered_existing_default_with_RULESET_REVISION_CONFLICT()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        var store = new EfRuleSetBootstrapStore(context);
        var bootstrapper = new RuleSetBootstrapper(store, new RuleSetValidator());

        // Pre-populate with a tampered default RuleSet row whose fingerprint
        // will not match the built-in canonical fingerprint. The SourceJson
        // is a structurally valid RuleSetDocument (passes RuleSetValidator)
        // but its RuleId is altered so the canonical fingerprint differs.
        context.RuleSets.Add(new RuleSetRow
        {
            RuleSetId = "default",
            Version = 1,
            SchemaVersion = "1.0",
            SourceJson =
                "{\"SchemaVersion\":\"1.0\",\"RuleSetId\":\"default\",\"Rules\":[" +
                "{\"RuleId\":\"tampered-rule\",\"Priority\":100,\"Enabled\":true," +
                "\"When\":{\"DocumentType\":\"FlightInvoice\",\"SellerContains\":\"air\"}," +
                "\"Then\":{\"ArchiveFolder\":\"transport/flight\",\"Category\":\"transport\"," +
                "\"RequireManualReview\":false,\"AllowCrossMessagePairing\":true}}]}",
            SourceFingerprint = new string('0', 64),
            AstFingerprint = new string('0', 64),
            IsCurrent = true,
            CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        await using var uow = await BeginAsync(context);
        var act = () => bootstrapper.EnsureDefaultInTransactionAsync(uow, CancellationToken.None);
        await act.Should().ThrowAsync<RuleSetBootstrapException>()
            .Where(e => e.ReasonCode == RpcErrorCodes.RulesetRevisionConflict);
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":\"1.0\",\"RuleSetId\":\"default\",\"Rules\":[],\"Unexpected\":true}")]
    [InlineData("{\"SchemaVersion\":\"1.0\",\"RuleSetId\":\"default\",\"Rules\":[{\"RuleId\":\"r\",\"Priority\":1,\"Enabled\":true,\"When\":{\"DocumentType\":\"FlightInvoice\",\"Unexpected\":true},\"Then\":{\"ArchiveFolder\":\"transport/flight\",\"Category\":\"transport\",\"RequireManualReview\":false,\"AllowCrossMessagePairing\":true}}]}")]
    public async Task FindCurrent_rejects_unmapped_rule_set_json_properties(string sourceJson)
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.RuleSets.Add(new RuleSetRow
        {
            RuleSetId = "default",
            Version = 1,
            SchemaVersion = "1.0",
            SourceJson = sourceJson,
            SourceFingerprint = new string('0', 64),
            AstFingerprint = new string('0', 64),
            IsCurrent = true,
            CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var store = new EfRuleSetBootstrapStore(context);
        var act = () => store.FindCurrentAsync("default", CancellationToken.None);

        await act.Should().ThrowAsync<RuleSetValidationException>()
            .Where(exception => exception.ReasonCode == RpcErrorCodes.RulesetInvalid);
    }

    [Theory]
    [InlineData("""{"schemaVersion":"1.0","schemaVersion":"1.0","ruleSetId":"default","rules":[{"ruleId":"r","priority":1,"enabled":true,"when":{"documentType":"FlightInvoice"},"then":{"archiveFolder":"transport/flight","category":"travel","requireManualReview":false,"allowCrossMessagePairing":true}}]}""")]
    [InlineData("""{"schemaVersion":"1.0","ruleSetId":"default","rules":[{"ruleId":"r","priority":1,"enabled":true,"when":{"documentType":"FlightInvoice","DocumentType":"FlightInvoice"},"then":{"archiveFolder":"transport/flight","category":"travel","requireManualReview":false,"allowCrossMessagePairing":true}}]}""")]
    public async Task FindCurrent_rejects_duplicate_json_properties(string sourceJson)
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.RuleSets.Add(new RuleSetRow
        {
            RuleSetId = "default",
            Version = 1,
            SchemaVersion = "1.0",
            SourceJson = sourceJson,
            SourceFingerprint = new string('0', 64),
            AstFingerprint = new string('0', 64),
            IsCurrent = true,
            CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var store = new EfRuleSetBootstrapStore(context);
        var act = () => store.FindCurrentAsync("default", CancellationToken.None);

        await act.Should().ThrowAsync<RuleSetValidationException>()
            .Where(exception => exception.ReasonCode == RpcErrorCodes.RulesetInvalid);
    }

    [Fact]
    public async Task FindCurrent_preserves_all_declared_when_properties_in_camel_case()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.RuleSets.Add(new RuleSetRow
        {
            RuleSetId = "default",
            Version = 1,
            SchemaVersion = "1.0",
            SourceJson = """{"schemaVersion":"1.0","ruleSetId":"default","rules":[{"ruleId":"r","priority":1,"enabled":true,"when":{"providerFamily":"airline","documentType":"AirTicket","sellerContains":"Sky","purchaserRelation":"target","subjectContains":"invoice","invoiceNumberPrefix":"A-"},"then":{"archiveFolder":"transport/flight","category":"transport","requireManualReview":false,"allowCrossMessagePairing":true}}]}""",
            SourceFingerprint = new string('0', 64),
            AstFingerprint = new string('0', 64),
            IsCurrent = true,
            CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var store = new EfRuleSetBootstrapStore(context);
        var document = await store.FindCurrentAsync("default", CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(
            document,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().Contain("\"providerFamily\":\"airline\"")
            .And.Contain("\"purchaserRelation\":\"target\"")
            .And.Contain("\"subjectContains\":\"invoice\"")
            .And.Contain("\"invoiceNumberPrefix\":\"A-\"");
    }

    private static async Task<IUnitOfWork> BeginAsync(InvoiceFlowDbContext context)
    {
        var factory = new EfUnitOfWorkFactory(context);
        return await factory.BeginAsync(TransactionPurpose.Migration, CancellationToken.None);
    }

    private static async Task<long> ReadScalarAsync(InvoiceFlowDbContext context, string commandText)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result);
    }
}