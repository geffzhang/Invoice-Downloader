// EF Core DbContext for the InvoiceFlowAI application data store. Lives in
// Infrastructure so the Application layer never references EF types, yet
// the entity types are still internal to this assembly (each entity class
// is declared internal to the Persistence namespace so the type cannot leak
// outside the persistence boundary through EF Core change tracking).
//
// All tables, indexes, triggers and FK constraints from design §8 are
// declared either via fluent API configuration or in the first-place
// migration 20260923_InitialSchema. Trigger SQL lives in the migration so
// append-only AuditEvents is enforced even when EF tries to UPDATE/DELETE
// a tracked entity.

using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence;

public sealed class InvoiceFlowDbContext : DbContext
{
    public InvoiceFlowDbContext(DbContextOptions<InvoiceFlowDbContext> options) : base(options)
    {
    }

    public DbSet<UserSettingsRow> UserSettings => Set<UserSettingsRow>();
    public DbSet<RunRow> Runs => Set<RunRow>();
    public DbSet<DocumentSourceRow> Documents => Set<DocumentSourceRow>();
    public DbSet<DocumentProcessingRow> DocumentProcessing => Set<DocumentProcessingRow>();
    public DbSet<InvoiceRow> Invoices => Set<InvoiceRow>();
    public DbSet<InvoiceItemRow> InvoiceItems => Set<InvoiceItemRow>();
    public DbSet<PairingRow> Pairings => Set<PairingRow>();
    public DbSet<ArchivedArtifactRow> ArchivedArtifacts => Set<ArchivedArtifactRow>();
    public DbSet<RunCheckpointRow> RunCheckpoints => Set<RunCheckpointRow>();
    public DbSet<MailboxCursorRow> MailboxCursors => Set<MailboxCursorRow>();
    public DbSet<MailboxAccountRow> MailboxAccounts => Set<MailboxAccountRow>();
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<RunEventRow> RunEvents => Set<RunEventRow>();
    public DbSet<RuleSetRow> RuleSets => Set<RuleSetRow>();
    public DbSet<ManualReviewItemRow> ManualReviewItems => Set<ManualReviewItemRow>();
    public DbSet<LegacyImportStateRow> LegacyImportState => Set<LegacyImportStateRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("Relational:JsonTypeColumn", "TEXT");

