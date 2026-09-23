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
- `ZeroPipeline.Recipe` `1.2.0`：JSON Recipe 序列化、节点注册和图构建。

```xml
<PackageReference Include="ZeroPipeline.Core" Version="1.2.0" />
<PackageReference Include="ZeroPipeline.Recipe" Version="1.2.0" />
```

`ZeroPipeline.Core` 和 `ZeroPipeline.Recipe` 都固定为 `1.2.0`，并在解决方案锁文件中固定传递依赖；不能使用浮动版本。

IMAP 处理固定使用以下包：

```xml
<PackageReference Include="MailKit" Version="4.18.0" />
```

本地持久化使用 EF Core SQLite：

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.12" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
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
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.250916001" />
<PackageReference Include="Microsoft.Playwright" Version="1.62.0" />
<PackageReference Include="ClosedXML" Version="0.105.1" />
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

前端通过 `window.chrome.webview.postMessage` 向 `InvoiceFlowAI.App` 发送 JSON/RPC 请求，并通过 WebView2 `message` 事件接收响应和异步事件。协议固定为 `invoiceflow.rpc.v1`，JSON 属性使用 camelCase，时间使用 UTC ISO-8601，金额使用字符串或已明确精度的 JSON number，所有 ID 使用不透明字符串。页面不直接访问文件系统、DPAPI、数据库、MailKit、HTTP 或 ZeroPipeline。

### 消息 envelope

请求、响应和事件使用不同但可判别的 envelope：

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-1",
  "method": "run.start",
  "params": {
    "dateFrom": "2026-09-01",
    "dateTo": "2026-09-23",
    "savePath": "C:/Invoices",
    "customRules": "",
    "accountId": "mail-account-1",
    "mailbox": "INBOX",
    "runMode": "interactive"
  }
}
```

成功响应：

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-1",
  "ok": true,
  "result": {
    "runId": "run-1",
    "state": "created"
  }
}
```

失败响应：

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-1",
  "ok": false,
  "error": {
    "code": "RUN_ALREADY_ACTIVE",
    "scope": "run",
    "retryable": false,
    "userMessage": "已有运行正在处理。",
    "detailsAvailable": true
  }
}
```

异步事件：

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "event": "run.progress",
  "runId": "run-1",
  "eventSequence": 12,
  "emittedAtUtc": "2026-09-23T10:00:00Z",
  "payload": {
    "stage": "extracting",
    "completed": 4,
    "total": 10,
    "percent": 40
  }
}
```

`id` 只用于一次 RPC 请求关联，前端必须保证同一活动请求中唯一；`runId` 标识长任务；`eventSequence` 在单个 run 内严格递增，事件不能依赖 WebView2 传输顺序来重排。后端对重复的幂等请求返回相同语义的响应，不重复创建运行或重复取消。

### 方法契约

首版只公开以下方法，未知方法返回 `RPC_METHOD_NOT_FOUND`，未知参数返回 `RPC_INVALID_PARAMS`：

| 方法 | 请求参数 | 成功结果 | 幂等/副作用 |
| --- | --- | --- | --- |
| `bridge.hello` | `clientVersion`、`supportedProtocolVersions` | `protocolVersion`、`serverVersion`、能力列表 | 无副作用；页面加载后必须先调用。 |
| `run.start` | `RunInput` 的 RPC 映射 | `runId`、初始状态 | 同一 `id` 重复请求返回原响应；新 `id` 创建新 run。 |
| `run.get` | `runId`、可选 `afterEventSequence` | 当前快照和可重放事件 | 只读；用于页面重连恢复。 |
| `run.cancel` | `runId`、`reason` | `runId`、`state`、`accepted` | 重复取消安全；已终态运行返回当前终态。 |
| `run.retry` | `runId`、`documentIds` 或 `retryAllEligible` | 新的 `runId` 或重试批次 ID | 只允许重试具有 `Retryable=true` 的候选。 |
| `review.list` | `runId`、分页和状态筛选 | 人工复核摘要分页 | 只读。 |
| `review.get` | `reviewId` | 脱敏的复核详情和字段 | 只读，不返回原始凭据。 |
| `review.submit` | `reviewId`、修正字段、决定 | 更新后的候选终态 | 必须带 revision，重复 revision 不重复应用。 |
| `settings.get` | 无或设置分组 | 非秘密设置 | API Key、邮箱授权码只返回 `configured` 和掩码状态。 |
| `settings.update` | 非秘密设置 | 更新结果和配置指纹 | 不允许携带 API Key、邮箱授权码或其他秘密字段。 |
| `secret.set` | `name`、`value` | `name`、`configured` | 只允许受控的秘密名称；写入 DPAPI 后不回显 value。 |
| `secret.delete` | `name` | `name`、`configured=false` | 幂等删除；不返回旧值。 |
| `report.open` | `runId`、报告类型 | 受控临时打开 token | 后端验证路径，不接受前端任意路径。 |

`run.start` 的参数映射为 `RunInput`：`dateFrom`、`dateTo` 使用 `yyyy-MM-dd`；`savePath` 必须是用户可访问的目录；`accountId` 必须引用已保存的邮箱配置；`customRules` 有长度上限；`runMode` 只能取 `interactive` 或首版明确支持的枚举值。后端重新校验所有字段，不能信任前端校验。

### 方法 DTO、分页和 JSON fixtures

所有方法的 `params` 和 `result` 在 `InvoiceFlowAI.Contracts.Rpc` 中使用显式 record；禁止用 `Dictionary<string, object>` 作为业务 DTO。未知字段默认拒绝，缺失必填字段返回 `RPC_INVALID_PARAMS`，`error.details` 使用统一的 field error 数组：

```csharp
public sealed record RpcFieldError(
    string Path,
    string Code,
    string Message);

public sealed record ReviewListRequest(
    string RunId,
    ManualReviewState? State = null,
    int Offset = 0,
    int Limit = 50);

public sealed record ReviewSummary(
    string ReviewId,
    string DocumentId,
    string ReasonCode,
    ManualReviewState State,
    int Revision,
    DateTimeOffset CreatedAtUtc);

public sealed record ReviewListResult(
    IReadOnlyList<ReviewSummary> Items,
    int Offset,
    int Limit,
    int Total,
    bool HasMore,
    int? NextOffset);

public sealed record ReviewGetRequest(string ReviewId);

public sealed record ReviewDetail(
    ManualReviewItem Item,
    IReadOnlyList<string> EditableFields,
    IReadOnlyList<string> AvailableDecisions,
    string SourceFileName,
    string SourceKind);

public sealed record ReviewSubmitRequest(
    string ReviewId,
    int ExpectedRevision,
    ManualReviewDecision Decision,
    InvoiceCorrectionPatch? Correction = null,
    string Comment = "");

public sealed record InvoiceCorrectionPatch(
    DateOnly? InvoiceDate = null,
    string? Purchaser = null,
    string? Seller = null,
    decimal? Amount = null,
    decimal? TaxAmount = null,
    decimal? TotalAmount = null,
    string? InvoiceCode = null,
    string? InvoiceNumber = null,
    InvoiceDocumentType? DocumentType = null,
    string? Category = null,
    InvoiceRoute? Route = null,
    IReadOnlyList<InvoiceItem>? Items = null);

public sealed record ReviewSubmitResult(
    string ReviewId,
    string DocumentId,
    int NewProcessingRevision,
    CandidateStatus Status,
    string ReasonCode,
    bool RearchiveQueued,
    bool ReportRefreshQueued);

public sealed record RunRetryRequest(
    string RunId,
    IReadOnlyList<string>? DocumentIds = null,
    bool RetryAllEligible = false,
    int MaxDocuments = 100);

public sealed record RunRetryResult(
    string RetryRunId,
    IReadOnlyList<string> AcceptedDocumentIds,
    IReadOnlyList<string> RejectedDocumentIds,
    string State);

public sealed record PipelineOptionsPatch(
  int? MailboxScanConcurrency = null,
  int? OcrPageConcurrency = null,
  int? AiRequestConcurrency = null,
  int? BrowserConcurrency = null,
  int? MaxInFlightCandidates = null,
  int? MaxRetryAttempts = null);

public sealed record SettingsUpdateRequest(
    int ExpectedRevision,
    string? AccountId = null,
    string? Mailbox = null,
    MailboxFilterRules? MailboxFilters = null,
    PipelineOptionsPatch? Pipeline = null,
    string? CustomRuleSetJson = null,
    bool? AllowVisionFallback = null);

public sealed record SettingsUpdateResult(
    int Revision,
    string ConfigurationFingerprint,
    IReadOnlyList<string> ChangedSections);
```

分页规则固定为 offset/limit：`Offset >= 0`、`1 <= Limit <= 100`，排序为 `CreatedAtUtc ASC, ReviewId ASC`，返回 `Total`、`HasMore` 和 `NextOffset`。`review.get` 不返回 OCR 原文、图片、完整邮件正文、秘密或本地绝对路径；`EditableFields` 是后端白名单，不由前端决定。`review.submit` 的 `Decision` 与 `Correction` 组合必须满足：`CorrectAndAccept` 必须有 correction，其他决定不能携带 correction；`ExpectedRevision` 必须等于当前 review revision。

`run.retry` 的 `DocumentIds` 与 `RetryAllEligible` 互斥；单次最多 100 个 document，只有 `Retryable=true` 且未超过最大次数的候选进入 `AcceptedDocumentIds`，其余进入 `RejectedDocumentIds` 并带稳定原因详情。`settings.update` 只允许非秘密配置，修改后由后端规范化 JSON、规则 AST 和 pipeline options 生成新的 `ConfigurationFingerprint`；`ExpectedRevision` 冲突返回 `SETTINGS_REVISION_CONFLICT`。

