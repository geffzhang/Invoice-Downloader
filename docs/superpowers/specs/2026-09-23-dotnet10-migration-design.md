# InvoiceFlowAI .NET 10 迁移设计

## 1. 目标与范围

将 InvoiceFlowAI 重写为面向 Windows 11 的 .NET 10 桌面应用。新应用使用 WinUI 3 作为原生窗口外壳，使用 WebView2 承载现有 HTML/JavaScript 用户界面。所有运行时业务逻辑均使用 C# 实现，发布后的应用不依赖 Python。

迁移目标是完整功能对等，包括：

- IMAP 邮箱扫描和发票候选筛选；
- PDF、OFD、XML 发票处理；
- 使用 `Sdcb.SimdPaddleOCR` 的本地 OCR；
- DeepSeek Flash 文本提取和多模态提取；
- 发票与凭证配对；
- 发票链接恢复；
- 归档命名和分类规则；
- Excel 报表和人工复核输出；
- 运行生命周期、真值/审计证据、诊断、重试、取消和进度事件；
- 现有 HTML/JavaScript 交互模型，并将其接入 C# JSON/RPC 后端。

首个版本只支持 Windows 11 x64。不要求兼容旧配置格式和旧运行状态格式。输出行为可以重新设计，但功能结果必须保持等价。

## 2. 迁移策略

采用带稳定前后端契约的垂直业务切片迁移。Python 实现仅作为行为参考和测试样本来源，不作为新应用的运行时依赖。

迁移顺序如下：

1. WinUI 3/WebView2 外壳和 JSON/RPC 桥接层；
2. ZeroPipeline DAG、领域契约、运行生命周期、取消、进度和诊断；
3. XML、OFD、PDF、本地 OCR、字段提取、校验和归档的本地文件节点；
4. IMAP 扫描和候选筛选节点；
5. 通过 `IChatClient` 接入 DeepSeek Flash 文本和多模态提取节点；
6. 链接恢复和供应商专用节点；
7. 配对、高级规则、报表、真值审计和发布加固。

每个切片都必须先具备可执行的测试面，再扩展到下一个切片。

## 3. 解决方案架构

```text
src/
  InvoiceFlowAI.App/
    App.xaml
    MainWindow.xaml
    WebView/
    Rpc/
  InvoiceFlowAI.Application/
    Runs/
    Pipeline/
    Commands/
    Events/
    Services/
  InvoiceFlowAI.Domain/
    Invoices/
    Candidates/
    Pairing/
    Rules/
    Truth/
  InvoiceFlowAI.Infrastructure/
    Mail/
    Ocr/
    Ai/
    Documents/
    Browser/
    Archive/
    Reports/
    Persistence/
    Security/
    Logging/
  InvoiceFlowAI.Contracts/
    Rpc/
    Models/
    Errors/

tests/
  InvoiceFlowAI.Domain.Tests/
  InvoiceFlowAI.Application.Tests/
  InvoiceFlowAI.Infrastructure.Tests/
  InvoiceFlowAI.App.Tests/
```

应用层引用 ZeroPipeline 的核心编排包：

- `ZeroPipeline.Core` `1.2.0`：DAG、拓扑调度、typed ports、背压和执行器；
- `ZeroPipeline.Recipe`：JSON Recipe 序列化、节点注册和图构建。

```xml
<PackageReference Include="ZeroPipeline.Core" Version="1.2.0" />
```

`ZeroPipeline.Recipe` 必须选择与 `ZeroPipeline.Core 1.2.0` 兼容的同系列版本，并在解决方案锁文件中固定；不能使用浮动版本。

IMAP 处理固定使用以下包：

```xml
<PackageReference Include="MailKit" Version="4.18.0" />
```

本地持久化使用 EF Core SQLite：

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.*" />
```

PDF 页面渲染固定使用 PDFiumCore：

```xml
<PackageReference Include="PDFiumCore" Version="155.0.8057" />
<PackageReference Include="PdfPig" Version="0.1.17-alpha-202609192350-df33d" />
<PackageReference Include="SkiaSharp" Version="4.154.0-preview.1.26454.9" />
<PackageReference Include="Sdcb.SimdPaddleOCR" Version="1.4.2" />
```

WebView2 使用指定的预览版本：

```xml
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.4255-prerelease" />
```

DeepSeek 的统一 AI 适配器固定使用：

```xml
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.10.0" />
```

其传递依赖的 `Microsoft.Extensions.AI`、OpenAI 客户端和相关 `10.x` 包必须通过锁文件固定，避免预览版或浮动依赖改变多模态消息序列化结果。

ZeroPipeline 参考仓库：<https://github.com/kzxl/ZeroPipeline/tree/master>。
其 `1.2.0` 核心包支持 `net8.0` 和 `netstandard2.0`，可由 .NET 10 应用引用。暂不使用 `ZeroPipeline.UI`，因为本项目的界面由 WinUI 3 + WebView2 承载，不能把 WinForms 画布控件作为 UI 基础。

### 职责边界

- `InvoiceFlowAI.App`：WinUI 窗口、WebView2 初始化、生命周期、DPI、打包和桥接接线。不包含发票业务规则。
- `InvoiceFlowAI.Contracts`：可 JSON 序列化的命令、事件、DTO、稳定错误码和前端契约。
- `InvoiceFlowAI.Application`：使用 ZeroPipeline 构建运行 DAG，负责准入校验、取消、重试策略、阶段转换、并发限制和进度发送。
- `InvoiceFlowAI.Domain`：发票实体、解析结果、分类规则、配对规则、校验和真值契约。该层不依赖 WebView2、HTTP、数据库或供应商 SDK。
- `InvoiceFlowAI.Infrastructure`：IMAP、文档、OCR、AI、浏览器、归档、报表、持久化、凭据和日志等具体实现。

`Microsoft.Web.WebView2` 只由 `InvoiceFlowAI.App` 引用。WebView2 负责加载随应用发布的 HTML/JavaScript 资源，并通过宿主桥接接入 JSON/RPC；页面脚本不能直接访问文件系统、DPAPI、数据库或外部 API。

整体分层借鉴 `E:/GitHub/qingpiao/src/QingPiao` 中 `Services/Parsers/Exporters` 的职责拆分，同时使用接口替代具体依赖，以适配桌面端编排和自动化测试。

ZeroPipeline 只位于应用编排层。领域层不依赖 ZeroPipeline；节点通过应用层定义的端口 DTO 与领域服务通信，避免将 DAG 类型扩散到业务实体中。

## 4. WebView2 契约

前端通过 JSON/RPC 消息与后端通信。每个请求都有请求 ID；长时间运行的操作还拥有运行 ID。

示例命令：

```json
{
  "id": "request-1",
  "method": "run.start",
  "params": {
    "dateFrom": "2026-09-01",
    "dateTo": "2026-09-23",
    "savePath": "C:/Invoices"
  }
}
```

进度和结束事件采用结构化、版本化格式：

- `run.stageChanged`
- `run.progress`
- `run.documentResult`
- `run.failed`
- `run.completed`
- `run.cancelled`

桥接层负责传输、请求关联、序列化和事件转发，不负责邮箱扫描或发票解析。

## 5. ZeroPipeline 编排与运行生命周期

由 `RunCoordinator` 创建运行上下文并构建 ZeroPipeline 图，由 `PipelineExecutor` 执行图。每次 `run.start` 都创建独立的图实例和运行上下文，不共享带状态的节点。

推荐的节点图如下：

```text
ValidateRequest
  -> ScanMailbox
  -> CollectCandidates
  -> RecoverUrls
  -> ExtractDocuments
  -> PairArtifacts
  -> ArchiveDocuments
  -> ExportReport
