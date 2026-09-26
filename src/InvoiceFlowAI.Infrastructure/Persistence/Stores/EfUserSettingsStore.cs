using System.Text.Json;
using InvoiceFlowAI.Application.Configuration;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Contracts.Settings;
using InvoiceFlowAI.Contracts.Serialization;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfUserSettingsStore : IUserSettingsStore
{
    private const string SettingsId = "default";
    private const string SnapshotImportId = "settings-snapshot-v1";
    private static readonly ConfigurationFingerprintService Fingerprints = new();

    private readonly InvoiceFlowDbContext _context;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;

    public EfUserSettingsStore(InvoiceFlowDbContext context, IUnitOfWorkFactory unitOfWorkFactory)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    }

    public async Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var row = await _context.UserSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.SettingsId == SettingsId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? CreateDefaultSnapshot() : ToSnapshot(row);
    }

    public async Task<UserSettingsSnapshot> UpdateAsync(
        SettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrEmpty(request.CustomRuleSetJson))
        {
            throw new ArgumentException("Custom rules are managed by the ruleset API.", nameof(request));
        }

        await using var unitOfWork = await _unitOfWorkFactory
            .BeginAsync(TransactionPurpose.SettingsUpdate, cancellationToken)
            .ConfigureAwait(false);

        var row = await _context.UserSettings
            .SingleOrDefaultAsync(settings => settings.SettingsId == SettingsId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("User settings have not been bootstrapped.");

        if (request.ExpectedRevision != row.Revision)
        {
            throw new SettingsRevisionConflictException(request.ExpectedRevision, row.Revision);
        }

        ApplyPatch(row, request);
        row.Revision++;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.ConfigurationFingerprint = ComputeFingerprint(row);

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(row);
    }

    public async Task ImportSnapshotAsync(
        UserSettingsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var unitOfWork = await _unitOfWorkFactory
            .BeginAsync(TransactionPurpose.SettingsUpdate, cancellationToken)
            .ConfigureAwait(false);

        var existingImport = await _context.LegacyImportState
            .FindAsync([SnapshotImportId], cancellationToken)
            .ConfigureAwait(false);
        if (existingImport is not null)
        {
            await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var row = await _context.UserSettings
            .SingleOrDefaultAsync(settings => settings.SettingsId == SettingsId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException("User settings have not been bootstrapped.");
        }

        if (row.Revision <= 1)
        {
            row.CurrentAccountId = snapshot.CurrentAccountId;
            row.DefaultMailbox = snapshot.DefaultMailbox;
            row.CompanyName = snapshot.CompanyName;
            row.LastOutputDirectory = NormalizeOptionalPath(snapshot.LastOutputDirectory);
            row.MailboxFiltersJson = JsonSerializer.Serialize(snapshot.MailboxFilters, InvoiceJsonOptions.Strict);
            row.PipelineOptionsJson = JsonSerializer.Serialize(snapshot.Pipeline, InvoiceJsonOptions.Strict);
            row.AllowVisionFallback = snapshot.AllowVisionFallback;
            row.RuleSetId = snapshot.RuleSetId;
            row.RuleSetVersion = snapshot.RuleSetVersion;
            row.Revision = Math.Max(row.Revision, snapshot.Revision);
            row.ConfigurationFingerprint = ComputeFingerprint(row);
            row.UpdatedAtUtc = snapshot.UpdatedAtUtc;
        }

        _context.LegacyImportState.Add(new LegacyImportStateRow
        {
            ImportId = SnapshotImportId,
            ImportVersion = 1,
            SourceFingerprint = snapshot.ConfigurationFingerprint,
            ImportedAtUtc = DateTimeOffset.UtcNow,
            SecretReentryRequired = false,
        });
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyPatch(UserSettingsRow row, SettingsUpdateRequest request)
    {
        if (request.AccountId is not null) row.CurrentAccountId = NullIfEmpty(request.AccountId);
        if (request.Mailbox is not null) row.DefaultMailbox = request.Mailbox;
        if (request.CompanyName is not null) row.CompanyName = request.CompanyName;
        if (request.LastOutputDirectory is not null)
            row.LastOutputDirectory = NormalizeOptionalPath(request.LastOutputDirectory);
        if (request.MailboxFilters is not null)
            row.MailboxFiltersJson = JsonSerializer.Serialize(request.MailboxFilters, InvoiceJsonOptions.Strict);
        if (request.Pipeline is not null)
            row.PipelineOptionsJson = JsonSerializer.Serialize(request.Pipeline, InvoiceJsonOptions.Strict);
        if (request.AllowVisionFallback is not null) row.AllowVisionFallback = request.AllowVisionFallback.Value;
    }

    private static UserSettingsSnapshot ToSnapshot(UserSettingsRow row)
    {
        var filters = DeserializeOrDefault(row.MailboxFiltersJson, new MailboxFilterRules(false, null, null));
        var pipeline = DeserializeOrDefault(row.PipelineOptionsJson, new PipelineOptionsPatch());
        return new UserSettingsSnapshot(
            row.Revision,
            row.CurrentAccountId,
            row.DefaultMailbox,
            filters,
            pipeline,
            row.AllowVisionFallback,
            row.RuleSetId,
            row.RuleSetVersion,
            ComputeFingerprint(row),
            row.UpdatedAtUtc,
            row.CompanyName,
            row.LastOutputDirectory);
    }

    private static T DeserializeOrDefault<T>(string? json, T fallback) =>
        string.IsNullOrWhiteSpace(json)
            ? fallback
            : JsonSerializer.Deserialize<T>(json, InvoiceJsonOptions.Strict) ?? fallback;

    private static string ComputeFingerprint(UserSettingsRow row)
    {
        var filters = DeserializeOrDefault(row.MailboxFiltersJson, new MailboxFilterRules(false, null, null));
        var pipeline = DeserializeOrDefault(row.PipelineOptionsJson, new PipelineOptionsPatch());
        return Fingerprints.ComputeFromObject(new
        {
            row.CurrentAccountId,
            row.DefaultMailbox,
            row.CompanyName,
            MailboxFilters = filters,
            Pipeline = pipeline,
            row.AllowVisionFallback,
            row.RuleSetId,
            row.RuleSetVersion,
        });
    }

    private static UserSettingsSnapshot CreateDefaultSnapshot()
    {
        var row = new UserSettingsRow
        {
            SettingsId = SettingsId,
            Revision = 0,
            DefaultMailbox = "INBOX",
            CompanyName = string.Empty,
            RuleSetId = "default",
            RuleSetVersion = 1,
            UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        };
        return ToSnapshot(row);
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}