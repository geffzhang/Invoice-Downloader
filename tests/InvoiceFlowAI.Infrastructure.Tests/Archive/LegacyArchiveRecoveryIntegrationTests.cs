using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Tests.Persistence;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class LegacyArchiveRecoveryIntegrationTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public LegacyArchiveRecoveryIntegrationTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Startup_reconciliation_scans_each_registered_output_root_once()
    {
        await _fixture.ResetAsync();
        var firstRoot = CreateRoot();
        var secondRoot = CreateRoot();
        const string firstReviewRelative = "archive/prior-run/review/hotel-a.pdf";
        const string secondOriginalRelative = "住宿发票/hotel-b.pdf";
        const string firstContent = "first root review bytes";
        const string secondContent = "second root original bytes";
        var firstReviewPath = Path.Combine(firstRoot, firstReviewRelative.Replace('/', Path.DirectorySeparatorChar));
        var secondOriginalPath = Path.Combine(secondRoot, secondOriginalRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(firstReviewPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondOriginalPath)!);
        await File.WriteAllTextAsync(firstReviewPath, firstContent);
        await File.WriteAllTextAsync(secondOriginalPath, secondContent);
        try
        {
            await using var context = _fixture.CreateContext();
            context.Runs.AddRange(
                NewRun("root-a-run-1", firstRoot),
                NewRun("root-a-run-2", firstRoot + Path.DirectorySeparatorChar),
                NewRun("root-b-run", secondRoot),
                NewRun("root-no-output", null),
                NewRun("root-invalid", "bad\0root"));
            await context.SaveChangesAsync(CancellationToken.None);
            var legacyStore = new EfLegacyArchiveInventoryStore(context);
            var firstInventoryId = await SeedRecoveryRowAsync(
                legacyStore, context, firstRoot, "住宿发票/hotel-a.pdf", firstReviewRelative, firstContent);
            var secondInventoryId = await SeedRecoveryRowAsync(
                legacyStore, context, secondRoot, secondOriginalRelative, "archive/prior-run/review/hotel-b.pdf", secondContent);
            var fileSystem = new PhysicalArchiveFileSystem();
            var recovery = new ArchiveRecoveryService(
                new EfUnitOfWorkFactory(context),
                new EfArchiveArtifactStore(context),
                fileSystem,
                new EfAuditStore(context),
                new EfPairingStore(context),
                legacyStore,
                new EfRunLifecycleStore(context));

            var result = await recovery.ReconcileAllKnownRootsAsync(CancellationToken.None);

            result.SkippedRootCount.Should().Be(1);
            result.LegacyItems.Should().HaveCount(2);
            result.LegacyItems.Should().ContainSingle(item => item.InventoryId == firstInventoryId
                && item.ResolvedState == LegacyArchiveInventoryState.Review);
            result.LegacyItems.Should().ContainSingle(item => item.InventoryId == secondInventoryId
                && item.ResolvedState == LegacyArchiveInventoryState.Discovered);
            result.Artifacts.Should().BeEmpty();
            File.Exists(firstReviewPath).Should().BeTrue();
            File.Exists(secondOriginalPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(firstRoot)) Directory.Delete(firstRoot, recursive: true);
            if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Pre_run_reconciliation_resolves_legacy_review_target_without_moving_or_deleting()
    {
        await _fixture.ResetAsync();
        var root = CreateRoot();
        const string relativeTarget = "archive/run-1/review/hotel.pdf";
        const string content = "verified review bytes";
        var target = Path.Combine(root, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, content);
        try
        {
            await using var context = _fixture.CreateContext();
            var legacyStore = new EfLegacyArchiveInventoryStore(context);
            var inventory = await SeedRecoveryRowAsync(legacyStore, context, root, "住宿发票/hotel.pdf", relativeTarget, content);
            var recovery = CreateRecovery(context, legacyStore);

            var result = await recovery.ReconcileBeforeRunAsync(root, CancellationToken.None);

            result.LegacyItems.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                InventoryId = inventory,
                ResolvedState = LegacyArchiveInventoryState.Review,
                ReasonCode = (string?)null,
            });
            File.Exists(target).Should().BeTrue();
            var row = await context.LegacyArchiveInventory.FindAsync(inventory);
            row!.CurrentRelativePath.Should().Be(relativeTarget);
            row.State.Should().Be(nameof(LegacyArchiveInventoryState.Review));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_review_target_with_matching_original_resets_item_to_discovered()
    {
        await _fixture.ResetAsync();
        var root = CreateRoot();
        const string originalRelativePath = "住宿发票/hotel.pdf";
        const string targetRelativePath = "archive/run-1/review/hotel.pdf";
        const string content = "restored source bytes";
        var original = Path.Combine(root, originalRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        await File.WriteAllTextAsync(original, content);
        try
        {
            await using var context = _fixture.CreateContext();
            var legacyStore = new EfLegacyArchiveInventoryStore(context);
            var inventory = await SeedRecoveryRowAsync(legacyStore, context, root, originalRelativePath, targetRelativePath, content);

            var decisions = await CreateRecovery(context, legacyStore).ReconcileLegacyAsync(root, CancellationToken.None);

            decisions.Should().ContainSingle().Which.ResolvedState.Should().Be(LegacyArchiveInventoryState.Discovered);
            var row = await context.LegacyArchiveInventory.FindAsync(inventory);
            row!.CurrentRelativePath.Should().Be(originalRelativePath);
            row.State.Should().Be(nameof(LegacyArchiveInventoryState.Discovered));
            File.Exists(original).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Hash_mismatch_keeps_legacy_item_in_recovery_required_and_preserves_file()
    {
        await _fixture.ResetAsync();
        var root = CreateRoot();
        const string relativeTarget = "archive/run-1/review/hotel.pdf";
        var target = Path.Combine(root, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "tampered bytes");
        try
        {
            await using var context = _fixture.CreateContext();
            var legacyStore = new EfLegacyArchiveInventoryStore(context);
            var inventory = await SeedRecoveryRowAsync(legacyStore, context, root, "住宿发票/hotel.pdf", relativeTarget, "expected bytes");

            var decisions = await CreateRecovery(context, legacyStore).ReconcileLegacyAsync(root, CancellationToken.None);

            decisions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
            {
                InventoryId = inventory,
                ResolvedState = LegacyArchiveInventoryState.RecoveryRequired,
                ReasonCode = "LEGACY_ARCHIVE_HASH_MISMATCH",
            });
            File.Exists(target).Should().BeTrue();
            (await File.ReadAllTextAsync(target)).Should().Be("tampered bytes");
            var row = await context.LegacyArchiveInventory.FindAsync(inventory);
            row!.State.Should().Be(nameof(LegacyArchiveInventoryState.RecoveryRequired));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ArchiveRecoveryService CreateRecovery(
        InvoiceFlowDbContext context,
        EfLegacyArchiveInventoryStore legacyStore)
    {
        var fileSystem = new PhysicalArchiveFileSystem();
        return new ArchiveRecoveryService(
            new EfUnitOfWorkFactory(context),
            new EfArchiveArtifactStore(context),
            fileSystem,
            new EfAuditStore(context),
            new EfPairingStore(context),
            legacyStore);
    }

    private static async Task<string> SeedRecoveryRowAsync(
        EfLegacyArchiveInventoryStore store,
        InvoiceFlowDbContext context,
        string root,
        string originalRelativePath,
        string currentRelativePath,
        string expectedContent)
    {
        var rootKeyPath = OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root);
        var rootKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootKeyPath))).ToLowerInvariant();
        var hash = Hash(expectedContent);
        var uowFactory = new EfUnitOfWorkFactory(context);
        LegacyArchiveInventorySnapshot item;
        await using (var discovery = await uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, CancellationToken.None))
        {
            item = await store.DiscoverAsync(
                rootKey, originalRelativePath, Path.GetFileName(originalRelativePath), hash, discovery, CancellationToken.None);
            await discovery.CommitAsync(CancellationToken.None);
        }
        await using (var recovery = await uowFactory.BeginAsync(TransactionPurpose.ArchiveCommit, CancellationToken.None))
        {
            await store.UpdateLocationAsync(
                item.InventoryId, currentRelativePath, LegacyArchiveInventoryState.RecoveryRequired, "run-1",
                recovery, CancellationToken.None);
            await recovery.CommitAsync(CancellationToken.None);
        }
        return item.InventoryId;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"invoice-flow-legacy-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static RunRow NewRun(string runId, string? outputRoot) => new()
    {
        RunId = runId,
        State = "Completed",
        Stage = "complete",
        DateFrom = new DateOnly(2026, 9, 1),
        DateToExclusive = new DateOnly(2026, 10, 1),
        OutputRoot = outputRoot,
        StartedAtUtc = DateTimeOffset.UtcNow,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };
}