```

节点之间使用 typed ports 传递候选、文档、解析结果和报表数据。候选级处理使用有界队列和背压，避免邮箱附件、OCR 页面或 AI 请求无限制堆积。单文件失败作为结果端口输出并进入人工复核分支，不直接使整张 DAG 失败。

运行生命周期仍保持以下状态：

```text
Created
  -> Validating
  -> ScanningMailbox
  -> CollectingCandidates
  -> RecoveringUrls
  -> ExtractingDocuments
  -> PairingArtifacts
  -> Archiving
  -> ExportingReport
  -> Completed
```

运行也可能以 `Cancelled`、`Failed`、`PartialSuccess` 或 `NeedsManualReview` 结束。单个损坏文档不能中断整批任务；只有凭据无效、存储不可用或无法恢复的持久化错误等运行级错误才终止整次运行。

每个阶段都接收 `CancellationToken`，写入审计事件，并发送安全的进度事件。凭据、原始授权值和未脱敏的敏感 URL 不能出现在事件或日志中。

### Serilog 结构化日志

运行日志使用 Serilog，通过 `Microsoft.Extensions.Logging` 的 `ILogger<T>` 注入到应用层和基础设施层。日志输出为结构化事件，不在业务代码中拼接长字符串。

每条日志尽量包含以下公共字段：

- `Timestamp`、`Level`、`MessageTemplate`；
- `Application`、`Version`、`ProcessId`；
- `RunId`、`Stage`、`NodeId`、`DocumentId`；
- `EventType`、`ReasonCode`、`Retryable`、`DurationMs`；
- `MachineName` 和脱敏后的外部服务域名。

ZeroPipeline 节点在执行开始、成功、失败、重试和取消时写入结构化事件。WebView2 桥接记录 RPC 方法名、请求 ID、运行 ID 和耗时，但不记录参数中的凭据、原始 OCR 文本或图片内容。DeepSeek 调用只记录模型名、输入类型、token/耗时元数据和结果状态，不记录 API Key、完整提示词、发票图像或完整发票文本。OFD 解析只记录来源 XML 路径、解析分支和结果状态，不记录完整 XML。

日志与审计分离：Serilog 日志用于诊断和运行观测；真值审计事件使用独立的 `IAuditStore` 持久化，并以稳定 schema 保存。日志可以按保留策略滚动清理，不能替代审计证据。

默认写入 `%LocalAppData%/InvoiceFlowAI/logs/invoiceflowai-.log`，按日期滚动并限制文件大小和保留数量。发布版默认记录 `Information` 及以上级别；调试模式可以提升到 `Debug`，但仍必须执行相同的脱敏策略。未配置可用的日志文件目录时，应用回退到安全的诊断目录，并将失败写入启动诊断信息。

### ZeroPipeline 适配边界

- `RunCoordinator`：创建/销毁图实例、绑定运行 ID、接收取消请求、汇总节点状态并向 WebView2 发事件；
- `PipelineGraph`：描述阶段依赖和数据流，不承载用户凭据或持久化状态；
- `PipelineExecutor`：执行拓扑调度、节点并发和背压；
- 应用节点：将 `IMailboxScanner`、`IInvoiceParser`、`IInvoiceOcr`、`IInvoiceFieldExtractor`、`IArchiveService` 和 `IReportExporter` 适配为 ZeroPipeline 节点；
- `ZeroPipeline.Recipe`：保存可诊断的流程版本和节点元数据，不保存授权码、API Key 或原始发票内容。

ZeroPipeline 的 DAG 调度负责节点依赖、队列和执行顺序；业务重试、错误码、审计写入和人工复核规则仍由应用层控制，避免把业务语义隐藏在通用编排器中。

### ZeroPipeline 节点输入输出契约

ZeroPipeline `1.2.0` 的执行模型是：`IPipelineNode.ExecuteAsync` 每次执行一个 cycle；节点从 `InputPort<T>` 取出一个 `DataPacket<T>`，处理后通过 `OutputPort<T>` 发布；`DataPacket<T>` 携带 `SequenceNumber`、时间戳和 `IsEndOfStream`。输入端口是有界队列，首版业务端口统一使用 `BackpressurePolicy.Block`，禁止使用 `DropOldest` 或 `DropNewest` 丢失发票数据。

因此，业务层不把 ZeroPipeline 的 `PipelineContext` 字典当作数据通道，也不把一个大批次藏在 context 中。`PipelineContext` 只保存本次运行的只读运行标识、配置快照、取消令牌和基础设施服务引用；候选、文档、发票和失败结果全部通过强类型端口传递。

节点使用以下通用包装类型承载运行和候选关联信息：

```csharp
public sealed record PipelineItem<T>(
  string RunId,
  long Sequence,
  string CorrelationId,
  T? Value,
  CandidateFailure? Failure = null);

public sealed record RunInput(
  string RunId,
  DateOnly DateFrom,
  DateOnly DateTo,
  string SavePath,
  string CustomRules,
  string AccountId,
  string Mailbox = "INBOX",
  string RunMode = "interactive");

public sealed record ValidatedRunInput(
  RunInput Request,
  string StagingDirectory,
  string ConfigurationFingerprint);

public sealed record MailboxMessageBatch(
  string AccountId,
  string Mailbox,
  long BatchSequence,
  IReadOnlyList<MailboxMessage> Messages,
  bool IsFinalBatch,
  int ScannedCount);

public sealed record CandidateBatch(
  IReadOnlyList<DocumentCandidate> Candidates,
  int SourceMessageCount,
  bool IsFinalBatch,
  IReadOnlyList<CandidateProcessResult>? TerminalResults = null);

public sealed record ExtractionBatch(
  IReadOnlyList<CandidateProcessResult> Results,
  bool IsFinalBatch);
