# 持久化层补齐 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 补齐 `DocumentProcessing` / `Invoices` 的写入层,让归档阶段的复核与归档写入满足外键约束,端到端流水线可以跑完。

**Architecture:** 在 `Application/Persistence` 增加两个写端口 `IDocumentProcessingStore`、`IInvoiceStore`;在 `Infrastructure/Persistence/Stores` 用现有 `EfUnitOfWork` 模式实现;`DocumentArchivingStage` 在按候选分发前,用一个 `CandidateCommit` 事务为每个候选 upsert `DocumentProcessing`(并按需 upsert `Documents` 源行与 `Invoices`/`InvoiceItems`),从而满足 `ManualReviewItems` 与 `ArchivedArtifacts` 的 `(DocumentId, ProcessingRevision)` 复合外键。

**Tech Stack:** .NET 10、EF Core + SQLite(`Microsoft.Data.Sqlite`)、xunit.v3 + FluentAssertions、Microsoft.Extensions.DependencyInjection。

**Spec:** `docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md`(持久化契约见 `DocumentProcessing`/`Invoices` 表定义与"事务、审计与外部文件一致性"一节约 3170-3207 行)

## Global Constraints

- 测试命令不得带 `--nologo`:本仓库 `global.json` 启用 Microsoft.Testing.Platform,`dotnet test --nologo` 会转发给测试应用导致退出码 5、零测试。
- 不提交 git,除非用户明确要求。
- `ResultJson`/`TraceJson` 禁止包含原文、图片、凭据;本计划只写 `documentId` 与 `status`。
- 不改变既有公开行为;`DocumentArchivingStage` 新依赖使用可选构造参数,避免破坏既有测试构造点。
- `(DocumentId, ProcessingRevision)` 是幂等键;同一键重复写入必须是更新而非新增。
- **外键与插入顺序**:SQLite 已开启 `PRAGMA foreign_keys=1`(活体探针确认)。EF Core 没有为 `DocumentProcessing`/`Invoices`/`InvoiceItems` 配置任何关系(`InvoiceFlowDbContext` 中无 `HasOne`),因此同一次 `SaveChanges` 内的多条插入**不保证按外键顺序执行**,活体探针已实测 `Documents + DocumentProcessing` 同批插入触发 `FOREIGN KEY constraint failed`。两个 store 必须在持有事务的情况下显式 `await _context.SaveChangesAsync(...)`,把父行先落盘,再暂存子行。事务原子性不受影响(仍在同一个 `EfUnitOfWork` 事务内)。
- 所有金额/数量以 invariant culture 文本落库,禁止浮点。

---

### Task 1: DocumentProcessing 写入端口与 EF 实现

**Files:**
- Create: `src/InvoiceFlowAI.Application/Persistence/IDocumentProcessingStore.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfDocumentProcessingStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`(第 60 行 `IManualReviewItemStore` 注册之后)
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/DocumentProcessingStoreTests.cs`

**Interfaces:**
- Consumes: `IUnitOfWork`(`src/InvoiceFlowAI.Application/Persistence/IUnitOfWork.cs`,含 `TransactionPurpose.CandidateCommit`)、`InvoiceFlowDbContext`、`DocumentSourceRow`、`DocumentProcessingRow`、`SqliteTestFixture`。
- Produces:
  - `public sealed record DocumentProcessingWrite(string DocumentId, int ProcessingRevision, string RunId, string SourceKind, string? SourceMessageUid, string? SourceFileName, string Stage, CandidateStatus Status, string ReasonCode, bool Retryable, int Attempt, int MaxAttempts, string? ArtifactPath, string? ResultJson, string? TraceJson, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc);`
  - `public interface IDocumentProcessingStore { Task UpsertAsync(DocumentProcessingWrite record, IUnitOfWork transaction, CancellationToken cancellationToken); }`
  - `EfDocumentProcessingStore(InvoiceFlowDbContext context)`;新行 `Sequence` 取该 Run 当前最大值 + 1(满足 `IX_DocumentProcessing_Run_Sequence` 唯一索引)。

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class DocumentProcessingStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public DocumentProcessingStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_creates_document_and_processing_rows_with_unique_run_sequences()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.Runs.Add(new RunRow
        {
            RunId = "run-processing",
            State = "Running",
            Stage = "archive-documents",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var uowFactory = new EfUnitOfWorkFactory(context);
        var store = new EfDocumentProcessingStore(context);
        var now = DateTimeOffset.UnixEpoch;

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await store.UpsertAsync(NewWrite("doc-a", "run-processing", now), transaction, CancellationToken.None);
            await store.UpsertAsync(NewWrite("doc-b", "run-processing", now), transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var documents = await context.Documents.AsNoTracking().OrderBy(row => row.DocumentId)
            .ToListAsync(TestContext.Current.CancellationToken);
        documents.Select(row => row.DocumentId).Should().Equal("doc-a", "doc-b");

        var rows = await context.DocumentProcessing.AsNoTracking().OrderBy(row => row.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Select(row => row.Sequence).Should().Equal(0L, 1L);
        rows.Should().OnlyContain(row => row.RunId == "run-processing" && row.Status == "Unresolved");

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await store.UpsertAsync(
                NewWrite("doc-a", "run-processing", now) with { Status = CandidateStatus.ManualReview, ReasonCode = "URL_RECOVERY_FAILED" },
                transaction,
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var updated = await context.DocumentProcessing.AsNoTracking()
            .SingleAsync(row => row.DocumentId == "doc-a", TestContext.Current.CancellationToken);
        updated.Status.Should().Be("ManualReview");
        updated.ReasonCode.Should().Be("URL_RECOVERY_FAILED");
        (await context.DocumentProcessing.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        (await context.Documents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    private static DocumentProcessingWrite NewWrite(string documentId, string runId, DateTimeOffset now) => new(
        DocumentId: documentId,
        ProcessingRevision: 0,
        RunId: runId,
        SourceKind: "url",
        SourceMessageUid: "uid-1",
        SourceFileName: "invoice-url.pdf",
        Stage: "archive-documents",
        Status: CandidateStatus.Unresolved,
        ReasonCode: "URL_RECOVERY_FAILED",
        Retryable: true,
        Attempt: 1,
        MaxAttempts: 2,
        ArtifactPath: null,
        ResultJson: "{}",
        TraceJson: null,
        StartedAtUtc: now,
        CompletedAtUtc: now);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`