首版 JSON fixtures 固定覆盖以下请求/响应：

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-review-list-1",
  "method": "review.list",
  "params": {
    "runId": "run-1",
    "state": "open",
    "offset": 0,
    "limit": 50
  }
}
```

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-review-submit-1",
  "method": "review.submit",
  "params": {
    "reviewId": "review-1",
    "expectedRevision": 2,
    "decision": "CorrectAndAccept",
    "correction": {
      "invoiceDate": "2026-09-22",
      "amount": "100.00",
      "seller": "Example Seller"
    },
    "comment": "日期和金额已人工确认"
  }
}
```

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-retry-1",
  "method": "run.retry",
  "params": {
    "runId": "run-1",
    "documentIds": ["document-1", "document-2"],
    "retryAllEligible": false,
    "maxDocuments": 100
  }
}
```

```json
{
  "protocol": "invoiceflow.rpc.v1",
  "id": "request-settings-update-1",
  "method": "settings.update",
  "params": {
    "expectedRevision": 4,
    "mailbox": "INBOX",
    "allowVisionFallback": true,
    "pipeline": {
      "aiRequestConcurrency": 1
    }
  }
}
```

fixture 还必须包含 `RPC_INVALID_PARAMS`、`REVIEW_REVISION_CONFLICT`、`SETTINGS_REVISION_CONFLICT`、`RUN_RETRY_SELECTION_INVALID` 和未知字段的失败响应；所有 fixture 通过 Contracts serializer round-trip 测试，确保 camelCase、DateOnly、Decimal 和 enum 字符串表示稳定。

### 事件契约

事件只由后端发送，前端不能伪造运行事件。所有事件都包含 `protocol`、`event`、`runId`、`eventSequence`、`emittedAtUtc` 和 `payload`。同一 `eventSequence` 只能发送一次；前端发现序号跳跃时调用 `run.get`，使用 `afterEventSequence` 请求缺失事件。

- `run.stageChanged`：`previousStage`、`stage`、`stageSequence`；
- `run.progress`：`stage`、`completed`、`total`、`percent`；
- `run.documentResult`：脱敏的 `DocumentId`、sequence、`CandidateStatus`、reason code、人工复核标记和归档相对路径；
- `run.failed`：`RunFailure` 的安全映射，包含 stage、reason code、retryable 和 `detailsAvailable`；
- `run.completed`：`RunSummary` 的安全映射；
- `run.cancelled`：取消原因、已完成数量、未处理数量和最终摘要。

`run.documentResult` 不发送 OCR 文本、原始邮件、图片、完整 URL、API Key、完整模型响应或本地绝对路径。相同 candidate 的重试结果带有新的 processing revision，但不能改变原始 sequence。

### 连接、取消和错误规则

页面加载后先执行 `bridge.hello`；在 hello 成功前，后端只接受 hello 和诊断级握手请求。WebView2 重新加载或暂时断开不会取消运行，页面重新连接后通过 `run.get` 恢复当前快照和事件；事件重放有固定上限，超出范围时返回完整快照并要求前端丢弃旧事件缓存。

`run.cancel` 只设置运行取消信号，不强制终止线程或删除已提交结果。节点在下一个安全检查点停止读取新输入，候选结果按 `Cancelled` 终态写入，最终发送且只发送一次 `run.cancelled`。取消一个不存在的 run 返回 `RUN_NOT_FOUND`，取消已完成 run 返回 `accepted=false` 和当前状态。

错误 envelope 只使用稳定的 `code`、`scope`、`retryable`、`userMessage`、`detailsAvailable` 和可选的脱敏 `details`。原始异常类型、堆栈、完整 URL、邮件内容、OCR 内容和秘密不能进入 RPC。错误码至少覆盖：`RPC_INVALID_JSON`、`RPC_PROTOCOL_UNSUPPORTED`、`RPC_METHOD_NOT_FOUND`、`RPC_INVALID_PARAMS`、`RUN_ALREADY_ACTIVE`、`RUN_NOT_FOUND`、`RUN_NOT_CANCELLABLE`、`REVIEW_REVISION_CONFLICT`、`SETTINGS_REVISION_CONFLICT`、`RUN_RETRY_SELECTION_INVALID`、`CREDENTIALS_NOT_CONFIGURED`、`RUN_FAILED`、`PERSISTENCE_UNAVAILABLE` 和 `WEBVIEW_BRIDGE_NOT_READY`。

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

### PipelineOptions 与流控默认值

首版不使用 ZeroPipeline 默认的所有端口容量，而是在应用层统一创建 `PipelineOptions`，并把容量和并发限制作为每次运行的不可变配置快照：

```csharp
public sealed record PipelineOptions(
  int ControlPortCapacity = 1,
  int MailboxBatchPortCapacity = 4,
  int CandidateBatchPortCapacity = 8,
  int ExtractionBatchPortCapacity = 4,
  int ResultBatchPortCapacity = 8,
  int EventPortCapacity = 64,
  int HeaderBatchSize = 200,
  int MessageBatchSize = 25,
  int CandidateBatchSize = 16,
  int MaxInFlightCandidates = 32,
  int MailboxScanConcurrency = 1,
  int OcrPageConcurrency = 2,
  int OcrLineWorkerCount = 2,
  int AiRequestConcurrency = 2,
  int BrowserConcurrency = 1,
  int ArchiveConcurrency = 2,
  int DatabaseWriterConcurrency = 1,
  int MaxReorderBufferItems = 32,
  int MaxRetryAttempts = 2,
  int MaxEventReplayItems = 1000,
  TimeSpan NodeExecutionTimeout,
  TimeSpan RetryGapTimeout)
{
  public static PipelineOptions Default => new(
    NodeExecutionTimeout: TimeSpan.FromMinutes(5),
    RetryGapTimeout: TimeSpan.FromMinutes(10));
}
```

实现必须从 `PipelineOptions.Default` 或显式配置工厂创建实例，不能直接传入 `TimeSpan.Zero`；`TimeSpan.Zero` 不表示无限等待，而是非法配置。所有业务输入端口使用 `BackpressurePolicy.Block`，禁止 `DropOldest` 和 `DropNewest`。控制 packet、EOF 和终态事件使用容量 1 或 64；邮箱 batch 使用容量 4；候选 batch 使用容量 8；OCR/AI 载荷使用容量 4；结果和归档 batch 使用容量 8。容量以 batch/packet 数计算，图像字节数另由 `MaxInFlightCandidates`、页面尺寸和 DeepSeek 请求体上限约束。

并发上限是硬上限而不是建议值：每个运行只能有 1 个 IMAP 扫描 producer；OCR 最多同时处理 2 个页面，单个 `PaddleOcrAll` 实例内最多 2 个 line worker；DeepSeek 最多 2 个 in-flight request；Playwright 浏览器恢复全局最多 1 个活动 context；归档最多 2 个文件操作；SQLite writer 固定为 1。单个 run 的 `MaxInFlightCandidates` 为 32，多个 run 不共享带状态的节点或这些并发槽。配置文件可以降低上限，但不能超过首版硬上限。

### SequenceReorderBuffer 的位置和语义

`SequenceReorderBuffer<T>` 位于 `InvoiceFlowAI.Application.Pipeline.Ordering`，是节点内部有限并发 worker 与 ZeroPipeline `OutputPort<T>` 之间的应用层组件。它不放在 Domain，不修改 ZeroPipeline.Core，也不把排序逻辑放进 `PipelineContext`：

```csharp
public sealed class SequenceReorderBuffer<T>
{
  public SequenceReorderBuffer(long firstSequence, int capacity);

  public IReadOnlyList<DataPacket<T>> Add(DataPacket<T> packet);

  public IReadOnlyList<DataPacket<T>> CompleteEndOfStream(
    DataPacket<T> endOfStream);
}
```

规则如下：

- 按 `DataPacket.SequenceNumber` 保存乱序 packet，只有从 `nextExpectedSequence` 连续可用时才释放到输出端口；
- 小于已释放 sequence 的重复 packet 被视为幂等重放并丢弃，payload 不重复写库；
- 超过 `MaxReorderBufferItems` 时停止向 worker 投递并依靠输入端口背压；
- 收到 EOF 只记录结束 sequence，不立即传播；只有所有低于 EOF 的 sequence 已释放且 retry barrier 已清空时才传播 EOF；
- 出现无法补齐的 sequence 时等待 `RetryGapTimeout`，之后抛出 `PIPELINE_SEQUENCE_GAP` 节点级故障，不能静默跳过；
- `Add` 和 `CompleteEndOfStream` 只在单个节点的应用层协调器内调用，不跨线程共享同一个 ZeroPipeline port。

### Retry 重新投递与 EOF

候选失败后，`IRetryCoordinator` 在结果进入最终结果端口前决定是否重试。可重试 candidate 会保留原始 `DocumentId`、`Sequence` 和 `CorrelationId`，增加 `Attempt` 与 `ProcessingRevision`，重新进入对应阶段的 retry input；不创建新的业务 sequence。`SequenceReorderBuffer` 将该 sequence 标记为 reserved，后续已完成 sequence 可以暂存在 buffer，但不能越过它向下游释放。

retry 发生时遵循以下顺序：

1. 当前尝试的临时输出和未提交事务回滚；
2. `IRetryCoordinator.Decide` 生成退避和下一次 attempt；
3. candidate 进入有界 retry queue，队列满时通过上游背压；
4. 成功或最终失败的唯一 `CandidateProcessResult` 替换 reserved sequence；
5. buffer 连续释放结果，随后才允许下游消费。

源节点收到 EOF 不代表整条链路可以立即结束。每个有 retry 能力的节点维护 `RetryBarrier`：`sourceEofReceived && retryQueueEmpty && activeAttempts==0 && reorderBufferEmpty` 全部成立时，才向下游发送 EOF。取消时不等待新的 retry，直接取消未开始的 retry，并为 reserved sequence 产生 `Cancelled` 结果后再关闭输出。retry 超时或持久化失败升级为节点/运行级失败，不发送伪造的 EOF。

### 多输入汇聚与 EOF

所有需要多个上游的汇聚节点使用 `AllInputsCompletionPolicy`，为每个已连接输入维护独立状态：`Open`、`EndOfStream`、`Faulted`、`Cancelled`。节点必须等待所有输入都达到 `EndOfStream` 后，才能产生最终汇总并向下游传播 EOF；一个输入没有数据不等于 EOF。

汇聚规则固定为：

- 业务 packet 按 `(RunId, Sequence, CorrelationId)` 归并；每个 required input 对同一 key 必须提供 packet 或显式 `NoValue` marker，不能用超时猜测缺失；
- 任一输入发生 `RunFailure` 或节点异常，立即停止汇聚并使运行失败；候选级 `CandidateProcessResult` 作为普通值继续转发；
- 重复 EOF 幂等忽略；EOF 到达后该输入不得再发送业务 packet，违反则产生 `PIPELINE_DATA_AFTER_EOF`；
- 所有输入 EOF 且 retry barrier 清空后，汇聚节点发送一次最终 `PairingBatch`、`ArchiveBatch` 或 `RunSummary`，再向每个输出端口发送一次 EOF；
- 取消时等待已取出的 packet 完成安全收尾，为未配对 key 生成 `Cancelled`/人工复核结果后关闭；
- `RunCoordinator` 以所有 required output 汇聚节点完成为最终屏障，不以某一个上游 EOF 或 `PipelineExecutor` cycle 结束作为运行完成条件。

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

### 运行终态优先级

运行阶段状态与运行终态分开保存。阶段可以是 `Scanning`、`Extracting`、`Archiving` 等；终态只在最终汇总屏障后确定，并且只写入一次：

```csharp
public enum RunTerminalStatus
{
  Completed,
  PartialSuccess,
  NeedsManualReview,
  Cancelled,
  Failed
}
```

终态判定按以下优先级执行：

1. **`Failed`**：存在运行级 `RunFailure`，包括凭据不可用、数据库/审计提交失败、输出目录不可写、ZeroPipeline 图校验失败、报表生成失败和不可恢复的节点故障。运行级失败优先于候选统计。
2. **`Cancelled`**：没有更早记录的运行级失败，且用户取消已被接受。已完成的候选和已提交的归档不回滚，未处理候选产生 `Cancelled` 终态。
3. **`NeedsManualReview`**：没有运行级失败或取消，但至少一个候选为 `ManualReview`，或所有候选均未得到可用发票结果而需要人工判断。
4. **`PartialSuccess`**：没有运行级失败、取消或人工复核，但存在 `Unresolved`、`Timeout`、`QuotaExhausted` 或候选级 `AuthFailed`，表示批次完成但存在候选级失败。至少一个候选成功不是必要条件；零成功的批次也使用此状态，并通过 `NO_RESOLVED_CANDIDATES` 原因码说明。
5. **`Completed`**：没有以上条件。包括所有候选为 `Resolved`、`Duplicate` 或 `Retained`，以及扫描完成但没有候选的 `NO_CANDIDATES` 运行。

取消与失败的竞争按首次记录的原因决定：取消信号先被 `RunCoordinator` 接受且之后没有运行级失败时为 `Cancelled`；运行级失败先记录时为 `Failed`。报表、数据库提交和必需审计提交失败属于 `Failed`；邮箱断开、临时文件清理等非关键收尾失败只写入 `finalizerFailures`，不覆盖已经确定的终态。`RunSummary` 必须同时包含终态、终态原因、各 `CandidateStatus` 数量、运行级失败和收尾失败摘要。

### 业务服务接口

以下接口位于 `InvoiceFlowAI.Application`，实现位于 `InvoiceFlowAI.Infrastructure`，都只返回领域 DTO 或稳定失败结果，不向调用方泄漏供应商 SDK 类型。接口实现必须支持取消、幂等键和脱敏 trace。

#### 配对

```csharp
public sealed record PairingContext(
  string RunId,
  string ConfigurationFingerprint,
  decimal AutoAcceptScore,
  decimal ManualReviewScore);

