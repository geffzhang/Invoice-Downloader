using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Serialization;
using InvoiceFlowAI.Contracts.Settings;
using Xunit;

namespace InvoiceFlowAI.Contracts.Tests;

public sealed class SettingsRpcContractTests
{
    [Fact]
    public void User_settings_snapshot_round_trips_company_and_last_output_directory()
    {
        var snapshot = NewSnapshot() with
        {
            CompanyName = "Example Buyer",
            LastOutputDirectory = "C:/Invoices",
        };

        var json = JsonSerializer.Serialize(snapshot, InvoiceJsonOptions.Strict);
        var actual = JsonSerializer.Deserialize<UserSettingsSnapshot>(json, InvoiceJsonOptions.Strict);

        actual.Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public void Legacy_user_settings_snapshot_defaults_new_fields()
    {
        const string json = """
            {
              "revision": 0,
              "currentAccountId": null,
              "defaultMailbox": "INBOX",
              "mailboxFilters": {
                "includeReadMessages": false,
                "minAttachmentBytes": null,
                "maxAttachmentBytes": null
              },
              "pipeline": {},
              "allowVisionFallback": false,
              "ruleSetId": "default",
              "ruleSetVersion": 1,
              "configurationFingerprint": "legacy",
              "updatedAtUtc": "1970-01-01T00:00:00Z"
            }
            """;

        var actual = JsonSerializer.Deserialize<UserSettingsSnapshot>(json, InvoiceJsonOptions.Strict);

        actual.Should().NotBeNull();
        actual!.CompanyName.Should().BeEmpty();
        actual.LastOutputDirectory.Should().BeNull();
    }

    [Fact]
    public void Settings_update_request_round_trips_company_and_output_directory_patch()
    {
        var request = new SettingsUpdateRequest(
            ExpectedRevision: 3,
            CompanyName: "Example Buyer",
            LastOutputDirectory: "C:/Invoices");

        var json = JsonSerializer.Serialize(request, InvoiceJsonOptions.Strict);
        var actual = JsonSerializer.Deserialize<SettingsUpdateRequest>(json, InvoiceJsonOptions.Strict);

        actual.Should().BeEquivalentTo(request);
    }

    [Theory]
    [InlineData(SecretRetention.Persistent, "persistent")]
    [InlineData(SecretRetention.Session, "session")]
    public void Secret_set_request_uses_closed_string_retention_values(SecretRetention retention, string expected)
    {
        var request = new SecretSetRequest("mail.imap.auth-code", "test-only-secret", retention);

        var json = JsonSerializer.Serialize(request, InvoiceJsonOptions.Strict);
        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("retention").GetString().Should().Be(expected);
        JsonSerializer.Deserialize<SecretSetRequest>(json, InvoiceJsonOptions.Strict).Should().Be(request);
    }

    [Fact]
    public void Secret_mutation_result_contains_no_secret_value()
    {
        var result = new SecretMutationResult("glm.api-key", Configured: true, Persistent: false);

        var json = JsonSerializer.Serialize(result, InvoiceJsonOptions.Strict);

        json.Should().Contain("\"configured\":true");
        json.Should().Contain("\"persistent\":false");
        json.Should().NotContain("test-only-secret");
        json.Should().NotContain("value");
    }

    [Fact]
    public void Provider_test_request_contains_only_credential_reference()
    {
        var request = new ProviderTestRequest("deepseek", "glm.api-key");

        var json = JsonSerializer.Serialize(request, InvoiceJsonOptions.Strict);

        json.Should().Contain("\"credentialName\":\"glm.api-key\"");
        json.Should().NotContain("value");
        json.Should().NotContain("test-only-secret");
    }

    [Fact]
    public void Account_list_result_and_directory_result_round_trip_without_secrets()
    {
        var accounts = new AccountListResult([NewAccount()]);
        var selectedDirectory = new DirectoryChooseResult(false, "C:/Invoices");
        var cancelledDirectory = new DirectoryChooseResult(true, null);

        var accountsJson = JsonSerializer.Serialize(accounts, InvoiceJsonOptions.Strict);
        var selectedJson = JsonSerializer.Serialize(selectedDirectory, InvoiceJsonOptions.Strict);
        var cancelledJson = JsonSerializer.Serialize(cancelledDirectory, InvoiceJsonOptions.Strict);

        JsonSerializer.Deserialize<AccountListResult>(accountsJson, InvoiceJsonOptions.Strict)
            .Should().BeEquivalentTo(accounts);
        JsonSerializer.Deserialize<DirectoryChooseResult>(selectedJson, InvoiceJsonOptions.Strict)
            .Should().BeEquivalentTo(selectedDirectory);
        JsonSerializer.Deserialize<DirectoryChooseResult>(cancelledJson, InvoiceJsonOptions.Strict)
            .Should().BeEquivalentTo(cancelledDirectory);
        accountsJson.Should().NotContain("test-only-secret");
        accountsJson.Should().NotContain("authCode");
        accountsJson.Should().NotContain("apiKey");
    }

    [Fact]
    public void Unknown_secret_retention_value_is_rejected()
    {
        const string json = "{\"name\":\"glm.api-key\",\"value\":\"x\",\"retention\":\"forever\"}";

        var act = () => JsonSerializer.Deserialize<SecretSetRequest>(json, InvoiceJsonOptions.Strict);

        act.Should().Throw<JsonException>();
    }

    private static UserSettingsSnapshot NewSnapshot() => new(
        Revision: 1,
        CurrentAccountId: "account-1",
        DefaultMailbox: "INBOX",
        MailboxFilters: new MailboxFilterRules(false, null, null),
        Pipeline: new PipelineOptionsPatch(),
        AllowVisionFallback: false,
        RuleSetId: "default",
        RuleSetVersion: 1,
        ConfigurationFingerprint: "safe-fingerprint",
        UpdatedAtUtc: DateTimeOffset.UnixEpoch,
        CompanyName: "",
        LastOutputDirectory: null);

    private static MailboxAccountSnapshot NewAccount() => new(
        "account-1",
        "user@example.com",
        "imap.example.com",
        993,
        true,
        "mail.imap.auth-code",
        "User",
        1,
        true,
        "u***@example.com",
        DateTimeOffset.UnixEpoch);
}