Expected: 编译失败(找不到 `DocumentProcessingWrite` / `EfDocumentProcessingStore`)。

- [ ] **Step 3: 实现端口与 store**

`src/InvoiceFlowAI.Application/Persistence/IDocumentProcessingStore.cs`:

```csharp
using InvoiceFlowAI.Domain.Candidates;

namespace InvoiceFlowAI.Application.Persistence;

/// <summary>
/// Terminal state of one document processing attempt. (DocumentId,
/// ProcessingRevision) is the idempotency key; repeated writes update the
/// row instead of creating a second processing result.
/// </summary>
public sealed record DocumentProcessingWrite(
    string DocumentId,
    int ProcessingRevision,
    string RunId,
    string SourceKind,
    string? SourceMessageUid,
    string? SourceFileName,
    string Stage,
    CandidateStatus Status,
    string ReasonCode,
    bool Retryable,
    int Attempt,
    int MaxAttempts,
    string? ArtifactPath,
    string? ResultJson,
    string? TraceJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IDocumentProcessingStore
{
    Task UpsertAsync(
        DocumentProcessingWrite record,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}
```

`src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfDocumentProcessingStore.cs`:

```csharp
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfDocumentProcessingStore : IDocumentProcessingStore
{
    private readonly InvoiceFlowDbContext _context;

    public EfDocumentProcessingStore(InvoiceFlowDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task UpsertAsync(
        DocumentProcessingWrite record,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("DocumentProcessingStore writes must use EfUnitOfWork.");
        }
        ArgumentException.ThrowIfNullOrEmpty(record.DocumentId);
        ArgumentException.ThrowIfNullOrEmpty(record.RunId);

        var document = await _context.Documents
            .SingleOrDefaultAsync(row => row.DocumentId == record.DocumentId, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
        {
            _context.Documents.Add(new DocumentSourceRow
            {
                DocumentId = record.DocumentId,
                SourceKind = string.IsNullOrWhiteSpace(record.SourceKind) ? "unknown" : record.SourceKind,
                SourceMessageUid = record.SourceMessageUid,
                SourceFileName = record.SourceFileName,
                SourceLocator = record.DocumentId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            // FK_DocumentProcessing_Documents is enforced at INSERT time and
            // EF batches inserts without a model relationship, so the parent
            // row must be durable before the processing row is staged.
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var row = await _context.DocumentProcessing
            .SingleOrDefaultAsync(
                candidate => candidate.DocumentId == record.DocumentId
                    && candidate.ProcessingRevision == record.ProcessingRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            var maxSequence = await _context.DocumentProcessing
                .Where(candidate => candidate.RunId == record.RunId)
                .MaxAsync(candidate => (long?)candidate.Sequence, cancellationToken)
                .ConfigureAwait(false);
            row = new DocumentProcessingRow
            {
                DocumentId = record.DocumentId,
                ProcessingRevision = record.ProcessingRevision,
                RunId = record.RunId,
                Sequence = (maxSequence ?? -1) + 1,
            };
            _context.DocumentProcessing.Add(row);
        }

        row.Stage = record.Stage;
        row.Status = record.Status.ToString();
        row.ReasonCode = record.ReasonCode;
        row.Retryable = record.Retryable;
        row.Attempt = record.Attempt;
        row.MaxAttempts = record.MaxAttempts;
        row.ArtifactPath = record.ArtifactPath;
        row.ResultJson = record.ResultJson;
        row.TraceJson = record.TraceJson;
        row.StartedAtUtc = record.StartedAtUtc;
        row.CompletedAtUtc = record.CompletedAtUtc;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // Flush now: the next candidate's MAX(Sequence)+1 runs in the same
        // transaction and would not see this row otherwise, producing a
        // duplicate (RunId, Sequence) key.
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

在 `AddInvoiceFlowInfrastructure` 中、第 60 行 `services.AddScoped<IManualReviewItemStore, EfManualReviewItemStore>();` 之后加:

```csharp
services.AddScoped<IDocumentProcessingStore, EfDocumentProcessingStore>();
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`
Expected: PASS。

---

### Task 2: Invoice 写入端口与 EF 实现

**Files:**
- Create: `src/InvoiceFlowAI.Application/Persistence/IInvoiceStore.cs`
- Create: `src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfInvoiceStore.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs`(Task 1 新增的注册之后)
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/InvoiceStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `DocumentProcessingWrite` / `IDocumentProcessingStore`(测试先落 processing 行以满足外键)、`InvoiceDocument`、`InvoiceItem`、`InvoiceRow`、`InvoiceItemRow`。
- Produces: `public interface IInvoiceStore { Task UpsertAsync(InvoiceDocument invoice, int processingRevision, IUnitOfWork transaction, CancellationToken cancellationToken); }`;`EfInvoiceStore` 以 `InvoiceId = invoice.DocumentId` 为键,重写 `Invoices` 行并同步 `InvoiceItems`(按 ordinal 复用/新增/删除多余项),`ArchiveState = "Pending"`。

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Persistence;

public sealed class InvoiceStoreTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public InvoiceStoreTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_persists_invoice_and_items_and_replaces_items_on_reupsert()
    {
        await _fixture.ResetAsync();
        await using var context = _fixture.CreateContext();
        context.Runs.Add(new RunRow
        {
            RunId = "run-invoice",
            State = "Running",
            Stage = "archive-documents",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var uowFactory = new EfUnitOfWorkFactory(context);
        var processingStore = new EfDocumentProcessingStore(context);
        var invoiceStore = new EfInvoiceStore(context);
        var now = DateTimeOffset.UnixEpoch;

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await processingStore.UpsertAsync(
                new DocumentProcessingWrite(
                    DocumentId: "doc-invoice",
                    ProcessingRevision: 0,
                    RunId: "run-invoice",
                    SourceKind: "attachment",
                    SourceMessageUid: "uid-1",
                    SourceFileName: "invoice.pdf",
                    Stage: "archive-documents",
                    Status: CandidateStatus.Resolved,
                    ReasonCode: string.Empty,
                    Retryable: false,
                    Attempt: 1,
                    MaxAttempts: 1,
                    ArtifactPath: "/tmp/source.pdf",
                    ResultJson: "{}",
                    TraceJson: null,
                    StartedAtUtc: now,
                    CompletedAtUtc: now),
                transaction,
                CancellationToken.None);
            await invoiceStore.UpsertAsync(NewInvoice(2), 0, transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using (var transaction = await uowFactory.BeginAsync(TransactionPurpose.CandidateCommit, CancellationToken.None))
        {
            await invoiceStore.UpsertAsync(NewInvoice(1), 0, transaction, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var invoice = await context.Invoices.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        invoice.InvoiceId.Should().Be("doc-invoice");
        invoice.DocumentId.Should().Be("doc-invoice");
        invoice.ProcessingRevision.Should().Be(0);
        invoice.Purchaser.Should().Be("Buyer");
        invoice.Seller.Should().Be("Merchant");
        invoice.TotalAmount.Should().Be("113.00");
        invoice.DocumentType.Should().Be(nameof(InvoiceDocumentType.RideInvoice));
        invoice.ArchiveState.Should().Be("Pending");

        var items = await context.InvoiceItems.AsNoTracking().OrderBy(row => row.Ordinal)
            .ToListAsync(TestContext.Current.CancellationToken);
        items.Should().HaveCount(1);
        items[0].Ordinal.Should().Be(0);
        items[0].Name.Should().Be("Item 0");
        items[0].Amount.Should().Be("56.50");
    }

    private static InvoiceDocument NewInvoice(int itemCount) => new(
        DocumentId: "doc-invoice",
        InvoiceDate: new DateOnly(2026, 9, 24),
        Purchaser: "Buyer",
        Seller: "Merchant",
        Amount: 100m,
        TaxAmount: 13m,
        TotalAmount: 113.00m,
        InvoiceCode: "code",
        InvoiceNumber: "number",
        DocumentType: InvoiceDocumentType.RideInvoice,
        Category: null,
        Route: null,
        Items: Enumerable.Range(0, itemCount)
            .Select(index => new InvoiceItem($"Item {index}", 1m, 56.50m, 56.50m))
            .ToArray(),
        SourceFileName: "invoice.pdf",
        ContentHash: new string('a', 64));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`
