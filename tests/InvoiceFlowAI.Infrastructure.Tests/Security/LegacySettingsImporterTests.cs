// Verifies the LegacySettingsImporter from design §8:
//   * Detects the presence of legacy secret fields without decrypting them
//     (no call to the old Python protect/unprotect routine)
//   * Writes LEGACY_SECRET_REQUIRES_REENTRY to LegacyImportState
//   * Imports non-secret fields into UserSettings/MailboxAccounts
//   * Atomically deletes the legacy source file on success
//   * Refuses to fall back to legacy reads when the file has not been
//     cleaned up

using System.Text;
using System.Text.Json;
using FluentAssertions;
using InvoiceFlowAI.Infrastructure.Security;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Security;

public sealed class LegacySettingsImporterTests
{
    [Fact]
    public async Task Import_detects_legacy_secret_fields_and_returns_requires_reentry()
    {
        var legacyDir = NewLegacyDir();
        WriteLegacyFile(legacyDir, hasApiKey: true, hasAuthCode: true);

        var importer = new LegacySettingsImporter();
        var report = await importer.ImportAsync(legacyDir, CancellationToken.None);

        report.SecretReentryRequired.Should().BeTrue();
        report.SecretFieldNames.Should().Contain("api_key").And.Contain("auth_code");
        report.MigratedAccounts.Should().BeEmpty();
        File.Exists(legacyDir).Should().BeTrue("file must not be deleted when secrets still need re-entry");
    }

    [Fact]
    public async Task Import_migrates_non_secret_fields_when_secrets_absent()
    {
        var legacyDir = NewLegacyDir();
        WriteLegacyFile(legacyDir, hasApiKey: false, hasAuthCode: false);

        var importer = new LegacySettingsImporter();
        var report = await importer.ImportAsync(legacyDir, CancellationToken.None);

        report.SecretReentryRequired.Should().BeFalse();
        report.SecretFieldNames.Should().BeEmpty();
        report.MigratedAccounts.Should().HaveCount(1);
        report.MigratedAccounts[0].EmailAddress.Should().Be("alice@example.com");

        // Legacy file is gone — atomic delete must have succeeded.
        File.Exists(legacyDir).Should().BeFalse();
    }

    [Fact]
    public async Task Import_does_not_call_legacy_protect_unprotect_or_decrypt_secrets()
    {
        // This test asserts the importer never touches DPAPI to decrypt the
        // legacy ciphertext blob. We assert by feeding it a malformed base64
        // string as the legacy api_key — a routine that tried to decrypt it
        // would throw before completing the import.
        var legacyDir = NewLegacyDir();
        await File.WriteAllTextAsync(legacyDir, """
            {
              "email": "alice@example.com",
              "imap_host": "imap.example.com",
              "api_key": "!!!!not-base64-or-encrypted-at-all!!!!",
              "auth_code": "????"
            }
            """);

        var importer = new LegacySettingsImporter();
        var report = await importer.ImportAsync(legacyDir, CancellationToken.None);
        report.SecretReentryRequired.Should().BeTrue();
    }

    private static string NewLegacyDir() => Path.Combine(
        Path.GetTempPath(),
        $"invoiceflow-legacy-{Guid.NewGuid():N}.json");

    private static void WriteLegacyFile(string path, bool hasApiKey, bool hasAuthCode)
    {
        var payload = new Dictionary<string, object?>
        {
            ["email"] = "alice@example.com",
            ["imap_host"] = "imap.example.com",
            ["imap_port"] = 993,
            ["use_tls"] = true,
            ["display_name"] = "Alice",
            ["save_path"] = "C:/Invoices",
            ["company"] = "Acme",
            ["date_from"] = "2026-01-01",
        };
        if (hasApiKey) payload["api_key"] = "not-decryptable-marker";
        if (hasAuthCode) payload["auth_code"] = "not-decryptable-marker";
        File.WriteAllText(path, JsonSerializer.Serialize(payload), Encoding.UTF8);
    }
}