```

`PipelineItem<T>.Sequence` 是业务顺序，必须与外层 `DataPacket<T>.SequenceNumber` 保持一致。允许节点在内部有限并发处理，但输出由 `SequenceReorderBuffer` 按 sequence 释放；下游永远收到确定顺序的结果。一个节点不能依赖执行线程顺序，也不能把 `DataPacket` 实例写入数据库。

节点图的端口契约固定如下：

| 节点 | 输入端口 | 输出端口 | 处理规则 |
| --- | --- | --- | --- |
| `ValidateRequestNode` | `Input<RunInput>` | `Valid<ValidatedRunInput>`、`Failure<RunFailure>` | 参数、路径、日期范围、凭据引用和配置指纹校验；失败是运行级结果，不启动后续节点。 |
| `ScanMailboxNode` | `Input<ValidatedRunInput>` | `Messages<PipelineItem<MailboxMessageBatch>>`、`Failure<RunFailure>` | 按页读取 IMAP；每页一个 packet，结束时发送 EOF；单封邮件损坏转候选前置失败，不中断扫描。 |
| `CollectCandidatesNode` | `Messages<PipelineItem<MailboxMessageBatch>>` | `Candidates<PipelineItem<CandidateBatch>>`、`Failure<RunFailure>` | 过滤发票候选、递归展开附件和嵌套 ZIP，生成不可变 candidate；保持 source message 和 candidate sequence。 |
| `RecoverUrlsNode` | `Candidates<PipelineItem<CandidateBatch>>` | `Candidates<PipelineItem<CandidateBatch>>`、`Failure<RunFailure>` | URL 候选逐项恢复；成功候选保留在 `Candidates`，失败候选写入同一批次的 `TerminalResults`，不抛批次异常。 |
| `ExtractDocumentsNode` | `Candidates<PipelineItem<CandidateBatch>>` | `Results<PipelineItem<ExtractionBatch>>`、`Failure<RunFailure>` | XML/OFD/PDF/OCR/DeepSeek 处理；输入批次中的既有终态结果原样转发，尚未终态的 candidate 必须补充一个 `CandidateProcessResult`。 |
| `PairArtifactsNode` | `Results<PipelineItem<ExtractionBatch>>` | `Pairs<PipelineItem<PairingBatch>>`、`Failure<RunFailure>` | 只消费已终态结果；配对冲突和无法配对更新为人工复核结果，既有失败结果原样转发，不使整次运行失败。 |
| `ArchiveDocumentsNode` | `Pairs<PipelineItem<PairingBatch>>` | `Archived<PipelineItem<ArchiveBatch>>`、`Failure<RunFailure>` | 归档操作必须幂等；单文件路径、命名或移动失败追加/更新候选结果，最终统一进入 `ArchiveBatch.Results`。 |
| `ExportReportNode` | `Archived<PipelineItem<ArchiveBatch>>` | `Completed<RunSummary>`、`Failure<RunFailure>` | 汇总所有候选终态并生成报表；报表写入、数据库提交或审计提交失败属于运行级故障。 |

`Messages`、`Candidates`、`Results` 等批次 DTO 只用于减少端口连接数量，不改变候选级语义；批次内部仍按 `DocumentCandidate.Sequence` 排序，且不得无限扩大。`CandidateBatch.TerminalResults` 用于恢复阶段提前失败的 candidate；后续节点必须将这些结果转发到唯一的结果端口，不得另建旁路失败端口。默认批次大小、端口容量和节点并发由 `PipelineOptions` 固定配置，并在启动日志中记录。

### 失败语义与异常边界

ZeroPipeline 的 `PipelineNode.ExecuteAsync` 在异常后会把节点置为 `Faulted`，`PipelineExecutor` 也会触发 `NodeFaulted` 并抛出异常。因此应用节点遵守以下边界：

1. **候选级失败不抛异常**：文件损坏、格式不支持、号码缺失、OCR 低质量、DeepSeek 非法 JSON、URL 恢复失败、配对冲突、归档单文件失败，都转换为 `CandidateProcessResult`，写入当前阶段唯一的结果端口，并继续处理其他候选。候选失败不得通过独立旁路端口绕过后续汇总。
2. **可重试候选失败不在节点内部无限重试**：节点根据 `RetryPolicy` 产生带 `Retryable=true` 的结果；应用层 `RetryCoordinator` 根据错误码、尝试次数和退避计划重新投递同一个 candidate，保持原 `DocumentId`、`Sequence` 和幂等键。
3. **节点级故障才抛异常**：端口类型错误、节点配置非法、依赖初始化失败、不可恢复的内部不变量破坏、无法创建临时目录等，抛出带稳定 `ReasonCode` 的 `PipelineNodeException`，由 `RunCoordinator` 将运行置为 `Failed`。
4. **运行级故障终止 DAG**：凭据认证失败、数据库迁移/提交失败、日志和审计存储不可用、输出根目录不可写、ZeroPipeline 图校验失败，写入 `RunFailure` 并停止新的输入；已完成的候选结果保留。
5. **取消不是失败**：收到 `CancellationToken` 后，节点停止读取新 packet；已取出的候选按 `CANCELLED` 产生终态结果，未取出的输入由上游清空或发送 EOF，运行最终为 `Cancelled`。不得把 `OperationCanceledException` 记录为普通错误或自动重试。
6. **EOF 必须传播**：源节点对每个输出端口发送一次 `DataPacket<T>.EndOfStream(sequence)`；转换节点收到 EOF 后只传播 EOF，不再消费业务数据；汇聚节点在所有上游 EOF 后输出最终汇总。

候选级结果使用以下稳定模型，不用异常文本驱动前端：

```csharp
public enum CandidateStatus
{
  Resolved,
  Duplicate,
  Retained,
  ManualReview,
  Unresolved,
  Cancelled,
  QuotaExhausted,
  AuthFailed,
  Timeout
}

public enum FailureScope
{
  Candidate,
  Node,
  Run
}

public enum FailureCategory
{
  Input,
  Document,
  Network,
  Authentication,
  Quota,
  Persistence,
  Cancellation,
  Validation,
  Internal
}

public sealed record CandidateFailure(
  string ReasonCode,
  FailureScope Scope,
  FailureCategory Category,
  bool Retryable,
  string SafeMessage,
  int Attempt = 0,
  int MaxAttempts = 0,
  string ExceptionType = "",
  string Fingerprint = "");

public sealed record CandidateProcessResult(
  DocumentCandidate Candidate,
  CandidateStatus Status,
  InvoiceDocument? Invoice = null,
  string ArtifactPath = "",
  CandidateFailure? Failure = null,
  IReadOnlyList<string>? Warnings = null,
  IReadOnlyDictionary<string, string>? Trace = null);

public sealed record RunFailure(
  string RunId,
  string Stage,
  string ReasonCode,
  FailureCategory Category,
  bool Retryable,
  string SafeMessage,
  string ExceptionType = "",
  string Fingerprint = "");
```

`CandidateProcessResult` 必须满足：同一个 `DocumentId` 在一次运行中最多产生一个最终结果；重复投递时由 `DocumentId + processing revision` 幂等去重；`ManualReview`、`Unresolved`、`Retained` 和 `Cancelled` 都是已终态，不得被下游重新解释为成功。每个阶段只能有一个候选结果输出端口；下游节点对已终态结果只允许转发、补充阶段 trace 或按明确规则更新状态，不得复制成第二个结果。`RunSummary` 统计各状态数量、阶段耗时、失败原因计数、报表路径和审计提交状态，但不包含 OCR 原文、图片、凭据或完整邮件正文。

`RunCoordinator` 负责将 ZeroPipeline 节点事件映射到运行状态：`NodeExecuting` 更新阶段开始，`NodeCompleted` 更新阶段耗时，`NodeFaulted` 只处理节点/运行级异常；候选级失败只能由结果端口汇总。这样可以保留 Python `ExtractionOutcome` 的候选级隔离、`RunLifecycle` 的运行级失败和取消语义，同时适配 ZeroPipeline 的异常模型。

### 核心领域 DTO

以下类型位于 `InvoiceFlowAI.Domain`，是 parser、候选流水线、配对、归档、审计和持久化之间的唯一业务数据契约。它们使用不可变 `record`，不引用 MailKit、PdfPig、PDFiumCore、SkiaSharp、WebView2、EF Core 或 ZeroPipeline 类型；JSON/RPC 和数据库分别使用 Contracts/Infrastructure 的映射 DTO，不能反向污染领域模型。

#### 来源身份与候选

```csharp
public enum DocumentSourceKind
{
  Attachment,
  Url,
  LocalFile,
  EmailBody,
  Generated
}