Expected: 编译失败(缺少 `IInvoiceStore` / `EfInvoiceStore`)。

- [ ] **Step 3: 实现**

`src/InvoiceFlowAI.Application/Persistence/IInvoiceStore.cs`:

```csharp
using InvoiceFlowAI.Domain.Invoices;

namespace InvoiceFlowAI.Application.Persistence;

public interface IInvoiceStore
{
    Task UpsertAsync(
        InvoiceDocument invoice,
        int processingRevision,
        IUnitOfWork transaction,
        CancellationToken cancellationToken);
}
```

`src/InvoiceFlowAI.Infrastructure/Persistence/Stores/EfInvoiceStore.cs`:

```csharp
using System.Globalization;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlowAI.Infrastructure.Persistence.Stores;

public sealed class EfInvoiceStore : IInvoiceStore
{
    private const string PendingArchiveState = "Pending";

    private readonly InvoiceFlowDbContext _context;

    public EfInvoiceStore(InvoiceFlowDbContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task UpsertAsync(
        InvoiceDocument invoice,
        int processingRevision,
        IUnitOfWork transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not EfUnitOfWork)
        {
            throw new InvalidOperationException("InvoiceStore writes must use EfUnitOfWork.");
        }
        ArgumentException.ThrowIfNullOrEmpty(invoice.DocumentId);
        ArgumentOutOfRangeException.ThrowIfNegative(processingRevision);

        // FK_Invoices_DocumentProcessing is enforced at INSERT time; flush the
        // processing rows staged earlier in this transaction first because EF
        // does not order inserts between unrelated entity types.
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var invoiceId = invoice.DocumentId;
        var row = await _context.Invoices
            .SingleOrDefaultAsync(candidate => candidate.InvoiceId == invoiceId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            row = new InvoiceRow { InvoiceId = invoiceId };
            _context.Invoices.Add(row);
        }

        row.DocumentId = invoiceId;
        row.ProcessingRevision = processingRevision;
        row.InvoiceDate = invoice.InvoiceDate ?? default;
        row.Purchaser = invoice.Purchaser ?? string.Empty;
        row.Seller = invoice.Seller ?? string.Empty;
        row.Amount = Money(invoice.Amount);
        row.TaxAmount = Money(invoice.TaxAmount);
        row.TotalAmount = Money(invoice.TotalAmount);
        row.InvoiceCode = invoice.InvoiceCode;
        row.InvoiceNumber = invoice.InvoiceNumber;
        row.DocumentType = invoice.DocumentType.ToString();
        row.Category = invoice.Category;
        row.Flags = (int)invoice.Flags;
        row.Confidence = invoice.Confidence.ToString(CultureInfo.InvariantCulture);
        row.ArchiveState = PendingArchiveState;

        // Flush the invoice before staging its line items for the same
        // FK-ordering reason (FK_InvoiceItems_Invoices).
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var existing = (await _context.InvoiceItems
                .Where(item => item.InvoiceId == invoiceId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(item => item.Ordinal);

        for (var ordinal = 0; ordinal < invoice.Items.Count; ordinal++)
        {
            var item = invoice.Items[ordinal];
            if (!existing.Remove(ordinal, out var itemRow))
            {
                itemRow = new InvoiceItemRow
                {
                    InvoiceItemId = $"{invoiceId}-{ordinal}",
                    InvoiceId = invoiceId,
                    Ordinal = ordinal,
                };
                _context.InvoiceItems.Add(itemRow);
            }

            itemRow.Name = item.Name;
            itemRow.Specification = item.Specification;
            itemRow.Unit = item.Unit;
            itemRow.Quantity = item.Quantity.ToString(CultureInfo.InvariantCulture);
            itemRow.UnitPrice = item.UnitPrice.ToString(CultureInfo.InvariantCulture);
            itemRow.Amount = item.Amount.ToString(CultureInfo.InvariantCulture);
            itemRow.TaxAmount = item.TaxAmount?.ToString(CultureInfo.InvariantCulture);
        }

        _context.InvoiceItems.RemoveRange(existing.Values);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Money(decimal? value) =>
        (value ?? 0m).ToString(CultureInfo.InvariantCulture);
}
```