public interface IPairingService
{
  Task<PairingBatch> PairAsync(
    IReadOnlyList<CandidateProcessResult> results,
    PairingContext context,
    CancellationToken cancellationToken);
}
```

配对只消费 `Resolved` 的 `InvoiceDocument` 和显式的 companion candidate；`Duplicate`、`Retained`、`Cancelled`、`Unresolved` 等结果原样进入 `PairingBatch.Results`。评分达到 `AutoAcceptScore` 自动配对，处于两个阈值之间进入 `ManualReview`，低于 `ManualReviewScore` 生成 `UNPAIRED_ARTIFACT` 候选结果。配对不得修改 invoice identity 或 source sequence。

配对首版规则固定为：

1. 先按文档类型建立 family 和 role：`打车 -> ride_invoice/ride_itinerary`、`住宿 -> hotel_invoice/hotel_folio`；不在同一 family 或 role 不匹配时不建边。
2. 兼容性 gate 依次检查金额、供应商、日期窗口和必要的 merchant token；供应商两侧都非空且不相等时拒绝配对。
3. 金额使用 Decimal 分；普通住宿要求差值小于 `0.01`，打车允许精确金额或价税换算后的 `0.50` slack。
4. 住宿日期差最多 3 天；打车日期用于评分，不作为缺失日期时的硬拒绝条件。
5. 建立二分图的连通分量，在每个分量内先最大化配对数量，再最大化总分；输入按 `DocumentId` 排序，保证结果确定性。

默认评分公式为：

```text
score = providerExact * 100
  + sourceMessageUidExact * 60
  + sharedMerchantTokenRatio * 40
  + amountScore
  + dateScore
```

其中 `amountScore` 为精确金额 30、打车价税关系 10、否则 0；住宿 `dateScore=max(0, 20 - days*4)`，其他交通 `dateScore=max(0, 10-min(days,10))`。默认 `AutoAcceptScore=180`、`ManualReviewScore=100`，且必须满足 `AutoAcceptScore > ManualReviewScore`。分数达到自动阈值才可自动接受；处于两个阈值之间进入人工复核，低于人工阈值为 `UNPAIRED_ARTIFACT`。

冲突规则固定为：同一 family/provider group 内的多个候选先求最优 assignment；如果存在多个“配对数量相同且总分相同”的最优 assignment，则整个连通分量不自动配对，生成一个 `PAIRING_AMBIGUOUS` 复核项。一个 `DocumentId` 在同一运行中最多参与一个 pairing；重复输入或已存在相同 pairing key 的候选转为 `Duplicate`，不产生第二个归档副作用。

跨邮件配对默认允许但不默认自动接受：只有供应商相同、金额精确、日期在允许窗口内且 merchant token 足够匹配、总分达到 `AutoAcceptScore` 时，跨 `SourceMessageUid` 的配对才可自动接受；否则必须人工复核。用户可通过配置关闭跨邮件配对，关闭后 source UID 不同直接进入 `PAIRING_CROSS_MESSAGE_DISABLED`。

#### 链接恢复

```csharp
public sealed record UrlRecoveryOptions(
  int MaxAttempts,
  TimeSpan RequestTimeout,
  long MaxDownloadBytes,
  IReadOnlySet<string> AllowedDomains,
  bool AllowBrowserFallback);

public sealed record UrlRecoveryResult(
  DocumentCandidate Candidate,
  bool Recovered,
  string LocalPath = "",
  string ContentHash = "",
  CandidateFailure? Failure = null);

public interface IUrlRecoveryService
{
  Task<UrlRecoveryResult> RecoverAsync(
    DocumentCandidate candidate,
    UrlRecoveryOptions options,
    CancellationToken cancellationToken);
}
```

非 URL candidate 直接返回 `Recovered=true`；URL 恢复成功必须验证域名、重定向链、响应大小、文件魔数和内容哈希。恢复失败返回 `CandidateFailure`，由 `RecoverUrlsNode` 转换为唯一结果端口中的 `CandidateProcessResult`。认证失败、限流和网络超时分别映射为稳定 reason code，不在服务内部无限重试。

#### 归档

```csharp
public sealed record ArchiveOptions(
  string OutputRoot,
  bool OverwriteExisting,
  bool PreserveOriginal,
  string NamingPolicyVersion);

public interface IArchiveService
{
  Task<ArchiveBatch> ArchiveAsync(
    PairingBatch pairing,
    ArchiveOptions options,
    CancellationToken cancellationToken);
}

public sealed record ArchivePreparation(
  string PreparationId,
  IReadOnlyList<ArchivedArtifact> Artifacts,
  string PreparationHash,
  string TemporaryRoot);

public interface IArchiveCommitCoordinator
{
  Task<ArchivePreparation> PrepareAsync(
    PairingBatch pairing,
    ArchiveOptions options,
    CancellationToken cancellationToken);