public sealed record DocumentIdentity(
  string DocumentId,
  string SourceMessageUid = "",
  string SourceFileName = "",
  string SourceLocator = "",
  DocumentSourceKind SourceKind = DocumentSourceKind.Attachment,
  string ProviderGroupKey = "");

public sealed record DocumentSource(
  DocumentIdentity Identity,
  string LocalPath = "",
  string SourceUrl = "",
  string MimeType = "",
  string ContentHash = "",
  string AttachmentPartId = "",
  string Mailbox = "",
  string Subject = "",
  string Sender = "");

public sealed record MailboxMessage(
  string AccountId,
  string Mailbox,
  string MessageUid,
  string MessageId,
  DateTimeOffset ReceivedAt,
  string Subject,
  string Sender,
  IReadOnlyList<MailAttachment> Attachments,
  IReadOnlyDictionary<string, string>? Headers = null);

public sealed record MailAttachment(
  string PartId,
  string FileName,
  string MimeType,
  long Size,
  string LocalPath = "",
  string ContentHash = "");

public sealed record DocumentCandidate(
  DocumentIdentity Identity,
  int Sequence,
  DocumentSource Source,
  string Channel = "",
  string SourceFileName = "",
  string CompatibilityHistoryKey = "",
  string ProviderFamily = "",
  bool IsParallelSafe = true,
  IReadOnlyDictionary<string, string>? Hints = null);
```

`DocumentId` 必须在 candidate 收集阶段生成并在整次运行内保持不变：本地附件优先使用内容 SHA-256，URL 优先使用规范化 URL 的 SHA-256，无法计算时使用包含邮件 UID、附件 part、文件名和 sequence 的稳定降级键。`CompatibilityHistoryKey` 只用于避免重复处理和兼容历史判断，不作为发票号码。`SourceLocator` 保存受控的本地相对定位信息或脱敏 URL 标识，不把原始授权 URL 放进领域事件。

`Sequence` 从 candidate 收集阶段开始分配，按邮箱扫描顺序单调递增；重试、URL 恢复和节点重投递不能改变它。`IsParallelSafe=false` 用于 URL 浏览器恢复、供应商上下文或其他不能并发共享状态的 candidate；应用层并发调度器必须尊重该标记。

#### 发票与明细

```csharp
public enum InvoiceDocumentType
{
  Other,
  Catering,
  TrainTicket,
  Taxi,
  AccommodationInvoice,
  AccommodationStatement,
  FlightTicket,
  Itinerary,
  TravelService,
  NonTargetCompanyInvoice
}

[Flags]
public enum InvoiceFlags
{
  None = 0,
  Itinerary = 1,
  Folio = 2,
  CreditNote = 4,
  Cancellation = 8,
  LowConfidence = 16,
  RequiresManualReview = 32
}

public sealed record InvoiceRoute(
  DateOnly? DepartureDate = null,
  string DepartureCity = "",
  string DestinationCity = "");

public sealed record InvoiceItem(
  string Name = "",
  string Specification = "",
  string Unit = "",
  decimal? Quantity = null,
  decimal? UnitPrice = null,
  decimal? Amount = null,
  decimal? TaxRate = null,
  decimal? TaxAmount = null);

public sealed record InvoiceDocument(
  DocumentIdentity Identity,
  bool IsInvoice = true,
  DateOnly? InvoiceDate = null,
  string Purchaser = "",
  string Seller = "",
  decimal? Amount = null,
  decimal? TaxAmount = null,
  decimal? TotalAmount = null,
  string InvoiceCode = "",
  string InvoiceNumber = "",
  InvoiceDocumentType DocumentType = InvoiceDocumentType.Other,
  string Category = "其他",
  InvoiceRoute? Route = null,
  InvoiceFlags Flags = InvoiceFlags.None,
  IReadOnlyList<InvoiceItem>? Items = null,
  decimal Confidence = 0m,
  string ParserName = "",
  string ExtractionRevision = "");
```

领域不保存模型原始字段名 `Date`、`Type`、`Departure_Date` 等；这些名称只允许出现在 DeepSeek/旧 Python 兼容映射层。`InvoiceNormalizer` 负责：日期按业务时区转为 `DateOnly`；金额使用 `decimal`，禁止 `double`；括号负数和红字票保留负号；`TotalAmount` 优先使用价税合计并校验 `Amount + TaxAmount`；空、未知和非法值统一为 `null` 或空字符串；文档类型必须归一化到 `InvoiceDocumentType`。

`InvoiceDocument` 的 `Identity` 必须与输入 candidate 一致。解析器不得根据模型输出重新生成 `DocumentId`。`Confidence` 是 0 到 1 的本地归一化值，不直接等同于 OCR 或 DeepSeek 的原始置信度；无法比较时使用 0 并设置 `LowConfidence`。`Items` 为空表示未提取明细，不代表发票没有明细；需要人工复核的原因必须通过 `CandidateFailure` 或 `InvoiceFlags.RequiresManualReview` 明确表达。

#### 解析、配对与归档结果

```csharp
public sealed record InvoiceParseResult(
  bool Succeeded,
  InvoiceDocument? Invoice,
  string DocumentId,
  string SourceFileName,
  string ParserName,
  CandidateFailure? Failure = null,
  bool Retryable = false);

public sealed record PairingKey(
  string InvoiceNumber = "",
  string SourceMessageUid = "",
  string Seller = "",
  DateOnly? BusinessDate = null,
  decimal? Amount = null);

public sealed record PairedArtifacts(
  DocumentIdentity InvoiceIdentity,
  IReadOnlyList<DocumentIdentity> CompanionIdentities,
  PairingKey Key,
  decimal Score,
  bool RequiresManualReview,
  string ReasonCode = "");

public sealed record PairingBatch(
  IReadOnlyList<PairedArtifacts> Pairs,
  IReadOnlyList<CandidateProcessResult> Results);

public sealed record ArchivedArtifact(
  DocumentIdentity Identity,
  string Role,
  string RelativePath,
  string FileName,
  string ContentHash,
  bool AlreadyExisted = false);

public sealed record ArchiveBatch(
  IReadOnlyList<ArchivedArtifact> Artifacts,
  IReadOnlyList<CandidateProcessResult> Results);

public sealed record RunSummary(
  string RunId,
  CandidateStatusCounts Counts,
  int ScannedMessageCount,
  int CandidateCount,
  string? ReportPath,
  bool AuditCommitted,
  TimeSpan Duration);

public sealed record CandidateStatusCounts(
  int Resolved,
  int Duplicate,
  int Retained,
  int ManualReview,
  int Unresolved,
  int Cancelled,
  int QuotaExhausted,
  int AuthFailed,
  int Timeout);
