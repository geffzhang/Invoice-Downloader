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

- `ZeroPipeline.Core`：DAG、拓扑调度、typed ports、背压和执行器；
- `ZeroPipeline.Recipe`：JSON Recipe 序列化、节点注册和图构建。

ZeroPipeline 参考仓库：<https://github.com/kzxl/ZeroPipeline/tree/master>。
其核心包支持 `net8.0` 和 `netstandard2.0`，可由 .NET 10 应用引用。暂不使用 `ZeroPipeline.UI`，因为本项目的界面由 WinUI 3 + WebView2 承载，不能把 WinForms 画布控件作为 UI 基础。

### 职责边界

- `InvoiceFlowAI.App`：WinUI 窗口、WebView2 初始化、生命周期、DPI、打包和桥接接线。不包含发票业务规则。
- `InvoiceFlowAI.Contracts`：可 JSON 序列化的命令、事件、DTO、稳定错误码和前端契约。
- `InvoiceFlowAI.Application`：使用 ZeroPipeline 构建运行 DAG，负责准入校验、取消、重试策略、阶段转换、并发限制和进度发送。
- `InvoiceFlowAI.Domain`：发票实体、解析结果、分类规则、配对规则、校验和真值契约。该层不依赖 WebView2、HTTP、数据库或供应商 SDK。
- `InvoiceFlowAI.Infrastructure`：IMAP、文档、OCR、AI、浏览器、归档、报表、持久化、凭据和日志等具体实现。

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

## 6. 文档处理与 OFD 解析

文档层提供独立接口：

```csharp
public interface IInvoiceParser
{
    Task<IReadOnlyList<InvoiceParseResult>> ParseAsync(
        DocumentSource source,
        CancellationToken cancellationToken);
}
```

PDF 优先使用文本提取；对于纯图片 PDF，则渲染页面后进入 OCR。XML 使用安全 XML 读取器，并将支持的发票 XML 结构映射为领域对象。

### OFD 解析要求

OFD 必须保留专用的发票结构解析器，不能只依赖通用文本抽取。

实现参考 `E:/GitHub/qingpiao/src/QingPiao/Parsers/OfdInvoiceParser.cs` 的行为，但不把该项目作为运行时依赖：

1. 将 OFD 作为受限 ZIP 包打开；
2. 优先解析 `Doc_0/Attachs/original_invoice.xml`，用于纸电票/电子发票结构；
3. 若不存在，则检查 `Doc_0/Tags/Tag.xml` 和 `Doc_0/Tags/CustomTag.xml`；
4. 加载 `Doc_0/Pages/Page_0/Content.xml`，建立 `Object ID -> TextObject` 映射以解析 `ObjectRef`；
5. 提取发票号码、开票日期、购买方、销售方、金额、税额和明细；
6. 发票数据缺失或不完整时返回人工复核结果，不伪造字段。

解析器必须禁用 DTD 和外部实体，拒绝 ZIP 路径穿越，限制条目数量和解压总大小，并兼容参考实现覆盖的 XML 命名空间差异。测试必须覆盖三种 XML 路径、数据缺失、XML 损坏、ZIP 损坏、超大压缩包和发票号码为空等情况。

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

- `Sdcb.SimdPaddleOCR`；
- 初始使用 `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` 等适合中文发票的模型包；
- 使用 ImageSharp 或 SkiaSharp 负责图像解码和像素转换。

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

```csharp
public interface IInvoiceFieldExtractor
{
    Task<InvoiceExtractionResult> ExtractAsync(
        OcrDocument ocr,
        CancellationToken cancellationToken);
}
```

文本请求包含 OCR 文本和坐标；多模态请求包含严格的提取提示词和图片 `DataContent`。两条路径共用类型化响应校验和本地业务校验。模型名称、端点、温度、token 上限、超时时间和重试策略均配置化；实现前必须根据目标 DeepSeek 账号验证具体的 DeepSeek Flash 模型标识。

系统先尝试基于 OCR 文本的提取。如果 OCR 置信度过低、必填字段缺失或本地校验失败，则将原始页面图像发送给 DeepSeek Flash 进行多模态复核。新方案不包含 GLM 的配置、包引用、错误码或运行时路径。

远程 AI 错误必须转换为稳定错误，包括超时、认证失败、限流、额度耗尽、响应无效和不支持多模态输入。单元测试使用假的 `IChatClient`，不需要真实 API Key。

## 8. 存储与安全

使用 SQLite 保存运行索引、发票历史、审计事件和人工复核状态。应用数据保存到 `%LocalAppData%/InvoiceFlowAI`；用户选择的发票和报表保存到指定输出目录。

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
- PDF：文本提取和需要 OCR 时的页面渲染；
- OFD：本文档规定的专用 ZIP/XML 发票解析器；
- 凭据：Windows DPAPI `CurrentUser` 保护器，参考 `Lyntai.Secrets.Dpapi`；
- 日志：Serilog 结构化日志，通过 `Microsoft.Extensions.Logging` 注入；
- AI：由 `Microsoft.Extensions.AI.OpenAI` 提供 `IChatClient`，接入 DeepSeek 的 OpenAI 兼容端点。

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

覆盖领域校验、金额和税额计算、规则、配对、查重、归档命名、路径安全、OFD XML 变体、OCR 归一化、DeepSeek 响应校验、DPAPI 密钥存储、Serilog 日志字段和报表映射。

### 集成测试

验证 WebView2 RPC 分发、完整本地文件链路、真实 OFD 样本、假的 `IChatClient` 响应、重试/取消、SQLite 持久化、审计事件和 Excel 生成。

### Windows 端到端测试

在干净的 Windows 11 x64 环境中验证首次启动、WebView2 加载、取消、网络中断和重试、大批量处理、WebView2 故障后的恢复、自包含启动、OCR 模型随包加载，以及不依赖 Python。

验收以行为而非代码翻译相似度为标准：

- 相同邮箱样本得到等价的候选集合；
- 相同 PDF/OFD/XML 样本得到等价的发票字段和人工复核分类；
- 配对、归档、查重、报表和审计结果正确；
- 所有失败场景都有稳定且可解释的错误；
- 在干净的 Windows 11 机器上，只配置必要的 DeepSeek 信息即可运行发布包。

## 12. 风险与待验证事项

- 通过目标端点验证 DeepSeek Flash 的具体模型标识和多模态内容格式；
- 验证 DPAPI 当前用户作用域、应用 entropy、密文损坏和用户/机器迁移失败行为；
- 验证 Serilog 日志结构、滚动保留、异常事件、取消事件和敏感字段脱敏；
- 测试 SimdPaddleOCR 对小字体、旋转、低分辨率和扫描发票的识别效果；
- 验证 PDF/OFD 渲染质量是否满足 OCR 要求；
- 使用 `original_invoice.xml` 和 `Tag.xml`/`CustomTag.xml` 两类样本验证 OFD 解析；
- 确认 Playwright 供应商流程和打包后的浏览器行为；
- 测量受限 OCR 并发下的内存占用；
- 确认中文 Excel 列宽和自包含发布包体积。

这些是实现阶段的验证任务，不构成改变已批准架构的理由。