在 `AddInvoiceFlowInfrastructure` 中加:

```csharp
services.AddScoped<IInvoiceStore, EfInvoiceStore>();
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`
Expected: PASS。

---

### Task 3: 归档阶段接入持久化(修复活体失败)

**Files:**
- Modify: `src/InvoiceFlowAI.Infrastructure/Archive/DocumentArchivingStage.cs`(字段/构造函数、`ExecuteAsync` 开头与结尾、新增私有方法)
- Test: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStagePersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1/2 的 `IDocumentProcessingStore`、`IInvoiceStore`、`DocumentProcessingWrite`;`ArchiveStageRequest`、`CandidateProcessResult`、`PairingBatch`、`EfUnitOfWorkFactory`、`EfArchiveArtifactStore`、`EfAuditStore`、`EfManualReviewItemStore`、`EfPairingStore`、`PhysicalArchiveFileSystem`、`ArchiveNamingPolicy`、`ArchiveCommitCoordinator`。
- Produces: `DocumentArchivingStage` 构造函数末尾新增两个可选参数 `IDocumentProcessingStore? processingStore = null, IInvoiceStore? invoiceStore = null`;`ExecuteAsync` 在分发前调用 `PersistTerminalStatesAsync`,保证每个候选都有 `DocumentProcessing` 行(以及可解析发票的 `Invoices` 行)后才进入复核/归档路径;分发结束后再 best-effort 刷新一次终态。