```

`InvoiceParseResult` 是 parser 到应用层的局部结果；`CandidateProcessResult` 是贯穿恢复、提取、配对和归档的最终候选结果，二者不能混用。配对只能消费已完成解析的 `InvoiceDocument` 和显式的 companion identity；归档只接受 `RelativePath`，绝不接受由模型或邮件内容直接拼接的绝对路径。`RunSummary` 只在所有输入端收到 EOF、所有候选达到终态且报表/审计策略完成后生成。

上述 DTO 的不变量统一由构造函数或 `DomainValidation` 校验：ID 和 sequence 非空且合法，金额为有限 decimal，日期为有效 `DateOnly`，置信度在 `[0,1]`，负数金额只有在红字/贷项标记或明确业务规则允许时才接受，结果中的 candidate identity 与 invoice identity 必须相同。跨层映射失败必须返回稳定 `DOMAIN_CONTRACT_INVALID`，不能静默丢字段。

## 6. 文档处理与 OFD 解析

文档层参考当前仓库与 `E:/GitHub/qingpiao/src/QingPiao/Parsers` 的实现，采用“统一入口 + 格式专用 parser + 领域归一化”的结构。QingPiao 的 `Invoice`、`InvoiceItem`、`ParseResult` 作为 C# 迁移的直接行为参考；当前 Python 的 `DocumentIdentity`、`InvoiceRecord`、金额/日期归一化和文档类型标记作为领域层约束参考。

### 统一接口

```csharp
public interface IInvoiceParser
{
    bool CanParse(DocumentSource source);

    Task<IReadOnlyList<InvoiceParseResult>> ParseAsync(
        DocumentSource source,
        ParserContext context,
        CancellationToken cancellationToken);
}
```

`ParserContext` 是应用层创建的本次解析只读上下文，不是领域实体，也不进入 JSON/RPC 或数据库：

```csharp
public sealed record ParserContext(
    string RunId,
    string StagingDirectory,
    bool AllowOcrFallback,
    bool AllowVisionFallback,
    string CustomRules,
    string ConfigurationFingerprint);
```

实现由 `InvoiceParserDispatcher` 按文件扩展名、MIME、文件魔数和文档来源选择：

```text
InvoiceParserDispatcher
  -> XmlInvoiceParser
  -> OfdInvoiceParser
  -> PdfInvoiceParser
  -> ManualReviewResult
```

`InvoiceParseResult` 已在本节前的领域 DTO 中定义，同时保留 QingPiao 风格的成功/人工处理结果和当前 Python 的稳定诊断信息。`InvoiceDocument` 包含发票主数据、`InvoiceItem` 明细、`DocumentIdentity`、来源身份、归一化金额/日期、文档类型和置信度；审计元数据由应用层事件单独保存。解析器不能直接写数据库、归档文件或 WebView2 事件。

### XML 实现

`XmlInvoiceParser` 对应 QingPiao 的 `XmlInvoiceParser.Parse`，但改为异步接口和安全 XML 读取器：

- 先识别数电票 `Header`/`EInvoiceData` 结构；
- 再识别传统 `InvoiceInfo`、`BuyerInfo`、`SellerInfo` 结构；
- 解析发票号码、日期、购买方、销售方、金额和税额；
- 日期统一进入当前 Python 的本地业务日期归一化逻辑；
- 金额进入 Decimal 归一化和税额/价税合计校验；
- 发票号码缺失返回 `XML_INVOICE_NUMBER_NOT_FOUND`，不抛出批次级异常；
- XML DTD、外部实体、深度和文本长度受限。

XML 是最确定的路径，成功后不再调用 OCR 或 DeepSeek，除非本地校验发现关键字段冲突且策略明确允许复核。

### OFD 实现

`OfdInvoiceParser` 对应 QingPiao 的 `OfdInvoiceParser.ParseAll`，保留以下顺序：

1. `Doc_0/Attachs/original_invoice.xml`：纸电票专用 XML；
2. `Doc_0/Tags/Tag.xml` 或 `Doc_0/Tags/CustomTag.xml`：数电票标签；
3. `Doc_0/Pages/Page_*/Content.xml`：建立 `TextObject ID -> TextCode` 映射并解析 `ObjectRef`；
4. 结构化 XML 无法得到有效发票号码时，返回 `OFD_INVOICE_XML_NOT_FOUND`，再由上层策略决定是否渲染页面进入 OCR/DeepSeek。

OFD parser 只负责 ZIP/XML 结构和发票字段，不负责页面渲染。多页遍历不能只固定 `Page_0`；需要遍历所有 `Page_*`，并对同一 Object ID 的文本定义稳定合并规则。`original_invoice.xml` 与 Tag/Content 两条路径分别测试。

### PDF 实现

`PdfInvoiceParser` 合并 QingPiao 的 `PdfInvoiceParser` 与当前 Python `invoice_extractor.py`/`pdf_converter.py` 的行为，但拆成多个内部策略：

- `PdfTextExtractor`：使用 PdfPig alpha 版本读取每页文本；
- `PdfInvoiceSegmenter`：按发票标题和页面边界切分多页、多张发票；
- `PdfFieldParser`：复用清理文本、号码、日期、公司、金额和明细的确定性规则；
- `IPdfPageRenderer`：使用 PDFiumCore 渲染扫描页；
- `IOcrFallback`：使用 SkiaSharp 转换像素，再交给 SimdPaddleOCR；
- `IInvoiceFieldExtractor`：先根据本地 OCR 文本提取，失败时调用 DeepSeek Flash 多模态提取；
- `InvoiceNormalizer`：映射为不可变的 `InvoiceDocument`/`InvoiceRecord` 领域对象。

PDF 的处理顺序为：

```text
PdfPig 文本提取
  -> 文本清理与多发票切分
  -> 确定性字段解析与本地校验
  -> 成功：输出结构化结果
  -> 文本为空/质量不足：PDFium 渲染
  -> SimdPaddleOCR
  -> DeepSeek Flash 文本或多模态提取
  -> 本地校验，不通过则人工复核
```

当前 Python 中的火车票、行程单、国外发票和供应商特例不能被硬编码到通用 PDF parser；应实现为独立的 `IDocumentSpecialCaseParser`，在 `PdfInvoiceParser` 的确定性路径之后、通用 AI fallback 之前运行。其确定性字段修正必须优先于模型输出。

### IInvoiceFieldExtractor 的 DeepSeek adapter

`IInvoiceFieldExtractor` 不是一个简单的“传图并返回字符串”接口，而是承接当前 Python `InvoiceExtractor.extract_info_via_llm` 的两轨路由：

```csharp
public interface IInvoiceFieldExtractor
{
  Task<FieldExtractionResult> ExtractAsync(
    FieldExtractionRequest request,
    CancellationToken cancellationToken);
}

public sealed record FieldExtractionRequest(
  DocumentIdentity Identity,
  string OcrText,
  IReadOnlyList<OcrLine> OcrLines,
  IReadOnlyList<RenderedPage> Pages,
  string CustomRules,
  bool AllowVisionFallback);

public sealed record FieldExtractionResult(
  InvoiceDocument? Document,
  ExtractionRoute Route,
  string ReasonCode,
  bool RequiresManualReview,
  ExtractionTrace Trace);
```

`ExtractionRoute` 和 `ExtractionTrace` 是不包含原文和图像的诊断 DTO：

```csharp
public enum ExtractionRoute
{
  LocalFastPath,
  OcrText,
  VisionFallback
}

public sealed record ExtractionTrace(
  ExtractionRoute Route,
  string TrackAStatus,
  string TrackBStatus,
  string ReasonCode,
  long DurationMs,
  string ModelName,
  string InputKind,
  string ResponseFingerprint = "");