  Task MarkPreparedAsync(
    ArchivePreparation preparation,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task MovePreparedFilesAsync(
    ArchivePreparation preparation,
    CancellationToken cancellationToken);

  Task MarkCommittedAsync(
    ArchivePreparation preparation,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task RecoverAsync(
    string runId,
    CancellationToken cancellationToken);
}
```

归档使用 `DocumentId + NamingPolicyVersion` 作为幂等键；命名策略只接受已归一化领域字段，生成相对路径后再由路径安全组件解析到 `OutputRoot`。同哈希文件视为已归档，内容不同的同名文件按明确冲突策略生成新名称或返回 `ARCHIVE_NAME_CONFLICT`。单个归档失败只更新对应 `CandidateProcessResult`，数据库状态和审计事件必须在同一应用事务中提交。

`NamingPolicyVersion=2026-09-23-v1` 的具体规则与当前 Python 保持一致：

- 普通票据：`{Date}_{DisplayType}_{Amount}_{Seller}{Extension}`；`DisplayType` 对行程单追加“行程单”，folio 固定为“住宿水单”；
- 火车票：`{DepartureDate}-{DepartureCity}-{DestinationCity}-火车票{Extension}`；缺失城市使用“未知”；
- 不满足最低归档字段时进入 `待人工复核/NeedsReview_{OriginalFileName}`；无法识别类型进入 `待人工复核/Unrecognized_{OriginalFileName}`；
- 目录优先使用 `InvoiceDocumentType` 注册表的 `ArchiveFolder`，然后应用已通过校验的公司/供应商路由规则；不能由模型直接指定绝对目录；
- 非法字符 `\\ / : * ? " < > |`、CR/LF 替换为 `_`，连续空白压缩为一个空格，去除首尾空格、点和下划线；
- 单个 path segment 最大 120 个 UTF-16 code units，完整相对路径最大 240 个 UTF-16 code units；截断必须保留扩展名和 `DocumentId` 前 8 位；
- 同内容哈希视为 `Duplicate`；同名不同内容按 `_01`、`_02` 递增后缀处理，不能使用不可审计的随机 UUID；达到 99 后返回 `ARCHIVE_NAME_CONFLICT` 并进入人工复核。

归档采用 `Prepared -> Committed` 两阶段补偿：移动前先写 `Prepared` 记录和临时文件哈希，文件原子替换并 fsync 后再提交 `Committed`。归档成功但数据库提交失败时，启动恢复根据哈希把文件绑定回 `Prepared` 记录并重试数据库提交；数据库成功但移动失败时，记录保持 `Prepared`，不标记为已归档，由恢复任务重试移动。文件和数据库都无法证明一致时进入 `ARCHIVE_RECOVERY_FAILED`，保留文件并人工复核，禁止静默删除。

#### 报表

```csharp
public sealed record ReportRequest(
  string RunId,
  string OutputRoot,
  string ReportName,
  IReadOnlyList<CandidateProcessResult> Results,
  IReadOnlyList<ArchivedArtifact> Artifacts,
  string TemplateVersion);

public sealed record ReportExportResult(
  string ReportPath,
  string ContentHash,
  int InvoiceRowCount,
  int ManualReviewRowCount,
  string TemplateVersion);

public interface IReportExporter
{
  Task<ReportExportResult> ExportAsync(
    ReportRequest request,
    CancellationToken cancellationToken);
}
```

报表必须由统一的候选终态结果生成，至少包含汇总页、发票明细页和人工复核页；失败候选不能被静默过滤。输出路径只能由 `OutputRoot + ReportName` 经过路径安全校验得到，重复导出使用内容哈希和幂等键处理。

`TemplateVersion=2026-09-23-v1` 的工作簿 schema 固定为以下三个工作表，名称和列顺序不可变：

| 工作表 | 列顺序 |
| --- | --- |
| `运行汇总` | `RunId`、`Status`、`DateFrom`、`DateToExclusive`、`ScannedMessageCount`、`CandidateCount`、`ResolvedCount`、`DuplicateCount`、`RetainedCount`、`ManualReviewCount`、`UnresolvedCount`、`CancelledCount`、`ReportGeneratedAtUtc` |
| `发票明细` | `Sequence`、`DocumentId`、`Status`、`InvoiceDate`、`InvoiceCode`、`InvoiceNumber`、`Purchaser`、`Seller`、`DocumentType`、`Category`、`Amount`、`TaxAmount`、`TotalAmount`、`Confidence`、`PairingState`、`ArchivePath`、`ReasonCode` |
| `人工复核` | `ReviewId`、`DocumentId`、`Status`、`ReasonCode`、`SourceFileName`、`SourceKind`、`EditableFields`、`CreatedAtUtc`、`ResolvedAtUtc`、`ResolvedBy`、`Comment` |

字段映射固定为：`RunSummary` 直接映射 `运行汇总`；每个 `CandidateProcessResult` 一行映射 `发票明细`，没有 `InvoiceDocument` 的失败结果仍必须输出一行，发票字段为空；`ManualReviewItem` 映射 `人工复核`，`EditableFields` 使用按字母排序后以 `,` 连接的白名单字段名。`ArchivePath` 只能是相对路径，`ReasonCode` 使用稳定错误码，不能填异常文本。

格式固定为：

- 首行冻结、加粗、深色底白字；自动筛选覆盖完整表头；
- `DateFrom`、`DateToExclusive`、`InvoiceDate` 使用 `yyyy-mm-dd`；UTC 时间使用 `yyyy-mm-ddThh:mm:ssZ`；
- `Amount`、`TaxAmount`、`TotalAmount` 使用 Excel numeric cell 和 `0.00` 格式；空值保持空 cell，不写“未知”；
- `Confidence` 使用 numeric cell 和 `0.000` 格式；状态、类型、reason code 使用文本；
- 所有 worksheet 默认字体为 Calibri 11，表头行高 24，数据行高 20，启用自动换行但不允许内容改变列宽；
- 固定列宽：`运行汇总` 18/16/14/14/22/16/16/16/16/18/16/14/24；`发票明细` 10/66/16/14/18/22/24/28/16/18/14/14/14/12/16/48/28；`人工复核` 18/66/18/28/28/16/32/24/24/20/48；
- `ManualReview`、`Unresolved` 和 `Cancelled` 行使用浅色状态填充，但颜色不是业务判断依据；
- workbook properties 写入 `TemplateVersion`、`RunId` 和生成时间；不写入 API Key、OCR 原文、邮件正文或完整 URL。

报表 golden fixture 使用 JSON 而不是比较二进制 xlsx：

```json
{
  "templateVersion": "2026-09-23-v1",
  "runId": "run-1",
  "results": [
    {
      "sequence": 1,
      "documentId": "document-1",
      "status": "Resolved",
      "invoice": {
        "invoiceDate": "2026-09-22",
        "invoiceNumber": "12345678",
        "purchaser": "Example Co",
        "seller": "Example Seller",
        "documentType": "Catering",
        "amount": "100.00",
        "taxAmount": "6.00",
        "totalAmount": "106.00",
        "confidence": "0.950"
      },
      "archivePath": "餐饮/20260922_餐饮_106.00_Example Seller.pdf",
      "reasonCode": ""
    },
    {
      "sequence": 2,
      "documentId": "document-2",
      "status": "ManualReview",
      "invoice": null,
      "archivePath": "",
      "reasonCode": "PAIRING_AMBIGUOUS"
    }
  ],
  "reviews": [
    {
      "reviewId": "review-1",
      "documentId": "document-2",
      "status": "Open",
      "reasonCode": "PAIRING_AMBIGUOUS",
      "editableFields": ["Amount", "InvoiceDate", "Seller"]
    }
  ]
}
```

golden 测试将 fixture 映射为每张表的二维 cell matrix，并断言 sheet 名称、列顺序、行数、字段值、numeric/date 类型、number format、冻结行、筛选范围、列宽和 template version；不比较 ClosedXML 生成的 zip/XML 文件字节序。另设一条导出回读测试，用 ClosedXML 重新打开 workbook，验证没有公式错误、损坏工作表或超出输出根目录的路径。

#### 人工复核

```csharp
public enum ManualReviewDecision
{
  Accept,
  Reject,
  CorrectAndAccept,
  RetryExtraction
}

public enum ManualReviewState
{
  Open,
  Resolved,
  Rejected
}

public sealed record ManualReviewItem(
  string ReviewId,
  string RunId,
  DocumentIdentity Identity,
  CandidateProcessResult CurrentResult,
  string ReasonCode,
  int Revision,
  DateTimeOffset CreatedAtUtc,
  DateTimeOffset? ResolvedAtUtc);

public sealed record ManualReviewUpdate(
  string ReviewId,
  int ExpectedRevision,
  ManualReviewDecision Decision,
  InvoiceDocument? CorrectedInvoice = null,
  string Comment = "");

public interface IManualReviewService
{
  Task<IReadOnlyList<ManualReviewItem>> ListAsync(
    string runId,
    ManualReviewState? state,
    int offset,
    int limit,
    CancellationToken cancellationToken);

  Task<ManualReviewItem?> GetAsync(
    string reviewId,
    CancellationToken cancellationToken);

  Task<CandidateProcessResult> SubmitAsync(
    ManualReviewUpdate update,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);
}
```

人工复核提交必须校验 revision，防止两个页面覆盖彼此修改；`CorrectAndAccept` 产生新的领域 revision 并保留原始 AI 结果和修正审计；`RetryExtraction` 只能重新排队允许重试的 candidate；`Reject` 进入 `Unresolved` 或业务规定的非目标状态。人工复核不会直接写文件，重新归档和报表由后续应用命令触发。

人工复核闭环固定为：

1. `review.submit` 在一个 transaction 内锁定 `ManualReviewItems.CurrentRevision`，校验 `ExpectedRevision`、字段白名单和当前 candidate 状态；冲突返回 `REVIEW_REVISION_CONFLICT`，不覆盖已有修正。
2. `Accept` 保留原解析结果，生成新的 accepted processing revision，写入人工决定和审计事件；若原结果已有 `Prepared` 归档，则进入归档补偿队列。
3. `CorrectAndAccept` 只允许修正 `InvoiceDate`、`Purchaser`、`Seller`、`Amount`、`TaxAmount`、`TotalAmount`、`InvoiceCode`、`InvoiceNumber`、`DocumentType`、`Category`、路线字段和明细；禁止修改 `DocumentId`、来源身份、文件哈希、模型 trace 和原始附件。
4. `Reject` 的最终候选状态固定为 `Unresolved`，reason code 为 `MANUAL_REJECTED`；如果用户选择明确的非目标公司结果，则状态为 `Retained`，文档类型为 `NonTargetCompanyInvoice`，不能使用模糊的“删除”。
5. `RetryExtraction` 只允许原失败为 `Retryable=true` 且尚未超过最大次数的 candidate；它创建新的 `ProcessingRevision`，回到 `ExtractDocumentsNode`，不重复扫描邮箱或创建新 `DocumentId`。
6. 接受或修正后由 `ReviewResolutionCoordinator` 依次触发：领域校验 -> 重新配对 -> 归档/归档补偿 -> 报表增量或重生成 -> 审计事件；任一步失败都保留已提交 revision，并创建可恢复的后续任务，不回滚人工决定。
7. 修正字段使用领域校验：金额必须是有限 Decimal，日期必须是有效 `DateOnly`，金额/税额/价税合计关系必须通过 `InvoiceAcceptanceService`，文档类型必须在注册白名单中。审核人使用当前 Windows 用户的不可变审计标识和显示名快照，不接受前端自报身份。

### 运行基础设施接口

这些接口位于 `InvoiceFlowAI.Application` 的抽象边界，具体实现由 `InvoiceFlowAI.Infrastructure.Persistence`、`InvoiceFlowAI.App.Rpc` 或运行编排层提供。它们不暴露 EF Core、WebView2、MailKit 或 ZeroPipeline 类型。

#### 应用事务抽象

应用层使用显式事务对象，不使用 EF Core 的 `DbContext`、`IDbContextTransaction` 或隐式 ambient transaction：

```csharp
public enum TransactionPurpose
{
  RunCreate,
  RunTransition,
  CandidateCommit,
  CheckpointCommit,
  ArchivePrepare,
  ArchiveCommit,
  ReviewSubmit,
  EventAppend,
  Migration
}

public interface IUnitOfWorkFactory
{
  Task<IUnitOfWork> BeginAsync(
    TransactionPurpose purpose,
    CancellationToken cancellationToken);
}

public interface IUnitOfWork : IAsyncDisposable
{
  string TransactionId { get; }
  TransactionPurpose Purpose { get; }
  bool IsCompleted { get; }

  Task CommitAsync(CancellationToken cancellationToken);
  Task RollbackAsync(CancellationToken cancellationToken);
}
```

规则固定为：

- 只有 `IUnitOfWorkFactory` 可以创建事务；同一 UoW 内由一个 scoped `InvoiceFlowDbContext` 执行所有仓储写入；
- 变更仓储和 `IAuditStore` 的写方法必须显式接收同一个 `IUnitOfWork`，禁止各自开启独立事务；
- UoW 只能由创建它的应用协调器提交或回滚，提交/回滚后不可再次使用；未完成的 UoW 在 `DisposeAsync` 时回滚；
- 不允许跨线程并发使用同一个 UoW 或 `DbContext`，不允许嵌套 UoW；需要新事务时必须先结束外层事务；
- `CommitAsync` 内部按 SQLite `BEGIN IMMEDIATE`、有界 busy timeout 和一次性提交执行；锁冲突、磁盘满和约束错误映射为稳定持久化错误；
- 只读查询不强制开启 UoW，但必须使用独立短生命周期 DbContext；
- 事务边界不跨越网络请求、OCR、DeepSeek、Playwright 或文件长时间处理。外部副作用先完成准备，再通过短事务写状态和审计。

`IAuditStore`、`IRunStateStore`、人工复核提交和 checkpoint 提交使用同一 UoW 时，业务记录和审计记录要么共同提交，要么共同回滚。应用服务负责创建 UoW、调用各接口、处理异常并在一个位置决定 commit/rollback。

#### 运行状态与 checkpoint

```csharp
public sealed record RunSnapshot(
  string RunId,
  RunTerminalStatus? TerminalStatus,
  string Stage,
  string TerminalReasonCode,
  long LastEventSequence,
  IReadOnlyDictionary<string, RunCheckpoint> Checkpoints,
  RunSummary? Summary,
  int Version);

public sealed record RunCheckpoint(
  string RunId,
  string NodeId,
  string Stage,
  long LastCommittedSequence,
  string InputCursorJson,
  int OutputCount,
  string State,
  int CheckpointRevision,
  DateTimeOffset UpdatedAtUtc);

public sealed record RunTransition(
  string RunId,
  string ExpectedState,
  string NewState,
  string Stage,
  string ReasonCode = "",
  int ExpectedVersion = 0);

public interface IRunStateStore
{
  Task CreateAsync(
    RunInput request,
    string configurationFingerprint,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task<RunSnapshot?> GetAsync(
    string runId,
    CancellationToken cancellationToken);

  Task<IReadOnlyList<RunSnapshot>> ListRecoverableAsync(
    CancellationToken cancellationToken);

  Task<RunSnapshot> TransitionAsync(
    RunTransition transition,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task SaveCheckpointAsync(
    RunCheckpoint checkpoint,
    int expectedCheckpointRevision,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task CompleteAsync(
    string runId,
    RunTerminalStatus status,
    RunSummary summary,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);
}
```

`IRunStateStore` 是唯一允许改变运行状态和 checkpoint 的接口。`TransitionAsync` 必须使用 optimistic concurrency；状态版本不匹配返回 `RUN_STATE_CONFLICT`，不能覆盖另一个线程或重连页面的状态。`CompleteAsync` 是一次性终态屏障，已终态运行重复提交只能返回已有快照。checkpoint 只保存已提交 packet 的 sequence，不能保存 API Key、OCR 原文、图片或完整邮件正文。

#### 审计存储

```csharp
public sealed record AuditEvent(
  string AuditEventId,
  string RunId,
  long EventSequence,
  string EventType,
  string Stage,
  string NodeId,
  string? DocumentId,
  int? ProcessingRevision,
  string ReasonCode,
  string PayloadJson,
  string PayloadHash,
  DateTimeOffset OccurredAtUtc);

public interface IAuditStore
{
  Task AppendAsync(
    AuditEvent auditEvent,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task AppendBatchAsync(
    IReadOnlyList<AuditEvent> auditEvents,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task<IReadOnlyList<AuditEvent>> ReadRunAsync(
    string runId,
    long afterSequence,
    int limit,
    CancellationToken cancellationToken);
}
```

`IAuditStore` 只允许 append 和只读查询，不提供 update/delete。`AppendAsync`/`AppendBatchAsync` 必须加入调用方当前的应用 transaction；实现不能自行开启一个独立提交，否则业务状态成功而审计失败时会产生不可接受的不一致。审计 payload 只允许稳定 schema 和脱敏字段，原始正文、图片、秘密和完整 URL 必须在进入 store 前被拒绝。

#### 重试协调

```csharp
public sealed record RetryContext(
  string RunId,
  string Stage,
  int Attempt,
  int MaxAttempts,
  DateTimeOffset NowUtc);

public sealed record RetryDecision(
  bool Retryable,
  TimeSpan Delay,
  string ReasonCode,
  int NextAttempt,
  bool EscalateToRunFailure = false);

public interface IRetryCoordinator
{
  RetryDecision Decide(
    CandidateFailure failure,
    RetryContext context);

  Task EnqueueAsync(
    DocumentCandidate candidate,
    RetryDecision decision,
    CancellationToken cancellationToken);

  Task<IReadOnlyList<DocumentCandidate>> DequeueReadyAsync(
    string runId,
    int limit,
    CancellationToken cancellationToken);
}
```

`IRetryCoordinator` 只负责错误分类、退避、尝试次数和重新投递，不执行 parser、AI、邮箱或归档操作。取消、认证失败、非法 JSON、请求体过大和数据库错误不可盲目重试；超时、连接断开和限流按错误码与阶段策略决定。重新投递保留原 `DocumentId`、原始 `Sequence` 和 processing revision 关联，不能生成新 candidate 身份。

#### 进度发布

```csharp
public sealed record ProgressEvent(
  string RunId,
  long EventSequence,
  string EventType,
  string Stage,
  string PayloadJson,
  DateTimeOffset EmittedAtUtc);

public interface IProgressPublisher
{
  Task<ProgressEvent> PublishStageChangedAsync(
    string runId,
    string previousStage,
    string stage,
    CancellationToken cancellationToken);

  Task<ProgressEvent> PublishProgressAsync(
    string runId,
    string stage,
    int completed,
    int total,
    CancellationToken cancellationToken);

  Task<ProgressEvent> PublishDocumentResultAsync(
    string runId,
    CandidateProcessResult result,
    CancellationToken cancellationToken);

  Task<ProgressEvent> PublishTerminalAsync(
    string runId,
    RunSummary summary,
    CancellationToken cancellationToken);
}
```

`IProgressPublisher` 不修改业务状态，也不决定终态；它只将已提交状态映射为脱敏事件。发布失败不能回滚已提交业务事务，但事件必须写入 `RunEvents` 以便重放；事件序号由后端单调分配，不能由前端提供。

#### RPC 分发

```csharp
public sealed record RpcRequestEnvelope(
  string Protocol,
  string Id,
  string Method,
  string ParamsJson);

public sealed record RpcConnectionContext(
  string ConnectionId,
  string ClientVersion,
  string NegotiatedProtocol);

public sealed record RpcResponseEnvelope(
  string Protocol,
  string Id,
  bool Ok,
  string? ResultJson,
  RpcError? Error);

public sealed record RpcError(
  string Code,
  string Scope,
  bool Retryable,
  string UserMessage,
  bool DetailsAvailable,
  string? DetailsJson = null);

public interface IRpcDispatcher
{
  Task<RpcResponseEnvelope> DispatchAsync(
    RpcRequestEnvelope request,
    RpcConnectionContext connection,
    CancellationToken cancellationToken);
}
```

`IRpcDispatcher` 只负责协议版本协商、JSON/schema 校验、请求幂等键、方法路由、错误映射和调用权限边界；它不直接访问数据库或执行业务节点。`bridge.hello` 是连接初始化的唯一例外。长任务方法只返回 accepted/run ID，进度和终态通过 `IProgressPublisher` 发送；请求重复时按 `ConnectionId + request.Id` 返回已缓存响应，不重复创建运行或提交人工修改。

#### 事件回放

```csharp
public sealed record StoredRunEvent(
  string RunId,
  long EventSequence,
  string EventType,
  string PayloadJson,
  DateTimeOffset EmittedAtUtc);

public sealed record EventReplayResult(
  RunSnapshot Snapshot,
  IReadOnlyList<StoredRunEvent> Events,
  bool RequiresFullRefresh,
  long LatestSequence);

public interface IEventReplayStore
{
  Task AppendAsync(
    StoredRunEvent runEvent,
    IUnitOfWork transaction,
    CancellationToken cancellationToken);

  Task<EventReplayResult> ReadSinceAsync(
    string runId,
    long afterSequence,
    int limit,
    CancellationToken cancellationToken);

  Task CompactAsync(
    string runId,
    long throughSequence,
    CancellationToken cancellationToken);
}
```

`IEventReplayStore` 对应独立的 `RunEvents` 表，不等同于 `AuditEvents`：进度事件可以按保留策略压缩，审计事件必须长期 append-only 保存。`RunEvents` 使用 `(RunId, EventSequence)` 唯一约束；`ReadSinceAsync` 超出保留窗口时返回 `RequiresFullRefresh=true`，前端必须先使用最新 `RunSnapshot`，不能拼接不完整事件流。append 与 `Runs.LastEventSequence` 更新在同一事务内完成。

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
  RunTerminalStatus Status,
  string ReasonCode,
  CandidateStatusCounts Counts,
  int ScannedMessageCount,
  int CandidateCount,
  string? ReportPath,
  bool AuditCommitted,
  TimeSpan Duration,
  RunFailure? PrimaryFailure = null,
  IReadOnlyList<RunFailure>? FinalizerFailures = null);

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

### InvoiceAcceptanceService

`InvoiceAcceptanceService` 是 parser、DeepSeek adapter、人工复核和归档之间唯一的字段准入门。它不负责解析文件、不调用网络、不写数据库，只对已归一化的 `InvoiceDocument` 做确定性校验：

```csharp
public sealed record InvoiceAcceptancePolicy(
  decimal AmountTolerance = 0.01m,
  decimal TaxTotalTolerance = 0.01m,
  decimal MinimumConfidence = 0.60m,
  bool RequireSeller = true,
  bool RequirePurchaserForNonExemptTypes = true,
  bool AllowNegativeAmountOnlyWithCreditFlag = true);

public sealed record InvoiceAcceptanceRequest(
  DocumentCandidate Candidate,
  InvoiceDocument Document,
  InvoiceAcceptancePolicy Policy,
  string CompanyName,
  bool IsVisionFallback,
  string SourceParser);

public enum AcceptanceDisposition
{
  Accepted,
  ManualReview,
  Rejected
}

public sealed record InvoiceAcceptanceFailure(
  string ReasonCode,
  FailureCategory Category,
  bool Retryable,
  string SafeMessage,
  string Field = "");

public sealed record InvoiceAcceptanceResult(
  AcceptanceDisposition Disposition,
  InvoiceDocument? Document,
  IReadOnlyList<InvoiceAcceptanceFailure> Failures,
  IReadOnlyList<string> Warnings,
  string ReasonCode);

public interface IInvoiceAcceptanceService
{
  InvoiceAcceptanceResult Evaluate(
    InvoiceAcceptanceRequest request);
}
```

校验顺序固定为：

1. `Identity` 必须与 candidate 完全一致，`IsInvoice` 不能为 false；
2. 日期必须是有效 `DateOnly`，火车票优先使用 `Route.DepartureDate`；
3. 金额必须是有限 Decimal；负数只有在 `CreditNote`/`Cancellation` 标记存在时允许；
4. `TaxAmount`、`TotalAmount` 存在时检查 `Amount + TaxAmount`，误差不得超过 `TaxTotalTolerance`；
5. `Seller`、`InvoiceNumber`、`DocumentType` 按票据类型检查，类型必须存在于注册白名单；
6. 非豁免类型执行公司购买方关系：`target` 接受，`non_target` 转为 `NonTargetCompanyInvoice/Retained`，`unknown` 进入人工复核；
7. `Confidence < MinimumConfidence`、模型结果与确定性字段冲突、视觉 fallback 无法解释字段来源时进入人工复核，不直接接受。

失败映射固定为：`INVOICE_DATE_INVALID`、`INVOICE_AMOUNT_INVALID`、`TAX_TOTAL_MISMATCH`、`INVOICE_SELLER_MISSING`、`INVOICE_NUMBER_MISSING`、`DOCUMENT_TYPE_UNKNOWN`、`PURCHASER_NOT_TARGET`、`PURCHASER_UNKNOWN`、`DOCUMENT_NOT_INVOICE`、`ACCEPTANCE_LOW_CONFIDENCE` 和 `IDENTITY_MISMATCH`。字段格式、金额和身份错误为 `Rejected`/不可重试；低置信度、模型冲突和未知购买方为 `ManualReview`；临时依赖错误不由本服务产生，而由上游 adapter 返回 retryable candidate failure。`InvoiceAcceptanceResult` 被映射为唯一的 `CandidateProcessResult`，不抛候选级异常。

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