- [ ] **Step 1: 写失败测试(复现活体故障)**

```csharp
using FluentAssertions;
using InvoiceFlowAI.Application.Archive;
using InvoiceFlowAI.Application.Pairing;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Pipeline;
using InvoiceFlowAI.Domain.Candidates;
using InvoiceFlowAI.Domain.Invoices;
using InvoiceFlowAI.Infrastructure.Archive;
using InvoiceFlowAI.Infrastructure.Persistence;
using InvoiceFlowAI.Infrastructure.Persistence.Entities;
using InvoiceFlowAI.Infrastructure.Persistence.Stores;
using InvoiceFlowAI.Infrastructure.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvoiceFlowAI.Infrastructure.Tests.Archive;

public sealed class DocumentArchivingStagePersistenceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public DocumentArchivingStagePersistenceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Review_path_creates_processing_row_and_review_item_without_preseeded_processing()
    {
        await _fixture.ResetAsync();
        const string runId = "run-live-regression";
        var source = CreateSource("unrecovered.pdf", "unrecovered bytes");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"invoice-flow-live-{Guid.NewGuid():N}");
        try
        {
            await using var context = _fixture.CreateContext();
            context.Runs.Add(new RunRow
            {
                RunId = runId,
                State = "Running",
                Stage = "archive-documents",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var stage = CreateStage(context);
            var result = new CandidateProcessResult(
                new DocumentCandidate(
                    DocumentIdentity.Create("doc-live-review"),
                    Sequence: 0,
                    CorrelationId: "corr",
                    SourceMessageUid: "uid",
                    OriginalFileName: "unrecovered.pdf",
                    ContentType: "application/pdf",
                    ContentLength: 17,
                    ProcessingRevision: 0,
                    SourceKind: "url"),
                CandidateStatus.ManualReview,
                Invoice: null,
                ArtifactPath: source,
                Failure: new CandidateFailure(
                    "URL_RECOVERY_FAILED",
                    FailureScope.Candidate,
                    FailureCategory.Network,
                    Retryable: true,
                    SafeMessage: "Recovery failed."));

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest(runId, outputRoot, new PairingBatch([], [result])),
                CancellationToken.None);

            var processed = batch.Results.Single();
            processed.Status.Should().Be(CandidateStatus.ManualReview);
            processed.Failure!.ReasonCode.Should().Be("URL_RECOVERY_FAILED");

            await using var verify = _fixture.CreateContext();
            var processing = await verify.DocumentProcessing.AsNoTracking()
                .SingleAsync(row => row.DocumentId == "doc-live-review", TestContext.Current.CancellationToken);
            processing.Status.Should().Be("ManualReview");
            processing.ReasonCode.Should().Be("URL_RECOVERY_FAILED");
            processing.RunId.Should().Be(runId);

            var review = await verify.ManualReviewItems.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            review.DocumentId.Should().Be("doc-live-review");
            review.ReasonCode.Should().Be("URL_RECOVERY_FAILED");
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Resolved_path_archives_and_persists_invoice_without_preseeded_processing()
    {
        await _fixture.ResetAsync();
        const string runId = "run-live-resolved";
        var source = CreateSource("invoice.pdf", "invoice bytes");
        var outputRoot = Path.Combine(Path.GetTempPath(), $"invoice-flow-live-{Guid.NewGuid():N}");
        try
        {
            await using var context = _fixture.CreateContext();
            context.Runs.Add(new RunRow
            {
                RunId = runId,
                State = "Running",
                Stage = "archive-documents",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var stage = CreateStage(context);
            var invoice = new InvoiceDocument(
                DocumentId: "doc-live-resolved",
                InvoiceDate: new DateOnly(2026, 9, 24),
                Purchaser: "Buyer",
                Seller: "Merchant",
                Amount: 100m,
                TaxAmount: 13m,
                TotalAmount: 113m,
                InvoiceCode: "code",
                InvoiceNumber: "number",
                DocumentType: InvoiceDocumentType.RideInvoice,
                Category: null,
                Route: null,
                Items: Array.Empty<InvoiceItem>(),
                SourceFileName: "invoice.pdf",
                ContentHash: Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("invoice bytes")))
                    .ToLowerInvariant());
            var result = new CandidateProcessResult(
                new DocumentCandidate(
                    DocumentIdentity.Create("doc-live-resolved"),
                    Sequence: 0,
                    CorrelationId: "corr",
                    SourceMessageUid: "uid",
                    OriginalFileName: "invoice.pdf",
                    ContentType: "application/pdf",
                    ContentLength: 13,
                    ProcessingRevision: 0,
                    SourceKind: "attachment"),
                CandidateStatus.Resolved,
                invoice,
                ArtifactPath: source);

            var batch = await stage.ExecuteAsync(
                new ArchiveStageRequest(runId, outputRoot, new PairingBatch([], [result])),
                CancellationToken.None);

            batch.Artifacts.Should().ContainSingle()
                .Which.State.Should().Be(ArchiveArtifactState.Committed);

            await using var verify = _fixture.CreateContext();
            var processing = await verify.DocumentProcessing.AsNoTracking()
                .SingleAsync(row => row.DocumentId == "doc-live-resolved", TestContext.Current.CancellationToken);
            processing.Status.Should().Be("Resolved");

            (await verify.ArchivedArtifacts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

            var persisted = await verify.Invoices.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            persisted.InvoiceId.Should().Be("doc-live-resolved");
            persisted.TotalAmount.Should().Be("113");
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    private static DocumentArchivingStage CreateStage(InvoiceFlowDbContext context)
    {
        var uowFactory = new EfUnitOfWorkFactory(context);
        var fileSystem = new PhysicalArchiveFileSystem();
        return new DocumentArchivingStage(
            new ArchiveNamingPolicy(),
            new ArchiveCommitCoordinator(
                uowFactory,
                new EfArchiveArtifactStore(context),
                fileSystem,
                new EfAuditStore(context)),
            fileSystem,
            new EfPairingStore(context),
            new EfManualReviewItemStore(context),
            uowFactory,
            cwtCancellationFinalizer: null,
            processingStore: new EfDocumentProcessingStore(context),
            invoiceStore: new EfInvoiceStore(context));
    }

    private static string CreateSource(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"invoice-flow-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        return path;
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`
Expected: 编译失败(构造参数不存在);若先补构造函数但未实现持久化,测试将以 `ARCHIVE_REVIEW_FAILED` / 外键失败告终——这就是活体故障的回归测试。

