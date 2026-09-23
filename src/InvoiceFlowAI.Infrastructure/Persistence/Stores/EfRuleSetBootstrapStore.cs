// EF Core implementation of the RuleSet bootstrapper port. Persists the
// built-in default RuleSet + the singleton UserSettings row inside the
// same UoW so the FK chain is always satisfied.

using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Rules;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfRuleSetBootstrapStore : IRuleSetBootstrapStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfRuleSetBootstrapStore(InvoiceFlowDbContext context) => _context = context;

    public async Task<RuleSetDocument?> FindCurrentAsync(string ruleSetId, CancellationToken cancellationToken)
    {
        var row = await _context.RuleSets
            .Where(r => r.RuleSetId == ruleSetId && r.IsCurrent)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null) return null;

        // The bootstrapper works with the published contract — it does not
        // need NormalizedAstJson. SourceJson is the persisted payload.
        return JsonToDocument(row.SourceJson);
    }

    public async Task SaveAsync(RuleSetDocument document, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sourceJson = DocumentToJson(document);
        var sourceFingerprint = Sha256Hex(sourceJson);

        // Compute the AST fingerprint via the canonical serializer so the
        // bootstrapper can later detect tampering.
        var fingerprintService = new InvoiceFlowAI.Application.Configuration.ConfigurationFingerprintService();
        var astFingerprint = fingerprintService.ComputeFromObject(document);

        // Mark any prior IsCurrent row as superseded.
        var existingCurrent = await _context.RuleSets
            .Where(r => r.RuleSetId == document.RuleSetId && r.IsCurrent)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in existingCurrent) row.IsCurrent = false;

        _context.RuleSets.Add(new RuleSetRow
        {
            RuleSetId = document.RuleSetId,
            Version = 1, // bootstrapper is hard-wired to version 1
            SchemaVersion = document.SchemaVersion,
            SourceJson = sourceJson,
            SourceFingerprint = sourceFingerprint,
            AstFingerprint = astFingerprint,
            IsCurrent = true,
            CreatedBy = "bootstrap",
            CreatedAtUtc = now,
        });
        await Task.CompletedTask;
    }

    public async Task UpdateUserSettingsRuleSetAsync(string ruleSetId, int ruleSetVersion, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = await _context.UserSettings
            .FirstOrDefaultAsync(s => s.SettingsId == "default", cancellationToken)
            .ConfigureAwait(false);

        if (settings is null)
        {
            _context.UserSettings.Add(new UserSettingsRow
            {
                SettingsId = "default",
                Revision = 1,
                RuleSetId = ruleSetId,
                RuleSetVersion = ruleSetVersion,
                DefaultMailbox = "INBOX",
                AllowVisionFallback = true,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            settings.RuleSetId = ruleSetId;
            settings.RuleSetVersion = ruleSetVersion;
            settings.Revision++;
            settings.UpdatedAtUtc = now;
        }
        await Task.CompletedTask;
    }

    private static string DocumentToJson(RuleSetDocument document) =>
        System.Text.Json.JsonSerializer.Serialize(document);

    private static RuleSetDocument JsonToDocument(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<RuleSetDocument>(json) ?? throw new InvalidOperationException("Stored RuleSetDocument was empty.");

    private static string Sha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}