        modelBuilder.Entity<UserSettingsRow>(b =>
        {
            b.ToTable("UserSettings");
            b.HasKey(x => x.SettingsId);
            b.Property(x => x.SettingsId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Revision).IsRequired();
            b.Property(x => x.DefaultMailbox).HasMaxLength(128).IsRequired();
            b.Property(x => x.RuleSetId).HasMaxLength(128).IsRequired();
            b.Property(x => x.RuleSetVersion).IsRequired();
            b.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            b.Property(x => x.UpdatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<RunRow>(b =>
        {
            b.ToTable("Runs");
            b.HasKey(x => x.RunId);
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasMaxLength(32).IsRequired();
            b.Property(x => x.Stage).HasMaxLength(64).IsRequired();
            b.Property(x => x.TerminalReasonCode).HasMaxLength(64);
            b.Property(x => x.AccountId).HasMaxLength(64);
            b.Property(x => x.Mailbox).HasMaxLength(128);
            b.Property(x => x.OutputRoot).HasMaxLength(512);
            b.Property(x => x.RuleSetId).HasMaxLength(64);
            b.Property(x => x.ConfigurationFingerprint).HasMaxLength(64);
            b.Property(x => x.RecipeVersion).HasMaxLength(32);
            b.Property(x => x.LastEventSequence).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => x.ConfigurationFingerprint);
            b.HasIndex(x => x.State);
            b.HasIndex(x => x.Stage);
        });

        modelBuilder.Entity<DocumentSourceRow>(b =>
        {
            b.ToTable("Documents");
            b.HasKey(x => x.DocumentId);
            b.Property(x => x.DocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SourceKind).HasMaxLength(32).IsRequired();
            b.Property(x => x.SourceMessageUid).HasMaxLength(128);
            b.Property(x => x.SourceFileName).HasMaxLength(256);
            b.Property(x => x.SourceLocator).HasMaxLength(512);
            b.Property(x => x.ProviderGroupKey).HasMaxLength(128);
            b.Property(x => x.ContentHash).HasMaxLength(64);
            b.Property(x => x.MimeType).HasMaxLength(128);
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => x.ContentHash).IsUnique(false);
        });

        modelBuilder.Entity<DocumentProcessingRow>(b =>
        {
            b.ToTable("DocumentProcessing");
            b.HasKey(x => new { x.DocumentId, x.ProcessingRevision });
            b.Property(x => x.DocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Stage).HasMaxLength(64).IsRequired();
            b.Property(x => x.Status).HasMaxLength(32).IsRequired();
            b.Property(x => x.ReasonCode).HasMaxLength(64);
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
        });

        modelBuilder.Entity<InvoiceRow>(b =>
        {
            b.ToTable("Invoices");
            b.HasKey(x => x.InvoiceId);
            b.Property(x => x.InvoiceId).HasMaxLength(64).IsRequired();
            b.Property(x => x.DocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Purchaser).HasMaxLength(256).IsRequired();
            b.Property(x => x.Seller).HasMaxLength(256).IsRequired();
            b.Property(x => x.DocumentType).HasMaxLength(64).IsRequired();
            b.Property(x => x.ArchiveState).HasMaxLength(32).IsRequired();
            b.Property(x => x.Revision).IsRequired();
            b.HasIndex(x => x.DuplicateKey);
            b.HasIndex(x => new { x.InvoiceNumber, x.Seller, x.InvoiceDate, x.TotalAmount });
        });

        modelBuilder.Entity<InvoiceItemRow>(b =>
        {
            b.ToTable("InvoiceItems");
            b.HasKey(x => x.InvoiceItemId);
            b.Property(x => x.InvoiceItemId).HasMaxLength(64).IsRequired();
            b.Property(x => x.InvoiceId).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.InvoiceId, x.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<PairingRow>(b =>
        {
            b.ToTable("Pairings");
            b.HasKey(x => x.PairingId);
            b.Property(x => x.PairingId).HasMaxLength(64).IsRequired();
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.InvoiceDocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasMaxLength(32).IsRequired();
            b.Property(x => x.ReasonCode).HasMaxLength(64);
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.RunId, x.InvoiceDocumentId, x.InvoiceProcessingRevision }).IsUnique();
        });

        modelBuilder.Entity<ArchivedArtifactRow>(b =>
        {
            b.ToTable("ArchivedArtifacts");
            b.HasKey(x => x.ArtifactId);
            b.Property(x => x.ArtifactId).HasMaxLength(64).IsRequired();
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.DocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Role).HasMaxLength(32).IsRequired();
            b.Property(x => x.RelativePath).HasMaxLength(512).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(256).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasMaxLength(16).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.RunId, x.DocumentId, x.ProcessingRevision, x.Role, x.ContentHash }).IsUnique();
        });

        modelBuilder.Entity<RunCheckpointRow>(b =>
        {
            b.ToTable("RunCheckpoints");
            b.HasKey(x => new { x.RunId, x.NodeId });
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.NodeId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Stage).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasMaxLength(32).IsRequired();
            b.Property(x => x.UpdatedAtUtc).IsRequired();
            b.HasIndex(x => new { x.RunId, x.Stage, x.LastCommittedSequence });
        });

        modelBuilder.Entity<MailboxCursorRow>(b =>
        {
            b.ToTable("MailboxCursors");
            b.HasKey(x => new { x.AccountId, x.Mailbox });
            b.Property(x => x.AccountId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Mailbox).HasMaxLength(128).IsRequired();
            b.Property(x => x.UpdatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<MailboxAccountRow>(b =>
        {
            b.ToTable("MailboxAccounts");
            b.HasKey(x => x.AccountId);
            b.Property(x => x.AccountId).HasMaxLength(64).IsRequired();
            b.Property(x => x.EmailAddress).HasMaxLength(256).IsRequired();
            b.Property(x => x.ImapHost).HasMaxLength(256).IsRequired();
            b.Property(x => x.ImapPort).IsRequired();
            b.Property(x => x.UseTls).IsRequired();
            b.Property(x => x.CredentialName).HasMaxLength(64).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(128);
            b.Property(x => x.Revision).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.Property(x => x.UpdatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<AuditEventRow>(b =>
        {
            b.ToTable("AuditEvents");
            b.HasKey(x => x.AuditEventId);
            b.Property(x => x.AuditEventId).HasMaxLength(64).IsRequired();
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            b.Property(x => x.Stage).HasMaxLength(64).IsRequired();
            b.Property(x => x.NodeId).HasMaxLength(64).IsRequired();
            b.Property(x => x.ReasonCode).HasMaxLength(64);
            b.Property(x => x.OccurredAtUtc).IsRequired();
            b.HasIndex(x => new { x.RunId, x.EventSequence }).IsUnique();
            b.HasIndex(x => new { x.DocumentId, x.ProcessingRevision, x.EventType });
        });

        modelBuilder.Entity<RunEventRow>(b =>
        {
            b.ToTable("RunEvents");
            b.HasKey(x => new { x.RunId, x.EventSequence });
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            b.Property(x => x.EmittedAtUtc).IsRequired();
            b.HasIndex(x => new { x.RunId, x.ExpiresAtUtc });
        });

        modelBuilder.Entity<RuleSetRow>(b =>
        {
            b.ToTable("RuleSets");
            b.HasKey(x => new { x.RuleSetId, x.Version });
            b.Property(x => x.RuleSetId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SchemaVersion).HasMaxLength(16).IsRequired();
            b.Property(x => x.SourceJson).IsRequired();
            b.Property(x => x.SourceFingerprint).HasMaxLength(64).IsRequired();
            b.Property(x => x.AstFingerprint).HasMaxLength(64).IsRequired();
            b.Property(x => x.CreatedBy).HasMaxLength(64);
            b.Property(x => x.CreatedAtUtc).IsRequired();
            b.HasIndex(x => x.SourceFingerprint);
            b.HasIndex(x => x.AstFingerprint);
        });

        modelBuilder.Entity<ManualReviewItemRow>(b =>
        {
            b.ToTable("ManualReviewItems");
            b.HasKey(x => x.ReviewId);
            b.Property(x => x.ReviewId).HasMaxLength(64).IsRequired();
            b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
            b.Property(x => x.DocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.ReasonCode).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasMaxLength(32).IsRequired();
            b.Property(x => x.CurrentRevision).IsRequired();
            b.Property(x => x.CreatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<LegacyImportStateRow>(b =>
        {
            b.ToTable("LegacyImportState");
            b.HasKey(x => x.ImportId);
            b.Property(x => x.ImportId).HasMaxLength(64).IsRequired();
            b.Property(x => x.SourceFingerprint).HasMaxLength(64).IsRequired();
            b.Property(x => x.ImportedAtUtc).IsRequired();
            b.Property(x => x.SecretReentryRequired).IsRequired();
        });
    }
}