- [ ] **Step 3: 实现**

3a. 字段与构造函数——替换 `DocumentArchivingStage.cs` 第 13-37 行:

```csharp
    private readonly IArchiveNamingPolicy _namingPolicy;
    private readonly IArchiveCommitCoordinator _coordinator;
    private readonly IArchiveFileSystem _fileSystem;
    private readonly IPairingStore _pairingStore;
    private readonly IManualReviewItemStore _reviewStore;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICwtCancellationFinalizer? _cwtCancellationFinalizer;
    private readonly IDocumentProcessingStore? _processingStore;
    private readonly IInvoiceStore? _invoiceStore;

    public DocumentArchivingStage(
        IArchiveNamingPolicy namingPolicy,
        IArchiveCommitCoordinator coordinator,
        IArchiveFileSystem fileSystem,
        IPairingStore pairingStore,
        IManualReviewItemStore reviewStore,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICwtCancellationFinalizer? cwtCancellationFinalizer = null,
        IDocumentProcessingStore? processingStore = null,
        IInvoiceStore? invoiceStore = null)
    {
        _namingPolicy = namingPolicy ?? throw new ArgumentNullException(nameof(namingPolicy));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
        _reviewStore = reviewStore ?? throw new ArgumentNullException(nameof(reviewStore));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _cwtCancellationFinalizer = cwtCancellationFinalizer;
        _processingStore = processingStore;
        _invoiceStore = invoiceStore;
    }
```

3b. `ExecuteAsync` 开头——把第 65-73 行(`var results`、`candidateIndexes`、`cwtCancellationIds` 三段)替换为:

```csharp
        var results = request.Batch.CandidateResults.ToArray();
        var cwtCancellationIds = results
            .Where(IsCwtCancellation)
            .Select(static result => result.Candidate.DocumentId.Value)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < results.Length; index++)
        {
            if (cwtCancellationIds.Contains(results[index].Candidate.DocumentId.Value))
            {
                results[index] = ToCwtReviewResult(results[index]);
            }
        }

        await PersistTerminalStatesAsync(request, results, cancellationToken).ConfigureAwait(false);

        var candidateIndexes = results
            .Select((result, index) => (Id: result.Candidate.DocumentId.Value, Index: index))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
```