```

#### 请求构造

将当前 Python 的提示词规则迁移为版本化 `InvoiceExtractionPrompt`，固定要求输出单个 JSON 对象，不允许 Markdown 包裹。字段契约包含：

```json
{
  "is_invoice": true,
  "Date": "YYYYMMDD",
  "Purchaser": "",
  "Seller": "",
  "Amount": "0.00",
  "InvoiceCode": "",
  "InvoiceNumber": "",
  "Type": "",
  "category": "",
  "Departure_Date": "",
  "Departure_City": "",
  "Destination_City": ""
}
```

提示词必须保留当前行为规则：销售方名称净化、金额优先取价税合计/小写金额、红字票保留负号、火车票城市无法确认时填“未知”、Folio 的 DD/MM/YY 日期解释，以及 `Type` 必须落在领域分类白名单中。`CustomRules` 作为独立的用户规则段追加，不能覆盖系统字段约束。

#### Track A：OCR 文本路径

1. `SimdPaddleOCR` 生成 OCR 文本、文本框和置信度；
2. 将 OCR 文本、按位置排序的 `OcrLine` 和文档上下文拼入 `user` 文本消息；
3. 使用 `IChatClient`、模型 `deepseek-flash`、温度 `0.1` 请求结构化结果；
4. 清理响应中的 ```json 包裹和外围说明，解析 JSON；
5. 缺少 Date/Seller/Amount/Type/Purchaser 等字段时补充领域默认值，再由本地校验决定是否人工复核；
6. 通过 `InvoiceNormalizer` 归一化金额、日期、类型、身份和 flags。

Track A 只使用 OCR 文本，不上传原始图片；这保留当前 Python “本地 OCR 后文本提取”的隐私和成本优势。

#### Track B：多模态 fallback

当 OCR 失败、OCR 文本过短、必填字段缺失、模型 JSON 无法解析或本地校验不通过时，才进入 Track B：

1. 使用 PDFiumCore + SkiaSharp 生成最多两页 PNG；
2. 图片按 `data:image/png;base64,...` 生成 `DataContent`/`image_url` content block；
3. 将同一版本的系统提示词作为 `user` 消息中的第一个文本块，图片块紧随其后；
4. 单次请求包含所有待识别页面，限制请求体和图片大小，避免超过 DeepSeek 48 MiB/32 MiB 限制；
5. 使用同一 JSON 清洗、字段默认值、类型白名单和本地校验；
6. 将路由标记为 `VisionFallback`，保留 Track A 失败的稳定原因码。

Track B 的图片只作为当前请求的内联数据发送，不生成公网 URL，不把 Base64 写入日志、SQLite、审计事件或 ZeroPipeline Recipe。

#### JSON 与错误处理

adapter 使用严格的 JSON 文档解析，不依赖正则截取嵌套对象。兼容模型偶尔返回 Markdown fence 的情况，但 fence 去除后仍必须得到唯一 JSON 对象。响应失败分类为：

- `AI_TIMEOUT`、`AI_CONNECTION_FAILED`：可重试；
- `AI_RATE_LIMITED`、`AI_QUOTA_EXHAUSTED`：按退避策略重试或终止；
- `AI_INVALID_JSON`、`AI_SCHEMA_INVALID`：Track A 可转 Track B，Track B 则进入人工复核；
- `AI_AUTHENTICATION_FAILED`、`AI_UNSUPPORTED_IMAGE`、`AI_REQUEST_TOO_LARGE`：不可盲目重试；
- `EXTRACTOR_ALL_ROUTES_FAILED`：所有路径失败后的最终结果。

adapter 不记录响应正文。只记录路由、耗时、模型名、输入类型、响应状态和响应指纹；响应指纹使用短 SHA-256，便于诊断而不泄露票据内容。

#### 确定性修正与 trace

模型结果进入领域对象前，先执行当前 Python 已验证的确定性修正，例如火车票行程日期覆盖模型漂移、特殊供应商票据字段修正和文档类型归一化。每次提取生成 `ExtractionTrace`：

```text
Route: LocalFastPath | OcrText | VisionFallback
TrackAStatus: NotStarted | Success | Failed
TrackBStatus: NotStarted | Success | Failed
ReasonCode
DurationMs
ModelName
```

trace 只存状态和脱敏元数据，不存 OCR 原文、图片、提示词或 API Key。

### 归一化与编排边界

三类 parser 只返回 `InvoiceParseResult`。`InvoiceNormalizer` 负责金额、日期、文档类型、身份和 flags 的统一表示；`InvoiceAcceptanceService` 负责关键字段校验、税额计算、查重键准备和人工复核原因；ZeroPipeline 节点负责批量调度、背压、重试、审计和结果汇总。

这样既保留 QingPiao 的 parser 分层，也吸收当前 Python 的候选身份、低置信度 fallback、供应商特例和真值审计约束。

## 7. OCR 与 AI 提取

OCR 与 AI 链路如下：

```text
PDF/OFD 页面图像
  -> SimdPaddleOCR 本地 OCR
  -> OCR 文本、坐标和置信度
  -> DeepSeek Flash 结构化提取
  -> 本地字段归一化和校验
  -> 发票领域对象
  -> 必要时使用 DeepSeek Flash 多模态复核
```

### 本地 OCR

使用以下组件：

- `Sdcb.SimdPaddleOCR` `1.4.2`；
- 初始使用 `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` 等适合中文发票的模型包；
- 使用 ImageSharp 或 SkiaSharp 负责图像解码和像素转换。

OCR 输出和 PDF 渲染输出只在应用/基础设施内存中短暂存在，不能作为领域实体持久化：

```csharp
public sealed record BoundingBox(int Left, int Top, int Width, int Height);

public sealed record OcrLine(
  string Text,
  BoundingBox Bounds,
  decimal Confidence,
  int PageNumber,
  int Ordinal);

public sealed record OcrDocument(
  DocumentIdentity Identity,
  IReadOnlyList<OcrLine> Lines,
  decimal Confidence,
  string EngineName,
  int PageCount);

public sealed record RenderedPage(
  int PageNumber,
  int Width,
  int Height,
  string MimeType,
  ReadOnlyMemory<byte> EncodedBytes,
  long ByteLength);
```

`OcrLine.Confidence` 和 `OcrDocument.Confidence` 限定在 `[0,1]`；`Ordinal` 记录同页原始识别顺序，字段提取前按页码、Y 坐标、X 坐标和 ordinal 稳定排序。`RenderedPage.EncodedBytes` 只允许存在于受控的 transient buffer，不能进入 `PipelineContext` 的持久化快照、日志、审计或 Recipe。

`PaddleOcrAll` 在应用生命周期内只加载一次并复用。OCR 在受限的后台 worker 中运行，通过 `LineWorkerCount`、页面并发数和图像尺寸限制内存峰值。首个版本使用普通的自包含 `win-x64` JIT/ReadyToRun 发布，暂不采用 Native AOT。

```csharp
public interface IInvoiceOcr
{
    Task<OcrDocument> RecognizeAsync(
        DocumentImage image,
        CancellationToken cancellationToken);
}
```

### 通过 Microsoft.Extensions.AI 接入 DeepSeek Flash

