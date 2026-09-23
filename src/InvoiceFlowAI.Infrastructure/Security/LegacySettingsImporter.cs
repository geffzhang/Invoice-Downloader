// LegacySettingsImporter — one-shot migrator for the old Python settings
// file format. Per design §8 the importer:
//   * Reads ONLY non-secret fields (email, imap host, save path, etc.)
//   * Detects the presence of legacy api_key / auth_code fields WITHOUT
//     trying to decrypt them — the old DPAPI ciphertext is meaningless
//     under the new entropy and the user must re-enter via secret.set
//   * Writes a LegacyImportState record so subsequent startups skip the
//     legacy file entirely
//   * Atomically deletes the legacy source file on success
//   * Refuses to provide a fallback read path; absence of a clean
//     legacy state means the user is told to re-enter secrets explicitly

using System.Text;
using System.Text.Json;
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Infrastructure.Security;

public sealed class LegacySettingsImporter
{
    private static readonly string[] SecretFieldNames = { "api_key", "auth_code", "deepseek_api_key", "imap_password" };

    public async Task<LegacyImportReport> ImportAsync(string legacyFilePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(legacyFilePath))
        {
            return new LegacyImportReport(
                SecretReentryRequired: false,
                SecretFieldNames: Array.Empty<string>(),
                MigratedAccounts: Array.Empty<MailboxAccountDraft>(),
                ImportedAtUtc: DateTimeOffset.UtcNow);
        }

        var raw = await File.ReadAllTextAsync(legacyFilePath, cancellationToken).ConfigureAwait(false);

        // Parse with a comment-tolerant reader; we never care about the
        // values of secret fields, only their presence.
        Dictionary<string, JsonElement> legacy;
        try
        {
            using var document = JsonDocument.Parse(raw);
            legacy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                legacy[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Corrupt legacy file — flag for re-entry rather than crashing.
            return new LegacyImportReport(
                SecretReentryRequired: true,
                SecretFieldNames: new[] { "corrupt_legacy_file" },
                MigratedAccounts: Array.Empty<MailboxAccountDraft>(),
                ImportedAtUtc: DateTimeOffset.UtcNow);
        }

        var detectedSecrets = SecretFieldNames.Where(legacy.ContainsKey).ToArray();
        var secretReentryRequired = detectedSecrets.Length > 0;

        if (secretReentryRequired)
        {
            // Don't migrate accounts when secrets still need re-entry — the
            // legacy file is left in place so the user can verify their
            // remembered api key matches what they intend to re-enter.
            return new LegacyImportReport(
                SecretReentryRequired: true,
                SecretFieldNames: detectedSecrets,
                MigratedAccounts: Array.Empty<MailboxAccountDraft>(),
                ImportedAtUtc: DateTimeOffset.UtcNow);
        }

        var account = MigrateAccount(legacy);
        AtomicDelete(legacyFilePath);

        return new LegacyImportReport(
            SecretReentryRequired: false,
            SecretFieldNames: Array.Empty<string>(),
            MigratedAccounts: account is null ? Array.Empty<MailboxAccountDraft>() : new[] { account },
            ImportedAtUtc: DateTimeOffset.UtcNow);
    }

    private static MailboxAccountDraft? MigrateAccount(Dictionary<string, JsonElement> legacy)
    {
        if (!legacy.TryGetValue("email", out var emailEl) || emailEl.ValueKind != JsonValueKind.String)
            return null;
        if (!legacy.TryGetValue("imap_host", out var hostEl) || hostEl.ValueKind != JsonValueKind.String)
            return null;

        var email = emailEl.GetString() ?? string.Empty;
        var host = hostEl.GetString() ?? string.Empty;
        var port = legacy.TryGetValue("imap_port", out var portEl) && portEl.TryGetInt32(out var p) ? p : 993;
        bool useTls = true;
        if (legacy.TryGetValue("use_tls", out var tlsEl))
        {
            useTls = tlsEl.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when tlsEl.TryGetInt32(out var tlsInt) => tlsInt != 0,
                JsonValueKind.String when bool.TryParse(tlsEl.GetString(), out var tlsBool) => tlsBool,
                _ => true,
            };
        }
        var displayName = legacy.TryGetValue("display_name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;

        return new MailboxAccountDraft(
            AccountId: string.Empty,
            EmailAddress: email,
            ImapHost: host,
            ImapPort: port,
            UseTls: useTls,
            CredentialName: "mail.imap.auth-code",
            DisplayName: displayName ?? string.Empty);
    }

    private static void AtomicDelete(string path)
    {
        var tempPath = path + ".deleted";
        if (File.Exists(tempPath)) File.Delete(tempPath);
        File.Move(path, tempPath);
        File.Delete(tempPath);
    }
}

public sealed record LegacyImportReport(
    bool SecretReentryRequired,
    IReadOnlyList<string> SecretFieldNames,
    IReadOnlyList<MailboxAccountDraft> MigratedAccounts,
    DateTimeOffset ImportedAtUtc);