3c. CWT 分支(原第 85-100 行)改为(终态转换已在上面完成):

```csharp
            if (cwtCancellationIds.Contains(documentId))
            {
                await ProcessReviewAsync(index, results[index], request, outputRoot, results, outcomes, cancellationToken).ConfigureAwait(false);
                continue;
            }
```

3d. `ExecuteAsync` 结尾——在 `return new ArchiveBatch(results, failures) { Artifacts = outcomes };` 之前插入:

```csharp
        await RefreshTerminalStatesAsync(request, results, cancellationToken).ConfigureAwait(false);
```

3e. 在 `ProcessReviewAsync` 方法之前新增:

```csharp
    private static CandidateProcessResult ToCwtReviewResult(CandidateProcessResult result) =>
        result with
        {
            Status = CandidateStatus.ManualReview,
            Failure = new CandidateFailure(
                "CWT_HOTEL_CANCELLATION",
                FailureScope.Candidate,
                FailureCategory.Validation,
                Retryable: false,
                SafeMessage: "A CWT hotel cancellation notice requires manual review."),
        };

    private async Task PersistTerminalStatesAsync(
        ArchiveStageRequest request,
        IReadOnlyList<CandidateProcessResult> results,
        CancellationToken cancellationToken)
    {
        if (_processingStore is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await _unitOfWorkFactory
            .BeginAsync(TransactionPurpose.CandidateCommit, cancellationToken)
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentId = result.Candidate.DocumentId.Value;
            await _processingStore.UpsertAsync(
                new DocumentProcessingWrite(
                    DocumentId: documentId,
                    ProcessingRevision: result.Candidate.ProcessingRevision,
                    RunId: request.RunId,
                    SourceKind: result.Candidate.SourceKind,
                    SourceMessageUid: result.Candidate.SourceMessageUid,
                    SourceFileName: result.Candidate.OriginalFileName,
                    Stage: "archive-documents",
                    Status: result.Status,
                    ReasonCode: result.Failure?.ReasonCode ?? string.Empty,
                    Retryable: result.Failure?.Retryable ?? false,
                    Attempt: 1,
                    MaxAttempts: 1,
                    ArtifactPath: string.IsNullOrWhiteSpace(result.ArtifactPath) ? null : result.ArtifactPath,
                    ResultJson: $"{{\"documentId\":\"{documentId}\",\"status\":\"{result.Status}\"}}",
                    TraceJson: null,
                    StartedAtUtc: now,
                    CompletedAtUtc: now),
                transaction,
                cancellationToken).ConfigureAwait(false);

            if (_invoiceStore is null || result.Invoice is null)
            {
                continue;
            }

            var invoice = result.Invoice;
            if (!string.Equals(invoice.DocumentId, documentId, StringComparison.Ordinal))
            {
                invoice = invoice with { DocumentId = documentId, Identity = new DocumentIdentity(documentId) };
            }

            await _invoiceStore.UpsertAsync(
                invoice,
                result.Candidate.ProcessingRevision,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshTerminalStatesAsync(
        ArchiveStageRequest request,
        IReadOnlyList<CandidateProcessResult> results,
        CancellationToken cancellationToken)
    {
        if (_processingStore is null)
        {
            return;
        }

        try
        {
            await PersistTerminalStatesAsync(request, results, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The pre-dispatch write already satisfied every foreign key.
            // A refresh failure leaves those rows intact and must not discard
            // work that is already archived.
        }
    }
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj`
Expected: PASS,包括既有归档/配对/URL 恢复集成测试(它们不传新参数或自行预置 processing 行,行为不变)。

---

### Task 4: 全量回归 + 活体端到端复跑

**Files:**
- Test: 全部 6 个测试项目(无文件改动)

**Interfaces:**
- Consumes: Task 1-3 的实现;`e:\tmp\livepipe`(此前活体 harness,需重新 build 以引用最新程序集)。
- Produces: 全绿测试结果、活体运行最终统计。

- [x] **Step 1: 跑全量测试**

Run(每个项目单独执行,禁止 `--nologo`):

```bash
dotnet test tests/InvoiceFlowAI.Domain.Tests/InvoiceFlowAI.Domain.Tests.csproj
dotnet test tests/InvoiceFlowAI.Contracts.Tests/InvoiceFlowAI.Contracts.Tests.csproj
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj
dotnet test tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/InvoiceFlowAI.UrlRecovery.Worker.Tests.csproj
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj
```

Expected: 原有 849 个测试 + 新增测试全部通过。

- [x] **Step 2: 重建 worker 与活体 harness**

```bash
dotnet build src/InvoiceFlowAI.UrlRecovery.Worker/InvoiceFlowAI.UrlRecovery.Worker.csproj -c Debug
dotnet build e:/tmp/livepipe/livepipe.csproj -c Debug
```