DeepSeek Flash 是新设计中唯一的远程 AI 服务。通过 `Microsoft.Extensions.AI` 和 `Microsoft.Extensions.AI.OpenAI`，使用 DeepSeek 的 OpenAI 兼容端点接入。

```csharp
IChatClient chatClient = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions
    {
        Endpoint = new Uri("https://api.deepseek.com")
    })
    .GetChatClient(modelName)
    .AsIChatClient();
```

应用层只依赖内部接口，`IChatClient` 由基础设施层持有：

具体的 `IInvoiceFieldExtractor` 请求/结果模型、Track A/Track B 路由和响应处理规则见第 6 节。文本请求包含 OCR 文本和坐标；多模态请求使用 DeepSeek OpenAI 兼容 Chat Completions 格式：图片必须位于 `user` 消息的 content block 数组中，图片块使用 `image_url`。PDF 页面默认渲染为 PNG，并编码为 `data:image/png;base64,...`；其他图像保留经过验证的实际 MIME 类型，不上传临时公网 URL。两条路径共用类型化响应校验和本地业务校验。

DeepSeek 视觉配置固定为：

- 模型：`deepseek-flash`；
- 端点：`https://api.deepseek.com`；
- 支持图像格式：JPEG、PNG、GIF、WebP；
- `image_url.detail` 可配置为 `low`、`high`、`original` 或 `auto`，发票识别默认使用 `original`；
- 图片只放在 `user` 消息中，不能放在 `system` 或 `assistant` 消息中；
- Base64 内联请求体上限为 48 MiB，单张 Base64/URL 图片最大 32 MiB；
- 单边最大 8192 像素；单请求最多 600 张图片，并受总大小限制约束。

`Microsoft.Extensions.AI.OpenAI 10.10.0` 的 `DataContent` 到 DeepSeek `image_url` content block 的序列化结果必须通过集成测试验证。如果该版本不能生成 DeepSeek 所需的 block 结构，则在 `IInvoiceFieldExtractor` 内部增加受控的 OpenAI-compatible HTTP content adapter；业务层仍只依赖 `IInvoiceFieldExtractor`，不直接依赖 DeepSeek JSON。

模型名称、端点、温度、token 上限、图片细节级别、超时时间和重试策略均配置化，但默认模型必须是 `deepseek-flash`。

系统先尝试基于 OCR 文本的提取。如果 OCR 置信度过低、必填字段缺失或本地校验失败，则将原始页面图像发送给 DeepSeek Flash 进行多模态复核。新方案不包含 GLM 的配置、包引用、错误码或运行时路径。

远程 AI 错误必须转换为稳定错误，包括超时、认证失败、限流、额度耗尽、请求体超过 48 MiB、图片格式不支持、响应无效和多模态序列化不兼容。单元测试使用假的 `IChatClient`，不需要真实 API Key；另设 DeepSeek 兼容端点集成测试验证最终 JSON content block。

## 8. 存储与安全

使用 EF Core SQLite 保存运行索引、发票历史、审计事件和人工复核状态。应用数据保存到 `%LocalAppData%/InvoiceFlowAI`；用户选择的发票和报表保存到指定输出目录。

`InvoiceFlowDbContext` 位于 `InvoiceFlowAI.Infrastructure.Persistence`，领域层只依赖仓储或查询接口，不引用 EF Core 实体和 `DbContext`。数据库模型至少包含：

- `Runs`：运行 ID、状态、阶段、开始/结束时间和结果摘要；
- `Documents`：文档 ID、来源、文件哈希、文件类型和处理状态；
- `Invoices`：发票主数据、归一化字段、查重键和归档状态；
- `InvoiceItems`：发票明细；
- `AuditEvents`：稳定 schema 的真值/审计事件；
- `ManualReviewItems`：人工复核原因、状态和处理时间。

使用 EF Core migrations 管理数据库结构。应用启动时只执行已发布的迁移，不在运行时自动创建或删除数据库。数据库写入使用显式事务，发票主数据、明细、归档状态和对应审计事件必须保持一致；日志写入不参与业务事务。

单次运行使用独立的 DbContext 生命周期，禁止跨线程共享 DbContext。批处理使用有界批量写入，避免逐条提交造成性能和锁竞争问题。SQLite 的 busy timeout、WAL 模式和连接重试策略配置化，并对数据库损坏、磁盘满和迁移失败返回稳定错误码。

### DeepSeek API Key 的 DPAPI 存储

DeepSeek API Key 使用 Windows DPAPI 保存，参考 `E:/GitHub/Lyntai/src/Lyntai.Secrets.Dpapi` 的 `DpapiSecretProtector` 和 Vault 注册边界。新应用不直接在业务服务中调用 `ProtectedData`，而是通过应用层接口隔离平台加密细节：

```csharp
public interface ISecretStore
{
  Task SaveAsync(string name, string value, CancellationToken cancellationToken);
  Task<string?> GetAsync(string name, CancellationToken cancellationToken);
  Task DeleteAsync(string name, CancellationToken cancellationToken);
}
```

实现要求：

- 使用 `DataProtectionScope.CurrentUser`，使密文只能由保存它的 Windows 用户在同一台机器上解密；
- 使用固定的应用级 entropy，例如由 `InvoiceFlowAI` 应用标识派生的 UTF-8 字节，避免同一用户下的其他应用直接复用密文；
- 密文以 Base64 形式保存到 `%LocalAppData%/InvoiceFlowAI/secrets.json` 或等价的本地键值存储；
- 文件只保存密文、版本和密钥名称，不保存 API Key 明文；
- 使用临时文件、替换写入和用户 ACL，避免写入中断造成半个密文文件；
- `Protect`/`Unprotect` 的平台检查在构造阶段完成，非 Windows 环境立即抛出 `PlatformNotSupportedException`；
- Base64 无效、密文损坏、用户不匹配、机器不匹配或篡改统一转换为 `CryptographicException`，由设置界面显示“凭据不可用，请重新配置”；
- 日志、审计记录、崩溃报告、JSON/RPC 事件和 ZeroPipeline Recipe 都不能包含 API Key 或解密后的密文。

默认不使用 `LocalMachine`，因为本应用是单用户桌面应用，不需要让同一台机器上的其他账户读取凭据。若未来提供 Windows 服务模式，必须新增显式配置和单独的安全评审，不能静默改变现有密钥作用域。

邮箱授权码沿用同一 `ISecretStore` 抽象和 DPAPI 保护策略。DeepSeek 与邮箱凭据使用不同的逻辑名称，例如 `deepseek.api-key` 和 `mail.imap.auth-code`，但共享相同的用户绑定和文件权限策略。

URL 证据只保存脱敏域名、稳定哈希和阶段元数据。原始邮件和发票图片默认保存在本地，只有在用户配置的 AI 策略允许时才上传给 DeepSeek。

## 9. 外部集成

- IMAP：使用 MailKit 适配器，支持供应商设置和受限邮箱扫描；
- 链接恢复：使用 .NET 版 Playwright 和供应商专用下载适配器；
- 报表：使用 ClosedXML 生成发票汇总、明细和人工复核工作簿；
- PDF：使用 `PdfPig` `0.1.17-alpha-202609192350-df33d` 进行文本提取和页面判断，使用 `PDFiumCore` `155.0.8057` 将扫描页面渲染为 OCR 图像；
- OFD：本文档规定的专用 ZIP/XML 发票解析器；
- 凭据：Windows DPAPI `CurrentUser` 保护器，参考 `Lyntai.Secrets.Dpapi`；
- 持久化：EF Core 10 + `Microsoft.EntityFrameworkCore.Sqlite`；
- 日志：Serilog 结构化日志，通过 `Microsoft.Extensions.Logging` 注入；
- AI：由 `Microsoft.Extensions.AI.OpenAI` 提供 `IChatClient`，接入 DeepSeek 的 OpenAI 兼容端点。