  ### 公司、供应商和自定义规则

  `CustomRules` 不作为任意 C#、正则脚本或模板字符串执行，而是在请求准入阶段解析为版本化、可审计的规则 AST：

  ```csharp
  public sealed record RuleSet(
    string RuleSetId,
    int Version,
    IReadOnlyList<InvoiceRule> Rules,
    string SourceFingerprint);

  public sealed record InvoiceRule(
    string RuleId,
    int Priority,
    InvoiceRuleCondition When,
    InvoiceRuleAction Then,
    bool Enabled = true);

  public sealed record InvoiceRuleCondition(
    string? ProviderFamily = null,
    InvoiceDocumentType? DocumentType = null,
    string? SellerContains = null,
    string? PurchaserRelation = null,
    string? SubjectContains = null,
    string? InvoiceNumberPrefix = null);

  public sealed record InvoiceRuleAction(
    string? ArchiveFolder = null,
    string? Category = null,
    bool? RequireManualReview = null,
    bool? AllowCrossMessagePairing = null);

  public interface IRuleSetParser
  {
    RuleSet Parse(string customRules, string ruleSetId, CancellationToken cancellationToken);
  }
  ```

  规则优先级固定为：安全/路径和 `InvoiceAcceptanceService` 准入 > 文档类型内置规则 > 供应商规则 > 公司规则 > 用户自定义路由规则。用户规则只能改变允许的归档目录、分类、人工复核标记和跨邮件配对开关，不能修改 `DocumentId`、发票金额/日期、来源身份，不能绕过非目标公司检查、最低字段检查、路径安全或模型响应 schema。规则 AST 解析失败、未知字段、未知目录或同优先级冲突返回 `RULESET_INVALID`，不能部分应用。

  公司规则使用当前 Python 的三态关系：`target`、`non_target`、`unknown`。目标公司名称规范化后做大小写不敏感包含匹配；空值、`未知购买方` 等占位值为 `unknown`。文档类型的 `ExemptFromPurchaserCheck=true` 时不因 purchaser mismatch 拒绝；其他类型的 `non_target` 进入 `NonTargetCompanyInvoice` 或用户明确配置的保留策略，`unknown` 默认人工复核。

  供应商规则通过注册表加载，注册表只接受内置程序集或经过签名/哈希校验的程序集，不从邮件或用户规则动态加载代码：

  ```csharp
  public sealed record ProviderRuleMatch(
    string ProviderId,
    string ProviderFamily,
    int Priority,
    string EvidenceCode);

  public interface IInvoiceProviderRule
  {
    string ProviderId { get; }
    int Priority { get; }

    bool CanHandle(DocumentSource source, InvoiceDocument? document);

    Task<ProviderRuleMatch?> EvaluateAsync(
      DocumentSource source,
      InvoiceDocument? document,
      CancellationToken cancellationToken);
  }

  public interface IProviderRuleRegistry
  {
    IReadOnlyList<IInvoiceProviderRule> GetOrderedRules();
  }
  ```

  供应商规则按 `Priority` 降序、`ProviderId` 升序确定性执行；多个规则匹配但给出不同 provider family 时进入 `PROVIDER_RULE_CONFLICT` 人工复核。供应商规则可以提供预期字段、配对 family 和归档目录建议，但不能直接写数据库、归档文件或覆盖人工修正。

  特殊票据 parser 使用同样的显式注册机制：

  ```csharp
  public interface IDocumentSpecialCaseParser
  {
    string ParserId { get; }
    int Priority { get; }

    bool CanParse(DocumentSource source, ParserContext context);

    Task<InvoiceParseResult?> TryParseAsync(
      DocumentSource source,
      ParserContext context,
      CancellationToken cancellationToken);
  }

  public interface IDocumentSpecialCaseParserRegistry
  {
    IReadOnlyList<IDocumentSpecialCaseParser> GetOrderedParsers();
  }
  ```

