# .NET 10 InvoiceFlowAI 迁移实施计划

> **供 Agentic Worker 使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`，逐项执行本计划。所有步骤使用复选框 `- [ ]` 跟踪。

**目标：** 按已批准的迁移规格构建 Windows 11 x64 .NET 10 Avalonia.Controls.WebView InvoiceFlowAI 应用，在保持 Python 业务行为契约的同时，替换为 C# 服务、ZeroPipeline、SQLite、DeepSeek、本地 OCR 和受控 JSON/RPC。

**架构：** 使用五层项目结构：Domain 负责不可变业务模型和确定性校验；Contracts 负责 JSON/RPC 与持久化安全 DTO；Application 负责编排、运行终态、仓储和应用服务；Infrastructure 负责 MailKit、EF Core、OCR、PDF/OFD、DeepSeek、Playwright、归档、报表、DPAPI 和日志适配器；App 负责 Avalonia 12、Avalonia.Controls.WebView、DI 组合、本地资源服务和 RPC bridge。第一条可执行垂直切片为 `bridge.hello -> settings/account bootstrap -> run.start -> deterministic empty mailbox -> run.completed -> report.export -> report.open`。

**技术栈：** .NET SDK 10.0.100、`net10.0-windows`、`win-x64`、Avalonia 12.1.0、Avalonia.Controls.WebView 12.1.0、ZeroPipeline Core/Recipe 1.2.0、EF Core SQLite 10.0.12、MailKit 4.18.0、PDFiumCore 155.0.8057、PdfPig `0.1.17-alpha-202609192350-df33d`、SkiaSharp `4.154.0-preview.1.26454.9`、Sdcb.SimdPaddleOCR 1.4.2、Microsoft.Extensions.AI.OpenAI 10.10.0、Microsoft.Playwright 1.62.0、ClosedXML 0.105.1、Serilog、WiX Toolset 5.0.2。

## 全局约束

- 只支持 Windows 11 x64，使用 `net10.0-windows`、RID `win-x64`、self-contained、unpackaged Avalonia 12 应用。
- 使用 SDK `10.0.100`、`rollForward=disable`、`allowPrerelease=false`，CI 使用 `dotnet restore --locked-mode`。
- Domain 不得引用 Avalonia.Controls.WebView、EF Core、HTTP、MailKit、PdfPig、PDFiumCore、SkiaSharp、ZeroPipeline 或供应商 SDK。
- provider secret 不得进入 DTO、日志、审计事件、Recipe、SQLite 结果 JSON、Avalonia WebView 事件或 fingerprint；使用 DPAPI-backed `ISecretStore` 和逻辑 secret reference。
- 使用 `invoiceflow.rpc.v1`、camelCase JSON、严格未知字段拒绝、稳定错误 envelope；新 dispatcher 不注册旧 Python 方法。
- 业务状态和审计写入使用显式 UoW 事务；不得向 Application 暴露 ambient EF transaction。
- 使用 typed ZeroPipeline port；业务 payload 不进入 `PipelineContext` dictionary；所有输入 port 使用 blocking backpressure。
- 保持确定性 sequence、幂等性、候选级失败隔离，以及终态优先级 `Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed`；使用 `docs/superpowers/fixtures/recipe/invoiceflow.default.v1.json`。
- 不生成或提交伪造的 native/model/browser hash；真实发布资产和 hash 只能来自 CI 输入。

---

### 任务 1：搭建 solution 和构建工具链

**文件：**
- 创建：`global.json`
- 创建：`Directory.Build.props`
- 创建：`Directory.Build.targets`
- 创建：`Directory.Packages.props`
- 创建：`NuGet.Config`
- 创建：`InvoiceFlowAI.sln`
- 创建：`src/InvoiceFlowAI.Domain/InvoiceFlowAI.Domain.csproj`
- 创建：`src/InvoiceFlowAI.Contracts/InvoiceFlowAI.Contracts.csproj`
- 创建：`src/InvoiceFlowAI.Application/InvoiceFlowAI.Application.csproj`
- 创建：`src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.Infrastructure.csproj`
- 创建：`src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`
- 创建：`src/InvoiceFlowAI.Installer/InvoiceFlowAI.Installer.wixproj`
- 测试：`tests/*/*.csproj`

**接口边界：**
- 产出规格要求的项目引用图和确定性的 Release 构建目标。
- `Domain <- Contracts <- Application <- Infrastructure`；`App` 引用 Application/Infrastructure/Avalonia.Controls.WebView；Installer 只引用 publish 输出。

- [ ] **步骤 1：先写失败的工具链检查**

创建 `build/verify-toolchain.ps1`。当 `.NET SDK 10.0.100` 或 WiX `5.0.2` 缺失时，脚本必须返回非零，并指出第一个缺失或版本不匹配的工具。Windows SDK 和 MSVC 不做独立精确版本门禁；桌面 TFM 指定最低 Windows 平台版本。

- [ ] **步骤 2：运行检查并确认 scaffold 前失败**

运行：`pwsh -File build/verify-toolchain.ps1`

预期：在工具未安装或版本不匹配时返回非零结果，并显示具体原因。

- [ ] **步骤 3：创建 solution 和项目**

按上面的项目引用图创建工程。启用 nullable、implicit usings、deterministic build、warnings-as-errors 和 lock files；只有需要 Windows API 的项目使用 `net10.0-windows`。

- [ ] **步骤 4：还原并编译空 solution**

运行：`dotnet restore --locked-mode`

预期：使用已提交的 lock 文件成功还原。

运行：`dotnet build -c Release --no-restore`

预期：所有项目零警告编译成功。

- [ ] **步骤 5：提交**

```powershell
git add global.json Directory.Build.props Directory.Build.targets Directory.Packages.props NuGet.Config InvoiceFlowAI.sln src tests build/verify-toolchain.ps1
git commit -m "build: scaffold dotnet solution and toolchain"
```

### 任务 2：迁移 Contracts、Domain 模型和 golden fixture 加载

**文件：**
- 创建：`src/InvoiceFlowAI.Domain/Invoices/*.cs`
- 创建：`src/InvoiceFlowAI.Domain/Candidates/*.cs`
- 创建：`src/InvoiceFlowAI.Domain/Rules/*.cs`
- 创建：`src/InvoiceFlowAI.Domain/Runs/*.cs`
- 创建：`src/InvoiceFlowAI.Contracts/Rpc/*.cs`
- 创建：`src/InvoiceFlowAI.Contracts/Serialization/InvoiceJsonContext.cs`
- 创建：`tests/InvoiceFlowAI.Domain.Tests/FixtureLoader.cs`
- 创建：`tests/InvoiceFlowAI.Contracts.Tests/ContractRoundTripTests.cs`
- 读取：`docs/superpowers/fixtures/**/*.json`

**接口边界：**
- 产出 `DocumentIdentity`、`DocumentCandidate`、`InvoiceDocument`、`CandidateProcessResult`、`RunInput`、`RunSummary`、RPC envelope、`RpcError`、账户/规则集/报表 DTO，以及严格 JSON serializer options。
- 所有已提交 fixture 必须支持 round-trip；使用 camelCase、enum 字符串、`DateOnly` 的 `yyyy-MM-dd` 和 invariant decimal 格式。

- [ ] **步骤 1：增加失败的 fixture 测试**

加载 Recipe、RuleSet、provider/parser registry、account RPC、progress event、provider conflict、parser conflict、account test、URL errors、release manifest 和 email-body receipt fixture，断言 schema/version 与关键数量。

- [ ] **步骤 2：运行测试并确认缺少类型时失败**

运行：`dotnet test tests/InvoiceFlowAI.Contracts.Tests -c Release`

预期：由于 DTO/serializer 尚未存在而编译失败。

- [ ] **步骤 3：实现不可变 Domain 和 Contracts record**

使用设计规格中的确切类型名和字段。用 `JsonUnmappedMemberHandling.Disallow` 拒绝未知 JSON 字段；在 Application 接收前校验 ID、Decimal 有限性、confidence 范围、identity 保持和终态不变量。

- [ ] **步骤 4：运行 round-trip 测试**

运行：`dotnet test tests/InvoiceFlowAI.Contracts.Tests -c Release`

预期：所有 fixture round-trip 通过，非 secret DTO 序列化结果不包含 secret-like 字段。

- [ ] **步骤 5：提交**

```powershell
git add src/InvoiceFlowAI.Domain src/InvoiceFlowAI.Contracts tests/InvoiceFlowAI.Contracts.Tests tests/InvoiceFlowAI.Domain.Tests
git commit -m "feat: add domain and rpc contracts"
```

### 任务 3：实现 canonical JSON、Recipe registry 和 RuleSet bootstrap

**文件：**
- 创建：`src/InvoiceFlowAI.Application/Configuration/ICanonicalJsonSerializer.cs`
- 创建：`src/InvoiceFlowAI.Application/Configuration/RecipeRegistry.cs`
- 创建：`src/InvoiceFlowAI.Application/Configuration/RuleSetBootstrapper.cs`
- 创建：`src/InvoiceFlowAI.Application/Rules/RuleSetValidator.cs`
- 创建：`src/InvoiceFlowAI.Application/Rules/ProviderRuleRegistry.cs`
- 创建：`src/InvoiceFlowAI.Application/Rules/SpecialParserRegistry.cs`
- 测试：`tests/InvoiceFlowAI.Application.Tests/Configuration/*`

**接口边界：**
- `RecipeRegistry.LoadDefaultAsync()` 返回已校验的 `PipelineRecipe`。
- `RuleSetBootstrapper.EnsureDefaultAsync(IUnitOfWork, CancellationToken)` 幂等插入 `default` version `1`，存在时校验 fingerprint，并初始化 `UserSettings.RuleSetVersion=1`。
- `ConfigurationFingerprintService.ComputeAsync(...)` 返回小写 SHA-256 fingerprint。

- [ ] **步骤 1：编写失败测试**

覆盖未知 Recipe port、修正后的 typed graph、默认 RuleSet 重复 bootstrap、fingerprint 不匹配、provider/parser registry 排序，以及 JSON 属性重排后的 canonical JSON 稳定性。

- [ ] **步骤 2：运行聚焦测试**

运行：`dotnet test tests/InvoiceFlowAI.Application.Tests --filter FullyQualifiedName~Configuration -c Release`

预期：registry/bootstrap 服务不存在而失败。

- [ ] **步骤 3：实现 registry 和 bootstrap**

加载 `invoiceflow.default.v1.json`，逐个校验 port；加载 `default.v1.json`，migration 后事务插入且禁止覆盖现有 version。使用固定连接：`recover.Candidates -> extract.Candidates -> pair.Results -> archive.Pairs -> report.Archived`。

- [ ] **步骤 4：重新运行测试**

预期：schema、graph、bootstrap 幂等、provider/parser 排序和 fingerprint 测试通过。

- [ ] **步骤 5：提交**

```powershell
git add src/InvoiceFlowAI.Application tests/InvoiceFlowAI.Application.Tests
git commit -m "feat: add recipe ruleset and fingerprint registries"
```

### 任务 4：实现 EF Core schema、仓储、legacy importer 和 DPAPI

**文件：**
- 创建：`src/InvoiceFlowAI.Infrastructure/Persistence/InvoiceFlowDbContext.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Persistence/Entities/*.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/20260923_InitialSchema.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Persistence/Migrations/InvoiceFlowDbContextModelSnapshot.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Persistence/Repositories/*.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Security/DpapiSecretStore.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Security/LegacySettingsImporter.cs`
- 创建：`tests/InvoiceFlowAI.Infrastructure.Tests/Persistence/*`
- 创建：`tests/InvoiceFlowAI.Infrastructure.Tests/Security/*`

**接口边界：**
- 实现 `IRunRepository`、`IDocumentRepository`、`IInvoiceRepository`、`IManualReviewRepository`、`IArchiveRepository`、`IRuleSetStore`、`IMailboxAccountStore`、`IRunStateStore`、`IAuditStore`、`IEventReplayStore` 和 `ISecretStore`。
- `LegacySettingsImporter` 只迁移非秘密字段，记录 `LegacyImportState`，要求 secret re-entry，并在成功后原子删除旧来源。

- [ ] **步骤 1：编写失败的 SQLite 测试**

测试空库 migration、`UserSettings` 到 `RuleSets` 的外键、`MailboxAccounts` revision 冲突、RuleSet bootstrap、append-only audit、RunEvents sequence 唯一性，以及不解密旧 secret 的 legacy secret re-entry。

- [ ] **步骤 2：实现前运行测试**

运行：`dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Persistence|FullyQualifiedName~Security -c Release`

预期：缺少 DbContext/migration/服务而失败。

- [ ] **步骤 3：实现 migration 和 entities**

创建包含全部表、外键、partial index、`UserSettings` singleton、`RuleSets`、`MailboxAccounts`、RunEvents 和 audit 约束的 `20260923_InitialSchema`。migration 只能由启动恢复阶段执行。

- [ ] **步骤 4：实现 DPAPI 和 legacy importer**

使用 `CurrentUser` 和应用派生 entropy。只检测旧 protected 字段而不解密，写入 `LEGACY_SECRET_REQUIRES_REENTRY`，映射非秘密字段，并在清理失败后阻止 fallback 读取。

- [ ] **步骤 5：运行测试**

运行：`dotnet test tests/InvoiceFlowAI.Infrastructure.Tests -c Release`

预期：migration、事务、并发、DPAPI、importer 和 append-only 测试通过。

- [ ] **步骤 6：提交**

```powershell
git add src/InvoiceFlowAI.Infrastructure tests/InvoiceFlowAI.Infrastructure.Tests
git commit -m "feat: add sqlite persistence dpapi and legacy importer"
```

### 任务 5：实现运行终态、候选隔离、checkpoint 和事件重放

**文件：**
- 创建：`src/InvoiceFlowAI.Application/Runs/RunCoordinator.cs`
- 创建：`src/InvoiceFlowAI.Application/Runs/TerminalDecisionService.cs`
- 创建：`src/InvoiceFlowAI.Application/Runs/CheckpointService.cs`
- 创建：`src/InvoiceFlowAI.Application/Runs/EventReplayService.cs`
- 创建：`tests/InvoiceFlowAI.Application.Tests/Runs/*`

**接口边界：**
- 产出优先级为 `Failed > Cancelled > NeedsManualReview > PartialSuccess > Completed` 的 `RunTerminalDecision`。
- 候选失败保持为 `CandidateProcessResult` 值；只有持久化/审计/报表/图/认证失败成为 `RunFailure`。
- 每个已提交 packet 在一个 UoW 中写入业务状态、audit、checkpoint、RunEvent 和 `Runs.LastEventSequence`。

- [ ] **步骤 1：编写失败测试**

覆盖五类终态、候选 `Unresolved/Timeout/QuotaExhausted`、取消与失败竞争、非关键 finalizer 失败、事件缺口、snapshot 刷新、重复 packet 重放，以及事务提交前/后的崩溃点。

- [ ] **步骤 2：运行聚焦测试**

运行：`dotnet test tests/InvoiceFlowAI.Application.Tests --filter FullyQualifiedName~Runs -c Release`

预期：终态/重放服务尚不存在而失败。

- [ ] **步骤 3：实现服务**

不得通过“没有抛异常”推断成功。只在最终屏障后计算终态，只提交一次终态决策，重复完成请求返回已存在状态。

- [ ] **步骤 4：运行测试并提交**

```powershell
dotnet test tests/InvoiceFlowAI.Application.Tests --filter FullyQualifiedName~Runs -c Release
git add src/InvoiceFlowAI.Application/Runs tests/InvoiceFlowAI.Application.Tests/Runs
git commit -m "feat: add terminal state checkpoint and event replay"
```

### 任务 6：实现归档两阶段提交和恢复

**文件：**
- 创建：`src/InvoiceFlowAI.Infrastructure/Archive/ArchiveCommitCoordinator.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Archive/ArchiveRecoveryService.cs`
- 创建：`tests/InvoiceFlowAI.Infrastructure.Tests/Archive/*`

**接口边界：**
- 使用 `RunId + DocumentId + ProcessingRevision + Role + ContentHash` 作为 `IArchiveCommitCoordinator` 幂等键。
- 使用 `Absent -> Prepared -> Committed`，只有证据不明确时进入 `RecoveryRequired`。

- [ ] **步骤 1：编写失败的恢复测试**

覆盖 DB Prepared + temp only、Prepared + final same hash、Prepared + final different hash、两者都缺失、无 DB 记录的孤立 final、重复 prepare/move/commit，以及每个阶段的进程终止。

- [ ] **步骤 2：实现前运行测试**

运行：`dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Archive -c Release`

预期：coordinator/recovery 服务不存在而失败。

- [ ] **步骤 3：实现事务协调器**

准备临时文件并计算 hash；事务 A 提交 Prepared 和 audit；原子移动并 fsync；校验最终 hash；事务 B 提交 Committed 和 audit。恢复过程不得静默删除证据不明确的文件。

- [ ] **步骤 4：运行测试并提交**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Archive -c Release
git add src/InvoiceFlowAI.Infrastructure/Archive tests/InvoiceFlowAI.Infrastructure.Tests/Archive
git commit -m "feat: add durable archive commit recovery"
```

### 任务 7：实现 report export/open 并替换路径 API

**文件：**
- 创建：`src/InvoiceFlowAI.Application/Reports/ReportApplicationService.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Reports/ClosedXmlReportExporter.cs`
- 创建：`src/InvoiceFlowAI.App/Rpc/ReportRpcHandlers.cs`
- 创建：`tests/InvoiceFlowAI.Infrastructure.Tests/Reports/*`
- 创建：`tests/InvoiceFlowAI.App.Tests/Rpc/ReportRpcTests.cs`

**接口边界：**
- `report.export` 接收 `ReportExportRequest`，返回 `ReportExportRpcResult`。
- `report.open` 返回绑定 run、相对路径、过期时间和 content hash 的一次性 `ReportOpenToken`。
- 不注册旧的绝对路径 API。

- [ ] **步骤 1：编写失败测试**

测试固定三 sheet schema、失败候选也必须输出、数值/日期/null 格式、重复导出的 content hash、token 过期、token 重放、hash mismatch 和绝对路径拒绝。

- [ ] **步骤 2：实现 exporter 和 handlers**

使用 ClosedXML 0.105.1，持久化相对报告路径/hash，强制执行 token 生命周期，并将失败映射为 `REPORT_EXPORT_FAILED` field details。

- [ ] **步骤 3：运行测试并提交**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Reports -c Release
dotnet test tests/InvoiceFlowAI.App.Tests --filter FullyQualifiedName~ReportRpc -c Release
git add src/InvoiceFlowAI.Application/Reports src/InvoiceFlowAI.Infrastructure/Reports src/InvoiceFlowAI.App/Rpc/ReportRpcHandlers.cs tests
git commit -m "feat: add report export and secure open tokens"
```

### 任务 8：实现 DeepSeek/OCR 和邮件正文 receipt pipeline

**文件：**

**接口边界：**

- [ ] **步骤 1：编写失败测试**

加载四类 parser 成功 fixture、缺字段 fixture、冲突 fixture、fake `IChatClient` 文本/视觉请求、401/429/timeout/image-size 场景，并断言 `InputKind=EmailBodyReceipt` trace。
```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests --filter FullyQualifiedName~Ai|FullyQualifiedName~EmailBody -c Release
```
### 任务 9：实现 Avalonia.Controls.WebView host、JSON/RPC client 并迁移现有前端

**文件：**
- 创建：`src/InvoiceFlowAI.App/WebView/AvaloniaWebViewHost.cs`
- 创建：`src/InvoiceFlowAI.App/Rpc/WebViewRpcBridge.cs`
- 创建：`src/InvoiceFlowAI.App/AppHost.cs`
- 修改：`templates/index_app.js`
- 创建：`tests/InvoiceFlowAI.App.Tests/WebView/*`
- 创建：`tests/InvoiceFlowAI.App.Tests/Rpc/*`

- Avalonia.Controls.WebView host 使用本地 `Web/index.html`、`IWebViewAssetVerifier`、`IWebViewNavigationPolicy` 和 `IRpcDispatcher`；启动时验证 Windows backend、版本和资源 manifest。
- 前端使用 `RpcClient`、单一 `AppStore` reducer、`bridge.hello`、event sequence 校验、`run.get` 重放、`account.*`、`ruleset.*`、`report.export/open`，不再使用 `window.pywebview.api`。

- [ ] **步骤 1：编写失败的 bridge/frontend 测试**

测试握手、未知旧方法拒绝、请求 timeout/cancel、事件缺口重放、控件重建、页面重载、旧 page 消息拒绝、backend 缺失、账户设置、报表导出/打开和 sessionStorage 不含 secret。

- [ ] **步骤 2：实现 host 和 bridge**

按固定初始化顺序实现 Avalonia WebView 控件、backend 验证、资源加载和消息桥接；RPC handler 在 UI 线程之外执行，响应和事件回到 Avalonia UI 线程。

- [ ] **步骤 3：迁移三个现有页面**

用 reducer action 和 selector 替换旧轮询与直接路径 API。保留现有工作流和可见文案，但将 GLM 控件替换为 DeepSeek/secret 状态。

- [ ] **步骤 4：运行测试并提交**

```powershell
dotnet test tests/InvoiceFlowAI.App.Tests -c Release
node --check templates/index_app.js
git add src/InvoiceFlowAI.App templates tests/InvoiceFlowAI.App.Tests
git commit -m "feat: migrate frontend to webview rpc"
```

### 任务 10：实现确定性 mailbox、parser、pairing 和归档集成

**文件：**
- 创建：`src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxScanner.cs`
- 创建：`src/InvoiceFlowAI.Infrastructure/Documents/*Parser.cs`
- 创建：`src/InvoiceFlowAI.Application/Pipeline/Nodes/*.cs`
- 创建：`tests/InvoiceFlowAI.Infrastructure.Tests/Mail/*`
- 创建：`tests/InvoiceFlowAI.Application.Tests/Pipeline/*`

**接口边界：**
- 实现 MailKit 扫描、UID cursor、ZIP 限制、provider registry、parser registry、URL recovery、extraction、pairing、archive 和 report nodes，并使用 typed Recipe graph。
- 每个 candidate 必须产生且只产生一个终态结果；所有 node failure 遵循 run/candidate 边界。

- [ ] **步骤 1：编写失败的行为测试**

迁移现有 Python 样本和测试，覆盖日期边界、UIDVALIDITY、provider/direct URL family、OFD/XML/PDF 优先级、特殊 parser 冲突、配对、查重、归档命名和候选失败隔离。

- [ ] **步骤 2：实现 nodes 和 adapter**

节点 port 只使用 Application DTO；基础设施通过 interface 接入；保持 sequence/reorder/retry 行为。

- [ ] **步骤 3：运行测试并提交**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests tests/InvoiceFlowAI.Application.Tests -c Release
git add src tests
git commit -m "feat: implement invoice processing pipeline"
```

### 任务 11：构建 manifest、固定资产、WiX installer 和 CI

**文件：**
- 创建：`build/model-manifest.ps1`
- 创建：`build/release-manifest.ps1`
- 创建：`.github/workflows/windows.yml`
- 创建：`src/InvoiceFlowAI.Installer/Product.wxs`
- 创建：`licenses/THIRD-PARTY-NOTICES.txt`
- 修改：`Directory.Build.targets`
- 测试：`tests/InvoiceFlowAI.App.Tests/Release/*`

**接口边界：**
- 产出 publish 目录、model/browser/release manifest、签名 x64 MSI、许可证清单和资产校验结果。

- [ ] **步骤 1：编写失败的 manifest 测试**

测试资产缺失、长度/hash 错误、PE 架构错误、Avalonia WebView backend 缺失、Chromium revision 不匹配、model manifest 不匹配、生产资产未签名和路径穿越。

- [ ] **步骤 2：实现脚本和 WiX 打包**

WiX 构建期间不得下载资产，只消费已锁定且有 hash 的 publish 输入；不得把真实生成 hash 写入源码 fixture。

- [ ] **步骤 3：运行 publish 和 installer 验证**

```powershell
pwsh -File build/verify-toolchain.ps1
dotnet restore --locked-mode
dotnet test -c Release
dotnet publish src/InvoiceFlowAI.App -c Release -r win-x64 --self-contained true
pwsh -File build/model-manifest.ps1
pwsh -File build/release-manifest.ps1
wix build src/InvoiceFlowAI.Installer/Product.wxs -arch x64
```

- [ ] **步骤 4：提交**

```powershell
git add build .github src/InvoiceFlowAI.Installer licenses Directory.Build.targets tests
git commit -m "build: add release manifests installer and ci"
```

### 任务 12：Windows 端到端验收和迁移审计

**文件：**
- 创建：`tests/InvoiceFlowAI.App.Tests/EndToEnd/WindowsReleaseTests.cs`
- 创建：`tests/InvoiceFlowAI.Infrastructure.Tests/Fixtures/*`
- 创建：`docs/superpowers/fixtures/migration/*`
- 修改：`docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md`，仅在验证出偏差时修改

**接口边界：**
- 产出干净 Windows 11 x64 启动、Avalonia.Controls.WebView 本地加载、DPAPI、账户 revision、空邮箱、完整运行、取消、失败隔离、报表导出/打开、归档恢复、OCR 资产加载、DeepSeek fake/兼容性行为和卸载/升级保留数据的证据。

- [ ] **步骤 1：建立完整 fixture 矩阵**

包含带 secret 字段的旧设置、旧五类终态、报表导出/打开重放、归档崩溃点、四类 email-body provider、四类特殊 parser 和旧绝对路径拒绝。

- [ ] **步骤 2：运行确定性集成套件**

运行：`dotnet test -c Release --collect:"XPlat Code Coverage"`

预期：contracts、migration、persistence、pipeline、RPC、Avalonia WebView fake-host、report、archive 和 parser 测试全部通过。

- [ ] **步骤 3：运行干净 Windows 验收**

在干净 Windows 11 x64 机器安装签名 MSI，执行启动/资产校验，运行空邮箱任务，并验证不需要 Python 进程或网络下载模型。

- [ ] **步骤 4：只记录剩余外部验证**

将 DeepSeek live endpoint、OCR 质量、真实 provider 浏览器流程和最终发布资产 hash 保留为外部发布检查，不从 fake test 推断已通过。

- [ ] **步骤 5：提交**

```powershell
git add tests docs/superpowers/fixtures docs/superpowers/specs/2026-09-23-dotnet10-migration-design.md
git commit -m "test: verify migration behavior and release acceptance"
```

## 执行顺序

任务 1-3 建立 solution 和契约基础；任务 4-6 建立持久化状态、运行终态和归档恢复；任务 7 提供第一个用户可见的报表路径；任务 8 提供提取和邮件正文行为；任务 9 接入实际前端；任务 10 完整业务 pipeline；任务 11-12 完成发布和验收。

第一个有价值的垂直切片是任务 1-7，使用 fake empty mailbox 和 fake DeepSeek client。不要等待真实 OCR model、Playwright asset 或 live DeepSeek 访问，先验证 RPC、SQLite、报表、终态和恢复行为。

## 计划自审

- **规格覆盖：** solution scaffold、typed Recipe DAG、设置/账户/secret、RuleSets、SQLite/UoW/audit/events/checkpoints、五类终态、候选隔离、MailKit、URL provider、parser、OCR、DeepSeek、report export/open、Avalonia.Controls.WebView、前端迁移、manifest、WiX、CI 和 Windows 验收均有对应任务。
- **类型一致性：** `RecoverUrls` 使用 `Candidates<PipelineItem<CandidateBatch>>`；`ExtractDocuments` 消费 Candidates 并产生 `Results<PipelineItem<ExtractionBatch>>`；pairing 产生 Pairs；archive 产生 Archived；report 消费 Archived。这与已修正的 Recipe fixture 一致。
- **不伪造资产：** model、browser、native library 和 release hash 仍由 CI 生成，计划不伪造这些值。
- **已知外部检查：** live DeepSeek 兼容性、OCR 准确率、真实 provider 浏览器流程、代码签名和最终资产 hash 都明确作为发布检查，不从本地 fake test 声称已通过。