然后把 worker 输出目录中的文件复制到 harness 输出目录(保持 native 资产一致)。

- [x] **Step 3: 活体复跑**

Run:

```bash
e:/tmp/livepipe/bin/Debug/net10.0/InvoiceFlowLivePipe.exe E:/GitHub/Invoice-Downloader/src/InvoiceFlowAI.UrlRecovery.Worker/bin/Debug/net10.0/InvoiceFlowAI.UrlRecovery.Worker.exe
```

Expected(与失败运行对比):
- 不再出现 `ARCHIVE_REVIEW_FAILED`;
- 运行在 25 分钟守护内到达 `Completed`(或至少 Candidate 全部提交并 finalize);
- 数据库 `DocumentProcessing > 0`、`ManualReviewItems > 0`(未恢复链接进入复核)、`ArchivedArtifacts > 0`;
- 输出目录出现 `archive/<runId>/...` 与报表文件。

- [x] **Step 4: 汇总证据**

查询 SQLite 统计并汇报:`DocumentProcessing` / `Invoices` / `ManualReviewItems` / `ArchivedArtifacts` 行数、运行终态、输出目录文件数。

- [x] **Step 5: 活体结果与计划偏差**

实测(2026-10-23):

- 全量测试 854 通过(原 849 + 持久化 4 + 配对去重 1)。
- `run-f0782857279e41c18e8768a76a8c47ff`:持久化写入生效——`DocumentProcessing` 985、`ManualReviewItems` 984、`Documents` 985,外键失败消失;但 985 个候选全部是 URL 恢复失败终态,没有可解析发票,因此 `Invoices` / `ArchivedArtifacts` 为 0。该运行在收尾重放阶段被 harness 25 分钟守护截断,未 finalize。
- 重放缓慢的根因不在本计划范围:`DocumentExtractionStage` 将终态结果同时放入 `Results` 与 `PreflightResults`,`ArtifactPairingStage` 又拼接两者,导致每个终态候选在 `ArchivedBatch` 中重复一次(1962 条结果 / 985 个文档)。已按 TDD 修复为按 `(DocumentId, ProcessingRevision)` 去重,并新增 `Pairing_deduplicates_preflight_results_already_present_in_extraction_results`。
- `run-082cbf53e80d499383523fcb57bef5fc`(守护提高到 40 分钟):984 个候选被历史去重跳过(`HISTORY_DUPLICATE_SKIP`)、1 个新候选进入复核(`GENERIC_URL_NO_VALID_ARTIFACT`),985 条候选结果无重复;运行在 2 分 49 秒内到达终态 `NeedsManualReview` / `RUN_NEEDS_MANUAL_REVIEW`(客户端映射为 `RunState.Completed`),报表已生成。
- 终态为 `NeedsManualReview` 而非 `Completed` 是 `TerminalDecisionService` 的既定优先级(存在待复核项),不是失败。

---

## Self-Review

- **Spec coverage:** 补齐 `DocumentProcessing` 写入(幂等 upsert、`(RunId, Sequence)` 唯一)、`Invoices` / `InvoiceItems`、事务边界(processing/invoice 同一 `CandidateCommit` 事务,归档文件仍沿用既有两阶段)、外键顺序(`Documents → DocumentProcessing → Invoices → InvoiceItems` 的分段 flush)。`ManualReviewItems` 与 `ArchivedArtifacts` 的 FK 失败被消除。
- **Discoveries folded in:** (1) EF 模型没有关系映射,同批插入不保证顺序——活体探针已实测;两个 store 在事务内显式 `SaveChangesAsync` 解决。(2) `Sequence` 的 `MAX+1` 必须能看到同一事务内已写入的行,因此每次 upsert 末尾 flush。(3) CWT 取消终态转换提前到持久化之前,避免 DB 记录与返回结果不一致。(4) 分发结束后刷新一次终态,使归档失败/CWT 终局原因落入 DB;刷新失败不丢弃已完成归档。
- **Placeholder scan:** 无 TBD/TODO;每个代码步骤均给出完整实现。
- **Type consistency:** `DocumentProcessingWrite` 在 Task 1 定义,Task 2/3 使用的字段签名一致;`IInvoiceStore.UpsertAsync(InvoiceDocument, int, IUnitOfWork, CancellationToken)` 在 Task 2/3 一致;`candidate.ProcessingRevision`(int)贯穿;构造参数 `processingStore` / `invoiceStore` 在 Task 3 的测试与 DI 注册中同名。
- **Known deviations:** 归档文件写入仍由 `ArchiveCommitCoordinator` 的独立事务完成,未与 processing/invoice 行合并为单事务(设计本身采用两阶段归档,可接受)。`Invoices.ArchiveState` 保持 `"Pending"`,由报表侧依据 `ArchivedArtifacts` 相对路径展示 `"Committed"`,与现有读取逻辑一致。