  注册顺序固定为：格式确定性 parser -> 特殊票据 parser（火车票、Folio、国外发票、供应商专用布局）-> 通用 OCR/DeepSeek fallback。`CanParse=false` 返回 null；`CanParse=true` 但解析失败必须返回稳定的 `InvoiceParseResult`，不能静默降级成普通票据。相同 priority 的多个 parser 同时匹配时返回 `SPECIAL_PARSER_CONFLICT`，交由人工复核。注册表在应用启动时冻结并记录版本指纹，运行中不能改变。

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
4. 结构化 XML 无法得到有效发票号码时，返回 `OFD_INVOICE_XML_NOT_FOUND`；若 `ParserContext.AllowOcrFallback=true`，必须进入页面渲染和 OCR，最多渲染前两页，再按统一 Track A/Track B 规则处理；若关闭 fallback，则直接生成 `ManualReview/OFD_INVOICE_XML_NOT_FOUND`。

OFD fallback 策略固定为：

- `original_invoice.xml`、Tag 或 Content XML 完整解析并通过 `InvoiceAcceptanceService` 时，禁止 OCR 和 DeepSeek；
- XML 缺失、XML 无有效发票号码、页面文本无法建立有效字段时，允许最多两页 PDFium/SkiaSharp 渲染，先 SimdPaddleOCR Track A，再 DeepSeek Track B；
- XML 字段存在互相冲突时，优先保留冲突证据并进入 `ManualReview/OFD_XML_FIELD_CONFLICT`；只有冲突字段可由同一页面 OCR 确定性解决，且 `AllowVisionFallback=true` 时才允许 DeepSeek，模型不能直接覆盖未解决的 XML 冲突；
- ZIP/XML 损坏、外部实体、路径越界、页数/文件大小超限、模型加载失败或渲染资源不可用，直接人工复核或运行级失败，不能通过 DeepSeek 绕过安全/资源错误；
- OCR/DeepSeek 结果必须再次通过 `InvoiceAcceptanceService`，失败进入人工复核，不再无限回退；
- fallback 使用与 candidate 相同的 `ProcessingRevision` 临时目录和 manifest 约束，页面超过两页不上传、不持久化、不静默截断。

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
- 首版锁定 `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` `1.4.2`，模型文件随发布包提供；
- 使用 `SkiaSharp` `4.154.0-preview.1.26454.9` 负责图像解码和像素转换。

OCR 输出和 PDF 渲染输出只在应用/基础设施内存中短暂存在，不能作为领域实体持久化：

```csharp
public sealed record DocumentImage(
  DocumentIdentity Identity,
  int PageNumber,
  int Width,
  int Height,
  int Dpi,
  string MimeType,
  ReadOnlyMemory<byte> EncodedBytes,
  string ContentHash,
  string TemporaryPath = "");

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

public sealed record PdfRenderOptions(
  int Dpi = 200,
  int MaxPages = 2,
  int MaxWidth = 4096,
  int MaxHeight = 8192,
  string OutputMimeType = "image/png");

public interface IPdfPageRenderer
{
  Task<IReadOnlyList<RenderedPage>> RenderAsync(
    DocumentSource source,
    PdfRenderOptions options,
    CancellationToken cancellationToken);
}

public interface IImagePreprocessor
{
  Task<DocumentImage> PrepareAsync(
    DocumentIdentity identity,
    RenderedPage page,
    CancellationToken cancellationToken);
}

public interface IDocumentImageLease : IAsyncDisposable
{
  DocumentImage Image { get; }
}
```

`OcrLine.Confidence` 和 `OcrDocument.Confidence` 限定在 `[0,1]`；`Ordinal` 记录同页原始识别顺序，字段提取前按页码、Y 坐标、X 坐标和 ordinal 稳定排序。`RenderedPage.EncodedBytes` 只允许存在于受控的 transient buffer，不能进入 `PipelineContext` 的持久化快照、日志、审计或 Recipe。

PDF 文字提取成功时不渲染图像。进入 OCR/视觉 fallback 后，`IPdfPageRenderer` 最多渲染前两页，默认 200 DPI，长边超过限制时等比例缩放；输出固定为 PNG，像素由 SkiaSharp 解码为连续的 non-premultiplied RGB/RGBA8 buffer，校验 stride、宽高和 content hash。首版不做不可逆二值化，只执行 EXIF/方向归一化、尺寸限制、颜色空间转换和 NFKC OCR 文本归一化。

`PaddleOcrAll` 在应用生命周期内只加载一次并复用。模型 manifest 固定模型包版本、文件相对路径、SHA-256 和引擎配置；启动时先校验 manifest，模型缺失或 hash 不匹配返回运行级 `OCR_MODEL_LOAD_FAILED`，禁止运行时静默下载未知模型。OCR 在受限的后台 worker 中运行，通过 `OcrPageConcurrency=2`、`OcrLineWorkerCount=2`、页面尺寸和 `MaxInFlightCandidates` 限制内存峰值。

模型 manifest 的格式固定为：

```json
{
  "schemaVersion": 1,
  "engine": "Sdcb.SimdPaddleOCR",
  "engineVersion": "1.4.2",
  "modelId": "ChineseV6Tiny",
  "modelPackageVersion": "1.4.2",
  "runtime": "win-x64",
  "files": [
    {
      "relativePath": "models/paddle/chinese-v6-tiny/model.pdmodel",
      "length": 0,
      "sha256": "<generated-at-build>"
    },
    {
      "relativePath": "models/paddle/chinese-v6-tiny/model.pdiparams",
      "length": 0,
      "sha256": "<generated-at-build>"
    }
  ],
  "preprocess": {
    "colorFormat": "RGBA8",
    "premultipliedAlpha": false,
    "defaultDpi": 200,
    "maxWidth": 4096,
    "maxHeight": 8192,
    "normalizeUnicode": "NFKC"
  },
  "manifestSha256": "<generated-last>"
}
```

实际发布时不允许保留 `<generated-at-build>` 或 `0`：构建脚本从 NuGet/模型资产缓存复制经过版本锁定的模型文件，按字节流计算大小和 SHA-256，使用规范化 JSON 计算 `manifestSha256`，再将 manifest 和模型一起复制到发布目录。CI 必须在干净 `win-x64` 构建中重新计算并比对 manifest，禁止从工作机已有模型目录“借用”文件。当前仓库没有这些模型二进制文件，因此真实哈希只能在模型资产下载并纳入 release 输入后生成；设计阶段不伪造哈希值。

发布验收必须验证：模型 package/version 与 `Microsoft.NET.Sdk` lock 文件一致；每个 manifest 文件存在、长度相等、SHA-256 相等；manifest 自身哈希稳定；`PaddleOcrAll` 加载的是 manifest 指定目录；任一缺失、替换、截断或版本不匹配都返回 `OCR_MODEL_LOAD_FAILED`，且不会联网下载替代模型。

`DocumentImage` 的生命周期由 `DocumentImageLease` 管理：渲染阶段在 `%LocalAppData%/InvoiceFlowAI/runs/{runId}/temp/{documentId}/{processingRevision}/pages/` 创建临时目录，文件使用随机临时名写入、flush 后原子改名；OCR、Track A 和必要的 Track B 完成且相关审计/处理结果提交后释放内存并删除临时文件。进程崩溃时启动恢复依据 checkpoint 和 processing 状态清理已完成 revision 的临时目录；未完成 revision 保留到恢复或诊断结束，不能把临时绝对路径写入领域 DTO、RPC 或日志。

首个版本使用普通的自包含 `win-x64` JIT/ReadyToRun 发布，暂不采用 Native AOT。损坏图像、像素格式不支持、渲染超时和单页读取失败转为候选级 `OCR_IMAGE_INVALID`/`PDF_RENDER_FAILED`；模型加载、原生库加载和统一内存分配失败属于运行级故障。

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

数据库使用 UTF-8 SQLite，所有 UTC 时间存为 ISO-8601 text，金额存为 decimal scaled integer 或 invariant text，不能使用 binary floating point。领域模型和 EF entity 分离；下面的表结构是首版 schema 契约，列名可以在实现中采用 PascalCase，但迁移必须保持语义一致。

| 表 | 主键和字段 | 外键、索引和约束 |
| --- | --- | --- |
| `Runs` | `RunId TEXT`、`State TEXT`、`Stage TEXT`、`TerminalReasonCode TEXT`、`DateFrom TEXT`、`DateToExclusive TEXT`、`AccountId TEXT`、`Mailbox TEXT`、`OutputRoot TEXT`、`ConfigurationFingerprint TEXT`、`RecipeVersion TEXT`、`StartedAtUtc TEXT`、`EndedAtUtc TEXT`、`CancellationRequestedAtUtc TEXT`、`LastEventSequence INTEGER`、`SummaryJson TEXT`、`PrimaryFailureJson TEXT`、`CreatedAtUtc TEXT` | `RunId` 主键；`ConfigurationFingerprint`、`State`、`Stage` 有普通索引；非终态运行建立 partial unique index，保证首版最多一个活动运行。 |
| `Documents` | `DocumentId TEXT`、`SourceKind TEXT`、`SourceMessageUid TEXT`、`SourceFileName TEXT`、`SourceLocator TEXT`、`ProviderGroupKey TEXT`、`ContentHash TEXT`、`MimeType TEXT`、`CreatedAtUtc TEXT` | `DocumentId` 主键；`ContentHash` 可为空但非空时唯一；`(SourceKind, SourceLocator)` 非空时唯一；禁止保存原始授权 URL。 |
| `DocumentProcessing` | `DocumentId TEXT`、`ProcessingRevision INTEGER`、`RunId TEXT`、`Sequence INTEGER`、`Stage TEXT`、`Status TEXT`、`ReasonCode TEXT`、`Retryable INTEGER`、`Attempt INTEGER`、`MaxAttempts INTEGER`、`ArtifactPath TEXT`、`ResultJson TEXT`、`TraceJson TEXT`、`StartedAtUtc TEXT`、`CompletedAtUtc TEXT`、`UpdatedAtUtc TEXT` | 复合主键 `(DocumentId, ProcessingRevision)`，即幂等键；`RunId` 外键；`(RunId, Sequence)` 唯一；`(RunId, DocumentId)` 当前 revision 索引；结果 JSON 禁止原文、图片和凭据。 |
| `Invoices` | `InvoiceId TEXT`、`DocumentId TEXT`、`ProcessingRevision INTEGER`、`InvoiceDate TEXT`、`Purchaser TEXT`、`Seller TEXT`、`Amount TEXT`、`TaxAmount TEXT`、`TotalAmount TEXT`、`InvoiceCode TEXT`、`InvoiceNumber TEXT`、`DocumentType TEXT`、`Category TEXT`、`Flags INTEGER`、`Confidence TEXT`、`DuplicateKey TEXT`、`ArchiveState TEXT`、`Revision INTEGER` | `(DocumentId, ProcessingRevision)` 外键；`InvoiceId` 主键；`DuplicateKey` 非空时普通索引；`(InvoiceNumber, Seller, InvoiceDate, TotalAmount)` 查询索引；不以发票号码单独做唯一约束。 |
| `InvoiceItems` | `InvoiceItemId TEXT`、`InvoiceId TEXT`、`Ordinal INTEGER`、`Name TEXT`、`Specification TEXT`、`Unit TEXT`、`Quantity TEXT`、`UnitPrice TEXT`、`Amount TEXT`、`TaxRate TEXT`、`TaxAmount TEXT` | `InvoiceId` 外键；主键 `InvoiceItemId`；`(InvoiceId, Ordinal)` 唯一；删除发票时级联删除明细。 |
| `Pairings` | `PairingId TEXT`、`RunId TEXT`、`InvoiceDocumentId TEXT`、`InvoiceProcessingRevision INTEGER`、`CompanionDocumentIdsJson TEXT`、`CompanionProcessingRevisionsJson TEXT`、`Score TEXT`、`State TEXT`、`ReasonCode TEXT`、`CreatedAtUtc TEXT`、`UpdatedAtUtc TEXT` | `RunId` 和 invoice processing 复合外键；`(RunId, InvoiceDocumentId, InvoiceProcessingRevision)` 唯一；companion revision 映射必须完整，不能只保存 document ID。 |
| `ArchivedArtifacts` | `ArtifactId TEXT`、`RunId TEXT`、`DocumentId TEXT`、`ProcessingRevision INTEGER`、`Role TEXT`、`RelativePath TEXT`、`FileName TEXT`、`ContentHash TEXT`、`State TEXT`、`AlreadyExisted INTEGER`、`CreatedAtUtc TEXT`、`CommittedAtUtc TEXT` | processing 复合外键；`ArtifactId` 主键；`(RunId, DocumentId, ProcessingRevision, Role, ContentHash)` 唯一；`RelativePath` 只保存相对输出路径。 |
| `RunCheckpoints` | `RunId TEXT`、`NodeId TEXT`、`Stage TEXT`、`LastCommittedSequence INTEGER`、`InputCursorJson TEXT`、`OutputCount INTEGER`、`State TEXT`、`CheckpointRevision INTEGER`、`UpdatedAtUtc TEXT` | 复合主键 `(RunId, NodeId)`；`RunId` 外键；`(RunId, Stage, LastCommittedSequence)` 索引；cursor JSON 不得包含秘密或原始正文。 |
| `MailboxCursors` | `AccountId TEXT`、`Mailbox TEXT`、`UidValidity INTEGER`、`LastCompletedUid INTEGER`、`CursorRevision INTEGER`、`UpdatedAtUtc TEXT` | 复合主键 `(AccountId, Mailbox)`；`UidValidity` 变化时必须重置 `LastCompletedUid`。 |
| `AuditEvents` | `AuditEventId TEXT`、`RunId TEXT`、`EventSequence INTEGER`、`EventType TEXT`、`Stage TEXT`、`NodeId TEXT`、`DocumentId TEXT`、`ProcessingRevision INTEGER`、`ReasonCode TEXT`、`PayloadJson TEXT`、`PayloadHash TEXT`、`OccurredAtUtc TEXT` | `AuditEventId` 主键；`RunId` 外键；`(RunId, EventSequence)` 唯一；`(DocumentId, ProcessingRevision, EventType)` 索引；append-only，不允许 update/delete。 |
| `RunEvents` | `RunId TEXT`、`EventSequence INTEGER`、`EventType TEXT`、`PayloadJson TEXT`、`EmittedAtUtc TEXT`、`ExpiresAtUtc TEXT` | 复合主键 `(RunId, EventSequence)`；`RunId` 外键；`(RunId, ExpiresAtUtc)` 索引；允许按保留策略 compact，但不得改变既有 sequence。 |
| `ManualReviewItems` | `ReviewId TEXT`、`RunId TEXT`、`DocumentId TEXT`、`ProcessingRevision INTEGER`、`ReasonCode TEXT`、`State TEXT`、`CurrentRevision INTEGER`、`CurrentResultJson TEXT`、`CreatedAtUtc TEXT`、`ResolvedAtUtc TEXT`、`ResolvedBy TEXT` | processing 复合外键；`ReviewId` 主键；同一 processing revision 只能有一个 open review 的 partial unique index；`CurrentRevision` 用于 optimistic concurrency。 |

`Runs`、`Documents` 和 `DocumentProcessing` 使用不同层次的身份：`DocumentId` 表示跨运行稳定来源身份，`ProcessingRevision` 表示一次处理尝试/结果版本，`RunId` 表示本次运行。所有写入文档处理结果的入口必须使用 `(DocumentId, ProcessingRevision)` 做 upsert；重复投递只能返回已提交结果，不能新增第二份发票、归档或审计结果。新的运行创建新的 revision，人工修正也创建新的 revision 并保留原 revision。

数据库关系要求：删除运行不级联删除全局 `Documents` 或历史审计；首版只允许删除用户明确选择的运行索引和非审计临时数据。删除 `DocumentProcessing` 前必须不存在发票、配对、归档或人工复核引用。所有外键启用 `PRAGMA foreign_keys=ON`，所有 ID 和 enum 值由 `DomainValidation` 在入库前校验。

### 事务、审计与外部文件一致性

数据库写入使用显式事务，单个 candidate 的以下变更必须在同一 SQLite transaction 内提交：`DocumentProcessing` 终态、`Invoices`/`InvoiceItems`、`ManualReviewItems`、`ArchivedArtifacts` 的数据库状态和对应 `AuditEvents`。`AuditEvents` 是同一事务中的 append-only outbox，不是 Serilog 日志；事务回滚时业务数据和审计事件一起回滚。

文件系统移动不能参加 SQLite transaction，因此归档采用两阶段状态：

1. 事务 A 写入 `ArchivedArtifacts(State=Prepared)` 和审计事件，并保存临时文件哈希；
2. 在受控输出目录内完成原子 rename/replace 并 fsync；
3. 事务 B 校验最终文件哈希，将状态改为 `Committed` 并追加审计事件；
4. 崩溃恢复根据 `Prepared` 记录、临时文件和最终文件哈希决定继续提交、清理临时文件或生成 `ARCHIVE_RECOVERY_FAILED`。

报表文件同样先写临时文件，完成哈希和原子替换后，才在事务中保存 `ReportPath` 和 `ReportHash`。任何数据库提交成功但外部文件未完成的记录都必须可由启动恢复扫描，不得标记为已归档。

### Checkpoint、重启恢复和迁移

每个 ZeroPipeline 节点只在其输入 packet 已成功处理、输出 packet 已写入持久化边界且相关 `AuditEvents` 已提交后更新 `RunCheckpoints.LastCommittedSequence`。checkpoint 更新与该 packet 的业务结果使用同一 transaction；未提交的 packet 在恢复时允许重新执行，依靠 `(DocumentId, ProcessingRevision)` 幂等键消除重复副作用。

应用启动时按以下顺序恢复：

1. 获取单实例迁移/恢复锁，运行 `PRAGMA foreign_keys=ON`、WAL 配置和 `PRAGMA integrity_check`；
2. 检查所有非终态 `Runs` 和 `RunCheckpoints`，将上次进程中断的节点标记为 `Recovering`；
3. 验证 checkpoint 的 run、node、sequence、cursor 和配置指纹；
4. 校验 `Prepared` 归档与临时文件/最终文件哈希；
5. 从最后一个已提交 sequence 重新投递未提交输入，恢复游标和节点执行；
6. 如果 checkpoint、文件哈希或配置指纹无法验证，运行终止为 `RUN_RECOVERY_FAILED`，保留数据库和现场，不静默丢弃数据。

使用 EF Core migrations 管理结构。发布包只包含已审查的 migration，启动时在独占迁移锁内按顺序执行；禁止 `EnsureCreated`、自动删除数据库、降级 migration 或运行时生成未知 schema。EF 的 `__EFMigrationsHistory` 是唯一迁移版本来源，发布版本同时记录应用 schema compatibility range。

迁移前将 SQLite 主文件、`-wal` 和 `-shm` 在 checkpoint/关闭连接后复制到带版本的备份目录，并记录备份哈希。迁移失败时回滚当前事务、保留失败诊断、阻止新运行并返回 `DB_MIGRATION_FAILED`；只有用户明确确认且备份校验成功时才允许恢复上一版本。数据库损坏或 `integrity_check` 失败时返回 `DB_CORRUPTED`，先复制只读诊断副本，再尝试最近的完整备份；不能自动新建空数据库替代原库。

检测到 `SQLITE_FULL`、日志目录或临时目录磁盘不足时，停止新的 packet，回滚当前事务，写入可用的启动/诊断日志并返回 `PERSISTENCE_DISK_FULL`。恢复足够空间后，依据最近 checkpoint 重试；已提交事务不回滚，未提交事务不产生业务结果。SQLite busy timeout、WAL、连接重试和批量大小均配置化，但不能通过无限重试掩盖锁死或磁盘故障。

单次运行使用独立的 `DbContext` 生命周期，禁止跨线程共享 `DbContext`。批处理使用有界批量写入，避免逐条提交造成性能和锁竞争问题。日志写入不参与业务事务，Serilog 失败也不能改变已提交业务状态；审计写入失败则按运行级 `Failed` 处理。

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
- 链接恢复：使用 `Microsoft.Playwright` `1.62.0` 和供应商专用下载适配器；
- 报表：使用 `ClosedXML` `0.105.1` 生成发票汇总、明细和人工复核工作簿；
- PDF：使用 `PdfPig` `0.1.17-alpha-202609192350-df33d` 进行文本提取和页面判断，使用 `PDFiumCore` `155.0.8057` 将扫描页面渲染为 OCR 图像；
- OFD：本文档规定的专用 ZIP/XML 发票解析器；
- 凭据：Windows DPAPI `CurrentUser` 保护器，参考 `Lyntai.Secrets.Dpapi`；
- 持久化：EF Core 10 + `Microsoft.EntityFrameworkCore.Sqlite`；
- 日志：Serilog 结构化日志，通过 `Microsoft.Extensions.Logging` 注入；
- AI：由 `Microsoft.Extensions.AI.OpenAI` 提供 `IChatClient`，接入 DeepSeek 的 OpenAI 兼容端点。

### Windows 11 x64 发布工程

首版采用 Windows App SDK `1.8.250916001`、`net10.0-windows`、`win-x64`、self-contained、unpackaged 应用。应用本身不使用 MSIX；安装、升级和卸载由同一 ProductCode 的 WiX MSI 完成。发布产物分为签名安装器和可诊断的安装目录 manifest，不允许从开发机全局路径加载 native DLL、浏览器或 OCR 模型。

发布目录固定为：

```text
InvoiceFlowAI/
  InvoiceFlowAI.exe
  InvoiceFlowAI.App.dll
  runtimes/win-x64/native/pdfium/pdfium.dll
  runtimes/win-x64/native/skia/libSkiaSharp.dll
  webview2/FixedVersionRuntime/       # 与 1.0.4255-prerelease 匹配
  models/paddle/chinese-v6-tiny/
    model-manifest.json
    *.pdmodel
    *.pdiparams
  browsers/playwright/chromium/
    browser-manifest.json
    chrome.exe
    ...
  web/index.html
  licenses/THIRD-PARTY-NOTICES.txt
  release-manifest.json
```

`release-manifest.json` 固定应用版本、Git revision、RID、每个 native 文件的 SHA-256、WebView2 Fixed Runtime 版本、OCR 模型版本/哈希、Playwright Chromium revision 和许可证清单版本。启动诊断只记录 manifest 校验结果，不记录密钥或发票内容。

资源加载规则：

- PDFium 使用显式绝对发布目录加载，调用前验证 DLL 哈希和架构；禁止依赖 PATH 或系统安装的 PDFium；
- SkiaSharp native asset 从 `runtimes/win-x64/native/skia` 加载，验证 x64、版本和 `SKBitmap` stride/通道后才交给 OCR；
- OCR `ChineseV6Tiny` 模型只从 `models/paddle/chinese-v6-tiny` 加载，manifest 或 SHA-256 不匹配时返回 `OCR_MODEL_LOAD_FAILED`；
- Playwright 不在用户机器上执行 `playwright install`，构建阶段下载并固定 Chromium revision，运行时使用 manifest 中的 `ExecutablePath`；
- WebView2 使用 Fixed Version Runtime，通过 `CoreWebView2Environment.CreateAsync` 指定 `webview2/FixedVersionRuntime`；不回退到 Evergreen，避免版本漂移；
- 所有临时文件、SQLite、日志、secret 和用户输出目录仍位于 `%LocalAppData%/InvoiceFlowAI` 或用户显式选择的输出目录，不写入安装目录。

签名和安装规则：

- `InvoiceFlowAI.exe`、所有自有 DLL、WiX MSI 和 bootstrapper 使用 SHA-256 Authenticode 签名，并使用 RFC 3161 时间戳；
- 发布证书只在 CI/发布机使用，证书私钥、thumbprint 和签名密码不进入仓库；开发构建允许未签名，但生产 manifest 必须标记签名状态；
- WiX MSI 使用稳定 ProductCode，升级通过递增 ProductVersion/PackageCode 原地升级，不允许同一版本并行安装；
- 升级前保留数据库、secrets、日志和用户输出，升级失败自动回滚应用文件但不覆盖用户数据；
- 卸载移除程序文件、快捷方式和 Fixed Runtime，但默认保留 `%LocalAppData%/InvoiceFlowAI` 数据；提供显式“同时删除用户数据”选项并二次确认；
- 安装器必须校验 Windows 11 x64、VC++/Windows App SDK 运行时依赖、Fixed Runtime 和发布 manifest，失败时不注册半成品安装。

第三方许可证清单至少包含：Windows App SDK、WebView2 Fixed Runtime、ZeroPipeline、MailKit/MimeKit、EF Core、PDFiumCore/PDFium、PdfPig、SkiaSharp、SimdPaddleOCR 及 `ChineseV6Tiny` 模型、Microsoft.Playwright/Chromium、ClosedXML 和所有传递依赖。清单由 lock file、native manifest 和模型 manifest 生成，并随安装器发布。

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
- 按日期范围、发件人和主题规则筛选邮件；
- 递归读取 MIME、附件和受限深度的嵌套 ZIP；
- 将附件转换为 `DocumentCandidate`，不把 MimeKit 对象泄漏到领域层；
- 支持 `CancellationToken`、连接超时、断线重连和单封邮件失败隔离；
- 对邮件 UID、附件哈希和 Message-ID 去重，避免重复下载；
- 不记录授权码、邮件正文、完整附件 URL 或完整邮件头。

#### 账户、扫描请求和游标

```csharp
public sealed record MailboxAccount(
  string AccountId,
  string EmailAddress,
  string ImapHost,
  int ImapPort = 993,
  bool UseTls = true,
  string CredentialName = "mail.imap.auth-code",
  string DisplayName = "");

public sealed record MailboxFilterRules(
  IReadOnlySet<string>? AllowedSenderAddresses = null,
  IReadOnlySet<string>? AllowedSenderDomains = null,
  IReadOnlySet<string>? BlockedSenderDomains = null,
  IReadOnlyList<string>? SubjectKeywords = null,
  bool CaseInsensitive = true);

public sealed record AttachmentFilterOptions(
  IReadOnlySet<string> AllowedExtensions,
  long MaxAttachmentBytes = 5 * 1024 * 1024,
  int MaxZipDepth = 3,
  int MaxZipMembersPerArchive = 256,
  long MaxExpandedBytesPerArchive = 50 * 1024 * 1024,
  long MaxExpandedBytesPerMessage = 100 * 1024 * 1024,
  bool KeepFilteredOuterArchive = true);

public sealed record MailboxScanCursor(
  string AccountId,
  string Mailbox,
  uint UidValidity,
  uint LastCompletedUid,
  DateTimeOffset UpdatedAtUtc);

public sealed record MailboxScanRequest(
  MailboxAccount Account,
  DateOnly Since,
  DateOnly? BeforeExclusive,
  string Mailbox,
  string BusinessTimeZoneId,
  MailboxFilterRules Rules,
  AttachmentFilterOptions Attachments,
  MailboxScanCursor? Cursor = null,
  int HeaderBatchSize = 200,
  int MessageBatchSize = 25,
  int MaxAttempts = 2);

public sealed record MailboxScanBatch(
  MailboxMessageBatch Messages,
  MailboxScanCursor Cursor,
  IReadOnlyList<string> UnresolvedUidHashes,
  bool IsFinalBatch);

public interface IMailboxScanner
{
  IAsyncEnumerable<MailboxScanBatch> ScanAsync(
    MailboxScanRequest request,
    CancellationToken cancellationToken);
}
```

默认 `AllowedExtensions` 为 `.pdf`、`.ofd`、`.xml`、`.jpg`、`.jpeg`、`.png` 和 `.zip`，比较时统一转为小写并以文件魔数复核，不能只信任扩展名。单个直接附件或 ZIP 成员超过 5 MiB 时不解包为正常 candidate；在 `KeepFilteredOuterArchive=true` 时保留外层文件并产生 `ATTACHMENT_OVER_SIZE` 或 `ZIP_MEMBER_OVER_SIZE` 的候选结果。ZIP 超过深度、成员数或展开总量限制时停止继续展开，保留外层容器并产生 `ZIP_LIMIT_EXCEEDED`，禁止 Zip Slip、绝对路径、符号链接和重复展开。

日期语义与 Python 保持一致：`Since` 包含，`BeforeExclusive` 不包含；邮件 `Date` 头优先，缺失或非法时使用 IMAP `INTERNALDATE`；先转换到 `BusinessTimeZoneId`，首版固定为 `Asia/Shanghai`，再比较本地日期。`BeforeExclusive` 必须晚于 `Since`。日期过滤不能使用客户端机器的本地时区。

扫描先以 UID 搜索建立稳定范围，再按 `HeaderBatchSize` 读取日期/内部日期，按 `MessageBatchSize` 拉取 RFC822 内容。只有一个 message batch 成功完成并将 `LastCompletedUid` 持久化后，游标才能前进。`UIDVALIDITY` 变化时旧游标失效，必须从新的 mailbox 范围重新扫描，不能继续使用旧 UID。未完成 UID 使用短 SHA-256 诊断指纹记录；重试耗尽后无法建立完整扫描范围时，扫描返回运行级 `MAILBOX_INPUT_UNRESOLVED`，已读取的 candidate 只作为诊断保留。

#### 认证和失败边界

邮箱认证属于运行级边界：

- 凭据缺失、DPAPI 解密失败、IMAP `AUTHENTICATE` 返回 401/登录失败、TLS 证书验证失败或 `SELECT` 目标 mailbox 失败，产生 `MAILBOX_CREDENTIALS_INVALID`、`MAILBOX_TLS_FAILED` 或 `MAILBOX_SELECT_FAILED`，运行终态为 `Failed`，不能按候选级失败继续处理；
- DNS、连接断开、临时服务器错误和超时可以按 `MaxAttempts` 重试，耗尽后产生 `MAILBOX_CONNECTION_FAILED`，仍属于运行级 `Failed`；
- 单封邮件 MIME 损坏、单个附件解压失败或单个附件超过限制，不影响其他邮件，转换为该 candidate 的 `CandidateProcessResult`；
- `CancellationToken` 取消只停止扫描和后续投递，已完成 batch 的游标保持有效，运行按统一取消规则结束。

DeepSeek 认证边界不同：DeepSeek API Key 是整个运行共享的基础设施凭据，HTTP 401/403、API Key 缺失或 `AI_AUTHENTICATION_FAILED` 应立即停止新的 AI 请求并作为运行级 `Failed`；不能把同一失效 Key 重试成多个候选级 `AuthFailed`。DeepSeek 超时、连接失败、限流、非法 JSON 和图片过大仍按 adapter 规则产生候选级结果或可重试结果；只有服务策略明确判定为全局额度/服务不可用时，才升级为运行级失败。邮箱认证和 DeepSeek 认证都不得把原始响应、授权值或完整请求写入日志/RPC。

IMAP 测试使用 MailKit 可替换的传输/协议边界或本地测试服务器，覆盖 TLS 失败、认证失败、分页、重复邮件、嵌套附件、损坏 MIME、断线重连和取消；还必须验证 `UIDVALIDITY` 变化会重置游标、`LastCompletedUid` 只在完整 batch 提交后推进、`Asia/Shanghai` 日期边界的包含/排除规则、发件人/主题筛选、5 MiB 附件限制、ZIP 深度/成员数/展开字节数限制，以及未完成 UID 导致运行级 `MAILBOX_INPUT_UNRESOLVED`。DeepSeek 测试必须验证 401/403 直接升级运行级失败且不会为每个候选重复重试。

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

错误类别包括输入校验、单文件失败、可重试的网络/AI 失败、邮箱认证、AI 认证、配对、规则、归档、人工复核、OCR、持久化、磁盘、WebView2 和系统错误。邮箱扫描至少使用 `MAILBOX_CREDENTIALS_INVALID`、`MAILBOX_TLS_FAILED`、`MAILBOX_SELECT_FAILED`、`MAILBOX_CONNECTION_FAILED`、`MAILBOX_INPUT_UNRESOLVED`、`ATTACHMENT_OVER_SIZE`、`ZIP_LIMIT_EXCEEDED`；AI 认证使用 `AI_AUTHENTICATION_FAILED`；配对/规则使用 `PAIRING_AMBIGUOUS`、`PAIRING_CROSS_MESSAGE_DISABLED`、`RULESET_INVALID`、`PROVIDER_RULE_CONFLICT`、`SPECIAL_PARSER_CONFLICT`；归档/复核/OCR 使用 `ARCHIVE_NAME_CONFLICT`、`ARCHIVE_RECOVERY_FAILED`、`REVIEW_REVISION_CONFLICT`、`OCR_MODEL_LOAD_FAILED`、`OCR_IMAGE_INVALID`、`PDF_RENDER_FAILED`；持久化使用 `PERSISTENCE_DISK_FULL`、`DB_CORRUPTED`、`DB_MIGRATION_FAILED` 和 `RUN_RECOVERY_FAILED`。面向用户的消息安全且可本地化；诊断信息只保留脱敏后的技术细节。

## 11. 测试与验收

### 单元测试

覆盖领域校验、金额和税额计算、规则 AST/优先级、公司/供应商冲突、配对兼容性/评分/多最优解、查重、归档命名/清洗/冲突后缀、路径安全、人工复核字段白名单和 revision、三类 parser 契约、特殊 parser registry、OFD XML 变体、DocumentImage 生命周期、PDF 渲染和 OCR 归一化、模型 manifest hash 校验、DeepSeek 响应校验、DPAPI 密钥存储、Serilog 日志字段和报表映射。

### 集成测试

验证 WebView2 RPC 分发、完整本地文件链路、真实 OFD 样本、假的 `IChatClient` 响应、重试/取消、EF Core SQLite 持久化、迁移、IUnitOfWork commit/rollback、业务数据与审计原子性、RunEvents 序号并发、Prepared/Committed 归档恢复、并发写入和 Excel 生成。三类 parser 还必须用当前 Python 样本和 QingPiao 样本做行为对照：字段等价、人工复核原因稳定、PDF 多发票切分一致、XML/OFD 优先级一致。

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
