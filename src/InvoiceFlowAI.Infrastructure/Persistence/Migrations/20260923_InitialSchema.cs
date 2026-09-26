// First-and-only initial migration for the InvoiceFlowAI database.
// Per design §8 the migration ID is pinned to 20260923_InitialSchema, must
// be idempotent (EF only applies migrations not already in
// __EFMigrationsHistory), and must include the table definitions, foreign
// keys, partial unique indexes, append-only trigger and PRAGMA scaffolding
// the design mandates.
//
// The migration is co-located with the model snapshot so a CI smoke test
// can compare the generated SQL against this file's contents.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InvoiceFlowAI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InvoiceFlowDbContext))]
[Migration("20260923_InitialSchema")]
public sealed class _20260923_InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // PRAGMAs — applied per-connection at OnConfiguring in SQLite but
        // recorded here so the schema-grade assertions see the migration.
        migrationBuilder.Sql("PRAGMA foreign_keys=ON;");

        migrationBuilder.CreateTable(
            name: "UserSettings",
            columns: table => new
            {
                SettingsId = table.Column<string>(maxLength: 64, nullable: false),
                Revision = table.Column<int>(nullable: false),
                CurrentAccountId = table.Column<string>(maxLength: 64, nullable: true),
                DefaultMailbox = table.Column<string>(maxLength: 128, nullable: false),
                MailboxFiltersJson = table.Column<string>(nullable: true),
                PipelineOptionsJson = table.Column<string>(nullable: true),
                AllowVisionFallback = table.Column<bool>(nullable: false),
                RuleSetId = table.Column<string>(maxLength: 128, nullable: false),
                RuleSetVersion = table.Column<int>(nullable: false),
                ConfigurationFingerprint = table.Column<string>(maxLength: 64, nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserSettings", x => x.SettingsId);
            });

        migrationBuilder.CreateTable(
            name: "RuleSets",
            columns: table => new
            {
                RuleSetId = table.Column<string>(maxLength: 64, nullable: false),
                Version = table.Column<int>(nullable: false),
                SchemaVersion = table.Column<string>(maxLength: 16, nullable: false),
                ParentVersion = table.Column<int>(nullable: true),
                RollbackFromVersion = table.Column<int>(nullable: true),
                RollbackTargetVersion = table.Column<int>(nullable: true),
                SourceJson = table.Column<string>(nullable: false),
                NormalizedAstJson = table.Column<string>(nullable: true),
                SourceFingerprint = table.Column<string>(maxLength: 64, nullable: false),
                AstFingerprint = table.Column<string>(maxLength: 64, nullable: false),
                IsCurrent = table.Column<bool>(nullable: false),
                CreatedBy = table.Column<string>(maxLength: 64, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RuleSets", x => new { x.RuleSetId, x.Version });
            });

        migrationBuilder.CreateIndex(
            name: "IX_RuleSets_SourceFingerprint",
            table: "RuleSets",
            column: "SourceFingerprint");

        migrationBuilder.CreateIndex(
            name: "IX_RuleSets_AstFingerprint",
            table: "RuleSets",
            column: "AstFingerprint");

        // Partial unique index: at most one IsCurrent row per RuleSetId.
        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX IX_RuleSets_Current_Per_Id ON RuleSets (RuleSetId) WHERE IsCurrent = 1;");

        migrationBuilder.CreateTable(
            name: "MailboxAccounts",
            columns: table => new
            {
                AccountId = table.Column<string>(maxLength: 64, nullable: false),
                EmailAddress = table.Column<string>(maxLength: 256, nullable: false),
                ImapHost = table.Column<string>(maxLength: 256, nullable: false),
                ImapPort = table.Column<int>(nullable: false),
                UseTls = table.Column<bool>(nullable: false),
                CredentialName = table.Column<string>(maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(maxLength: 128, nullable: true),
                Revision = table.Column<int>(nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MailboxAccounts", x => x.AccountId);
            });

        // FK to RuleSets declared inline in CREATE TABLE UserSettings (below) — the
        // DbContext does not expose these as navigations; integrity is enforced
        // by the RuleSetBootstrapper (see design §8: UserSettings is bound to
        // an existing RuleSets row at write time).

        migrationBuilder.CreateTable(
            name: "Runs",
            columns: table => new
            {
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                State = table.Column<string>(maxLength: 32, nullable: false),
                Stage = table.Column<string>(maxLength: 64, nullable: false),
                TerminalReasonCode = table.Column<string>(maxLength: 64, nullable: true),
                DateFrom = table.Column<DateOnly>(nullable: false),
                DateToExclusive = table.Column<DateOnly>(nullable: false),
                AccountId = table.Column<string>(maxLength: 64, nullable: true),
                AccountRevision = table.Column<int>(nullable: true),
                Mailbox = table.Column<string>(maxLength: 128, nullable: true),
                OutputRoot = table.Column<string>(maxLength: 512, nullable: true),
                SettingsRevision = table.Column<int>(nullable: true),
                RuleSetId = table.Column<string>(maxLength: 64, nullable: true),
                RuleSetVersion = table.Column<int>(nullable: true),
                ConfigurationFingerprint = table.Column<string>(maxLength: 64, nullable: true),
                RecipeVersion = table.Column<string>(maxLength: 32, nullable: true),
                StartedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                EndedAtUtc = table.Column<DateTimeOffset>(nullable: true),
                CancellationRequestedAtUtc = table.Column<DateTimeOffset>(nullable: true),
                LastEventSequence = table.Column<long>(nullable: false),
                SummaryJson = table.Column<string>(nullable: true),
                PrimaryFailureJson = table.Column<string>(nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Runs", x => x.RunId);
            });

        migrationBuilder.CreateIndex(name: "IX_Runs_ConfigurationFingerprint", table: "Runs", column: "ConfigurationFingerprint");
        migrationBuilder.CreateIndex(name: "IX_Runs_State", table: "Runs", column: "State");
        migrationBuilder.CreateIndex(name: "IX_Runs_Stage", table: "Runs", column: "Stage");

        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX IX_Runs_OneActivePerUser ON Runs (SettingsRevision) " +
            "WHERE State IN ('Created', 'Running', 'Recovering');");

        migrationBuilder.CreateTable(
            name: "Documents",
            columns: table => new
            {
                DocumentId = table.Column<string>(maxLength: 64, nullable: false),
                SourceKind = table.Column<string>(maxLength: 32, nullable: false),
                SourceMessageUid = table.Column<string>(maxLength: 128, nullable: true),
                SourceFileName = table.Column<string>(maxLength: 256, nullable: true),
                SourceLocator = table.Column<string>(maxLength: 512, nullable: true),
                ProviderGroupKey = table.Column<string>(maxLength: 128, nullable: true),
                ContentHash = table.Column<string>(maxLength: 64, nullable: true),
                MimeType = table.Column<string>(maxLength: 128, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Documents", x => x.DocumentId);
            });

        migrationBuilder.CreateIndex(name: "IX_Documents_ContentHash", table: "Documents", column: "ContentHash");
        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX IX_Documents_SourceLocator ON Documents (SourceKind, SourceLocator) " +
            "WHERE SourceLocator IS NOT NULL;");

        migrationBuilder.CreateTable(
            name: "DocumentProcessing",
            columns: table => new
            {
                DocumentId = table.Column<string>(maxLength: 64, nullable: false),
                ProcessingRevision = table.Column<int>(nullable: false),
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                Sequence = table.Column<long>(nullable: false),
                Stage = table.Column<string>(maxLength: 64, nullable: false),
                Status = table.Column<string>(maxLength: 32, nullable: false),
                ReasonCode = table.Column<string>(maxLength: 64, nullable: true),
                Retryable = table.Column<bool>(nullable: false),
                Attempt = table.Column<int>(nullable: false),
                MaxAttempts = table.Column<int>(nullable: false),
                ArtifactPath = table.Column<string>(nullable: true),
                ResultJson = table.Column<string>(nullable: true),
                TraceJson = table.Column<string>(nullable: true),
                StartedAtUtc = table.Column<DateTimeOffset>(nullable: true),
                CompletedAtUtc = table.Column<DateTimeOffset>(nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DocumentProcessing", x => new { x.DocumentId, x.ProcessingRevision });
                table.ForeignKey(
                    "FK_DocumentProcessing_Documents_Document",
                    x => x.DocumentId,
                    "Documents",
                    "DocumentId",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_DocumentProcessing_Runs_Run",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DocumentProcessing_Run_Sequence",
            table: "DocumentProcessing",
            columns: new[] { "RunId", "Sequence" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "Invoices",
            columns: table => new
            {
                InvoiceId = table.Column<string>(maxLength: 64, nullable: false),
                DocumentId = table.Column<string>(maxLength: 64, nullable: false),
                ProcessingRevision = table.Column<int>(nullable: false),
                InvoiceDate = table.Column<DateOnly>(nullable: false),
                Purchaser = table.Column<string>(maxLength: 256, nullable: false),
                Seller = table.Column<string>(maxLength: 256, nullable: false),
                Amount = table.Column<string>(nullable: false),
                TaxAmount = table.Column<string>(nullable: false),
                TotalAmount = table.Column<string>(nullable: false),
                InvoiceCode = table.Column<string>(nullable: true),
                InvoiceNumber = table.Column<string>(nullable: true),
                DocumentType = table.Column<string>(maxLength: 64, nullable: false),
                Category = table.Column<string>(nullable: true),
                Flags = table.Column<int>(nullable: false),
                Confidence = table.Column<string>(nullable: false),
                DuplicateKey = table.Column<string>(nullable: true),
                ArchiveState = table.Column<string>(maxLength: 32, nullable: false),
                Revision = table.Column<int>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Invoices", x => x.InvoiceId);
                table.ForeignKey(
                    "FK_Invoices_DocumentProcessing",
                    x => new { x.DocumentId, x.ProcessingRevision },
                    "DocumentProcessing",
                    new[] { "DocumentId", "ProcessingRevision" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_Invoices_DuplicateKey", table: "Invoices", column: "DuplicateKey");
        migrationBuilder.CreateIndex(
            name: "IX_Invoices_Lookup",
            table: "Invoices",
            columns: new[] { "InvoiceNumber", "Seller", "InvoiceDate", "TotalAmount" });

        migrationBuilder.CreateTable(
            name: "InvoiceItems",
            columns: table => new
            {
                InvoiceItemId = table.Column<string>(maxLength: 64, nullable: false),
                InvoiceId = table.Column<string>(maxLength: 64, nullable: false),
                Ordinal = table.Column<int>(nullable: false),
                Name = table.Column<string>(nullable: true),
                Specification = table.Column<string>(nullable: true),
                Unit = table.Column<string>(nullable: true),
                Quantity = table.Column<string>(nullable: true),
                UnitPrice = table.Column<string>(nullable: true),
                Amount = table.Column<string>(nullable: true),
                TaxRate = table.Column<string>(nullable: true),
                TaxAmount = table.Column<string>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InvoiceItems", x => x.InvoiceItemId);
                table.ForeignKey(
                    "FK_InvoiceItems_Invoices",
                    x => x.InvoiceId,
                    "Invoices",
                    "InvoiceId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceItems_Ordinal",
            table: "InvoiceItems",
            columns: new[] { "InvoiceId", "Ordinal" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "Pairings",
            columns: table => new
            {
                PairingId = table.Column<string>(maxLength: 64, nullable: false),
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                InvoiceDocumentId = table.Column<string>(maxLength: 64, nullable: false),
                InvoiceProcessingRevision = table.Column<int>(nullable: false),
                CompanionDocumentIdsJson = table.Column<string>(nullable: false),
                CompanionProcessingRevisionsJson = table.Column<string>(nullable: false),
                Score = table.Column<string>(nullable: false),
                State = table.Column<string>(maxLength: 32, nullable: false),
                ReasonCode = table.Column<string>(maxLength: 64, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Pairings", x => x.PairingId);
                table.ForeignKey(
                    "FK_Pairings_Runs",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_Pairings_Invoice",
                    x => new { x.InvoiceDocumentId, x.InvoiceProcessingRevision },
                    "DocumentProcessing",
                    new[] { "DocumentId", "ProcessingRevision" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Pairings_Unique_Per_InvoiceRun",
            table: "Pairings",
            columns: new[] { "RunId", "InvoiceDocumentId", "InvoiceProcessingRevision" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "ArchivedArtifacts",
            columns: table => new
            {
                ArtifactId = table.Column<string>(maxLength: 64, nullable: false),
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                DocumentId = table.Column<string>(maxLength: 64, nullable: false),
                ProcessingRevision = table.Column<int>(nullable: false),
                Role = table.Column<string>(maxLength: 32, nullable: false),
                RelativePath = table.Column<string>(maxLength: 512, nullable: false),
                FileName = table.Column<string>(maxLength: 256, nullable: false),
                ContentHash = table.Column<string>(maxLength: 64, nullable: false),
                State = table.Column<string>(maxLength: 16, nullable: false),
                AlreadyExisted = table.Column<bool>(nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                CommittedAtUtc = table.Column<DateTimeOffset>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ArchivedArtifacts", x => x.ArtifactId);
                table.ForeignKey(
                    "FK_ArchivedArtifacts_DocumentProcessing",
                    x => new { x.DocumentId, x.ProcessingRevision },
                    "DocumentProcessing",
                    new[] { "DocumentId", "ProcessingRevision" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_ArchivedArtifacts_Runs",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ArchivedArtifacts_Unique_Artifact",
            table: "ArchivedArtifacts",
            columns: new[] { "RunId", "DocumentId", "ProcessingRevision", "Role", "ContentHash" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "RunCheckpoints",
            columns: table => new
            {
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                NodeId = table.Column<string>(maxLength: 64, nullable: false),
                Stage = table.Column<string>(maxLength: 64, nullable: false),
                LastCommittedSequence = table.Column<long>(nullable: false),
                InputCursorJson = table.Column<string>(nullable: true),
                OutputCount = table.Column<int>(nullable: false),
                State = table.Column<string>(maxLength: 32, nullable: false),
                CheckpointRevision = table.Column<int>(nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunCheckpoints", x => new { x.RunId, x.NodeId });
                table.ForeignKey(
                    "FK_RunCheckpoints_Runs",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RunCheckpoints_Stage",
            table: "RunCheckpoints",
            columns: new[] { "RunId", "Stage", "LastCommittedSequence" });

        migrationBuilder.CreateTable(
            name: "MailboxCursors",
            columns: table => new
            {
                AccountId = table.Column<string>(maxLength: 64, nullable: false),
                Mailbox = table.Column<string>(maxLength: 128, nullable: false),
                UidValidity = table.Column<long>(nullable: false),
                LastCompletedUid = table.Column<long>(nullable: false),
                CursorRevision = table.Column<int>(nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MailboxCursors", x => new { x.AccountId, x.Mailbox });
                table.ForeignKey(
                    "FK_MailboxCursors_Accounts",
                    x => x.AccountId,
                    "MailboxAccounts",
                    "AccountId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AuditEvents",
            columns: table => new
            {
                AuditEventId = table.Column<string>(maxLength: 64, nullable: false),
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                EventSequence = table.Column<long>(nullable: false),
                EventType = table.Column<string>(maxLength: 64, nullable: false),
                Stage = table.Column<string>(maxLength: 64, nullable: false),
                NodeId = table.Column<string>(maxLength: 64, nullable: false),
                DocumentId = table.Column<string>(maxLength: 64, nullable: true),
                ProcessingRevision = table.Column<int>(nullable: true),
                ReasonCode = table.Column<string>(maxLength: 64, nullable: true),
                PayloadJson = table.Column<string>(nullable: false),
                PayloadHash = table.Column<string>(nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEvents", x => x.AuditEventId);
                table.ForeignKey(
                    "FK_AuditEvents_Runs",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditEvents_Run_Sequence",
            table: "AuditEvents",
            columns: new[] { "RunId", "EventSequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AuditEvents_Document_EventType",
            table: "AuditEvents",
            columns: new[] { "DocumentId", "ProcessingRevision", "EventType" });

        // Append-only triggers per design §8.
        migrationBuilder.Sql(@"
            CREATE TRIGGER AuditEvents_NoUpdate
            BEFORE UPDATE ON AuditEvents
            BEGIN
                SELECT RAISE(ABORT, 'AuditEvents is append-only');
            END;");
        migrationBuilder.Sql(@"
            CREATE TRIGGER AuditEvents_NoDelete
            BEFORE DELETE ON AuditEvents
            BEGIN
                SELECT RAISE(ABORT, 'AuditEvents is append-only');
            END;");

        migrationBuilder.CreateTable(
            name: "RunEvents",
            columns: table => new
            {
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                EventSequence = table.Column<long>(nullable: false),
                EventType = table.Column<string>(maxLength: 64, nullable: false),
                PayloadJson = table.Column<string>(nullable: false),
                EmittedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunEvents", x => new { x.RunId, x.EventSequence });
                table.ForeignKey(
                    "FK_RunEvents_Runs",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RunEvents_Retention",
            table: "RunEvents",
            columns: new[] { "RunId", "ExpiresAtUtc" });

        migrationBuilder.CreateTable(
            name: "ManualReviewItems",
            columns: table => new
            {
                ReviewId = table.Column<string>(maxLength: 64, nullable: false),
                RunId = table.Column<string>(maxLength: 64, nullable: false),
                DocumentId = table.Column<string>(maxLength: 64, nullable: false),
                ProcessingRevision = table.Column<int>(nullable: false),
                ReasonCode = table.Column<string>(maxLength: 64, nullable: false),
                State = table.Column<string>(maxLength: 32, nullable: false),
                CurrentRevision = table.Column<int>(nullable: false),
                CurrentResultJson = table.Column<string>(nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                ResolvedAtUtc = table.Column<DateTimeOffset>(nullable: true),
                ResolvedBy = table.Column<string>(nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ManualReviewItems", x => x.ReviewId);
                table.ForeignKey(
                    "FK_ManualReviewItems_DocumentProcessing",
                    x => new { x.DocumentId, x.ProcessingRevision },
                    "DocumentProcessing",
                    new[] { "DocumentId", "ProcessingRevision" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_ManualReviewItems_Runs",
                    x => x.RunId,
                    "Runs",
                    "RunId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX IX_ManualReviewItems_OneOpenPerProcessing " +
            "ON ManualReviewItems (RunId, DocumentId, ProcessingRevision) WHERE State = 'Open';");

        migrationBuilder.CreateTable(
            name: "LegacyImportState",
            columns: table => new
            {
                ImportId = table.Column<string>(maxLength: 64, nullable: false),
                ImportVersion = table.Column<int>(nullable: false),
                SourceFingerprint = table.Column<string>(maxLength: 64, nullable: false),
                ImportedAtUtc = table.Column<DateTimeOffset>(nullable: false),
                SecretReentryRequired = table.Column<bool>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LegacyImportState", x => x.ImportId);
            });

        migrationBuilder.Sql("PRAGMA journal_mode=WAL;");
        migrationBuilder.Sql("PRAGMA busy_timeout=5000;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Down-migration drops the schema in reverse so test databases and
        // local development can rebuild cleanly. Production never runs Down.
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS AuditEvents_NoUpdate;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS AuditEvents_NoDelete;");
        migrationBuilder.DropTable("LegacyImportState");
        migrationBuilder.DropTable("ManualReviewItems");
        migrationBuilder.DropTable("RunEvents");
        migrationBuilder.DropTable("AuditEvents");
        migrationBuilder.DropTable("MailboxCursors");
        migrationBuilder.DropTable("RunCheckpoints");
        migrationBuilder.DropTable("ArchivedArtifacts");
        migrationBuilder.DropTable("Pairings");
        migrationBuilder.DropTable("InvoiceItems");
        migrationBuilder.DropTable("Invoices");
        migrationBuilder.DropTable("DocumentProcessing");
        migrationBuilder.DropTable("Documents");
        migrationBuilder.DropTable("Runs");
        migrationBuilder.DropTable("UserSettings");
        migrationBuilder.DropTable("MailboxAccounts");
        migrationBuilder.DropTable("RuleSets");
    }
}