### PDF 渲染适配边界

`PDFiumCore` 只位于 `InvoiceFlowAI.Infrastructure.Documents`，通过内部 `IPdfPageRenderer` 接口提供页面渲染能力。`SkiaSharp` 负责图片解码、颜色空间/像素格式转换和向 `SimdPaddleOCR` 提供连续像素缓冲。应用层和领域层不直接引用 PDFium 或 SkiaSharp 类型。

渲染器必须：

- 以 `win-x64` 为首个发布目标，验证 PDFium 原生 DLL 与 .NET 10 的加载方式；
- 明确原生库搜索路径，优先从应用发布目录加载，禁止依赖开发机全局 DLL；
- 支持配置 DPI、页面范围、像素格式和最大页面尺寸；
- 将 PDFium 原生资源随自包含发布包一起发布，并在启动诊断中记录版本和加载结果；
- 对损坏 PDF、超大页面、渲染超时和原生库加载失败返回稳定错误码；
- 使用内存流或受控临时文件将渲染结果交给 `IInvoiceOcr`，任务完成后清理临时资源；
- 在发布验收中确认 PDFiumCore 的许可证、原生组件再分发条款和第三方声明。

`SkiaSharp 4.154.0-preview.1.26454.9` 的 Windows x64 native assets 必须随发布包正确加载；需要验证 PDFium 输出到 SkiaSharp 位图的像素格式、stride、颜色通道顺序和资源释放，避免将未释放的 `SKBitmap` 或非连续缓冲传入 OCR。

文本型 PDF 仍先由 PdfPig 处理；只有文本缺失、文本质量不足或页面需要视觉识别时才调用 PDFium 渲染，避免不必要的 CPU 和内存开销。由于当前 PdfPig 版本为 alpha 预览包，必须在依赖锁定文件和发布构建中固定精确版本，并使用代表性中文发票 PDF 样本验证文本提取结果。

### IMAP 适配边界

`MailKit` 只位于 `InvoiceFlowAI.Infrastructure.Mail`，应用层依赖 `IMailboxScanner`，不直接引用 MailKit 类型。适配器负责：

- 使用 IMAPS/TLS 连接 QQ、163 等邮箱；
- 按日期范围和发件人/主题规则筛选邮件；
- 递归读取 MIME、附件和嵌套 ZIP；
- 将附件转换为 `DocumentCandidate`，不把 MimeKit 对象泄漏到领域层；
- 支持 `CancellationToken`、连接超时、断线重连和单封邮件失败隔离；
- 对邮件 UID、附件哈希和 Message-ID 去重，避免重复下载；
- 不记录授权码、邮件正文、完整附件 URL 或完整邮件头。

IMAP 测试使用 MailKit 可替换的传输/协议边界或本地测试服务器，覆盖 TLS 失败、认证失败、分页、重复邮件、嵌套附件、损坏 MIME、断线重连和取消。

## 10. 错误模型

前端使用稳定错误码，而不是直接展示原始异常文本：

```json
{
  "runId": "run-1",
  "stage": "document_extraction",
  "reasonCode": "OFD_INVOICE_XML_NOT_FOUND",
  "userMessage": "OFD 文件中未找到可识别的发票数据。",
  "retryable": false,
  "documentId": "document-1",
  "detailsAvailable": true
}
```

错误类别包括输入校验、单文件失败、可重试的网络/AI 失败、认证、持久化、磁盘、WebView2 和系统错误。面向用户的消息安全且可本地化；诊断信息只保留脱敏后的技术细节。

## 11. 测试与验收

### 单元测试

覆盖领域校验、金额和税额计算、规则、配对、查重、归档命名、路径安全、三类 parser 契约、OFD XML 变体、OCR 归一化、DeepSeek 响应校验、DPAPI 密钥存储、Serilog 日志字段和报表映射。

### 集成测试

验证 WebView2 RPC 分发、完整本地文件链路、真实 OFD 样本、假的 `IChatClient` 响应、重试/取消、EF Core SQLite 持久化、迁移、事务、并发写入、审计事件和 Excel 生成。三类 parser 还必须用当前 Python 样本和 QingPiao 样本做行为对照：字段等价、人工复核原因稳定、PDF 多发票切分一致、XML/OFD 优先级一致。

### Windows 端到端测试

在干净的 Windows 11 x64 环境中验证首次启动、WebView2 加载、取消、网络中断和重试、大批量处理、WebView2 故障后的恢复、自包含启动、OCR 模型随包加载，以及不依赖 Python。

验收以行为而非代码翻译相似度为标准：

- 相同邮箱样本得到等价的候选集合；
- 在 MailKit `4.18.0` 下，相同邮箱样本得到等价的候选集合；
- 相同 PDF/OFD/XML 样本得到等价的发票字段和人工复核分类；
- 配对、归档、查重、报表和审计结果正确；
- 所有失败场景都有稳定且可解释的错误；
- 在干净的 Windows 11 机器上，只配置必要的 DeepSeek 信息即可运行发布包。

## 12. 风险与待验证事项

- 通过目标端点验证 DeepSeek Flash 的具体模型标识和多模态内容格式；
- 验证 `Microsoft.Extensions.AI.OpenAI 10.10.0` 的 `DataContent` 或受控 adapter 最终生成 `user.content[].type=image_url`，并正确承载 Base64 data URL；
- 验证 `deepseek-flash` 的 `detail=original`、图片格式和 48 MiB/32 MiB 限制处理；
- 验证 `ZeroPipeline.Core 1.2.0` 在 .NET 10 `win-x64` 发布目标中的 DAG 调度、背压、取消和节点失败隔离；
- 验证 DPAPI 当前用户作用域、应用 entropy、密文损坏和用户/机器迁移失败行为；
- 验证 Serilog 日志结构、滚动保留、异常事件、取消事件和敏感字段脱敏；
- 测试 SimdPaddleOCR 对小字体、旋转、低分辨率和扫描发票的识别效果；
- 验证 PDF/OFD 渲染质量是否满足 OCR 要求；
- 验证 `PDFiumCore` `155.0.8057` 在自包含 `win-x64` 发布包中的原生 DLL 加载、版本诊断和许可证声明；
- 验证 `SkiaSharp` `4.154.0-preview.1.26454.9` 的 native assets、像素格式、stride 和 OCR 资源释放；
- 验证 `PdfPig` `0.1.17-alpha-202609192350-df33d` 的中文发票文本提取、alpha 包还原和发布构建可重复性；
- 使用 `original_invoice.xml` 和 `Tag.xml`/`CustomTag.xml` 两类样本验证 OFD 解析；
- 确认 Playwright 供应商流程和打包后的浏览器行为；
- 测量受限 OCR 并发下的内存占用；
- 确认中文 Excel 列宽和自包含发布包体积。

这些是实现阶段的验证任务，不构成改变已批准架构的理由。
