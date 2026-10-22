# Python 运行时退役实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 以 Avalonia/.NET 作为唯一桌面实现，移除仓库内全部 Python 源文件、测试及其专属依赖清单，同时维持 Windows 桌面发布包不含 Python runtime。

**Architecture:** 以 `desktop-parity-matrix.md` 为行为验收账本，先补齐 IMAP、URL/provider、编排和归档等 .NET 行为证据，再移除全部 Python 源文件和测试。Avalonia 是唯一桌面入口；用户于 2026-09-26 将范围从旧桌面 runtime 扩大到独立真值/审计工具。Windows CI 仅运行 .NET 构建、测试和发布审计。

**Tech Stack:** .NET 10、C#、xUnit、FluentAssertions、PowerShell、GitHub Actions。

## 全局约束

- 保留用户可观察到的正确性、安全性、失败处理、顺序和归档行为，不要求照搬 Python 内部实现。
- URL 恢复保留显式代理支持及公网目标校验；最多 10 个独立组并发，其中强 provider 组最多 4 个，同组候选串行。
- worker job 清理失败时 URL 恢复必须安全失败；诊断只包含脱敏原因和不透明 job 标识，且不得声称临时数据已删除。
- 用户于 2026-09-26 批准删除全部 Python 源文件、测试和 Python 专属 `requirements.txt`；不删除 C# parity 实现、测试、构建或发布资产。
- parity 状态只使用 `verified`、`approved retired`、`out of desktop scope` 作为最终状态；退役前不得有 `partial`、`gap` 或 `decision needed`。
- Windows CI 只运行 .NET job；仓库不保留 Python runtime、工具源码、pytest 测试或 requirements 清单。
- 不把真值数据、邮箱内容、凭据或签名 URL 写入共享 fixture。
- 不创建 Git commit；保留用户已有改动。

---

## 文件与职责

- `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`：每项可观察行为的证据索引和最终状态。
- `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/Fixtures/`：仅保存合成数据，供 C# parity 测试读取。
- `tests/InvoiceFlowAI.Infrastructure.Tests/Mail/MailKitMailboxScannerTests.cs`：MailKit 日期、UID 选择、逐 UID FETCH 和失败语义。
- `tests/InvoiceFlowAI.Infrastructure.Tests/Url/`：公网 URL 安全、代理、worker job 隐私及 provider 恢复合同。
- `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/DocumentExtractionStageTests.cs`：本地优先、取消、顺序和恢复并发合同。
- `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/`：拒绝归档、提交失败哈希不变量、恢复和 CWT 跨 run 行为。
- `tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/WorkerHostTests.cs`：worker 临时 job 生命周期和响应内容合同。
- Task 1 曾将 `build_truth_dataset.py`、`strict_truth_audit.py`、`batch_validation.py`、`artifact_verifier.py`、`model_calibration.py` 及其依赖列为独立工具；用户后续授权已将这些工具源码一并删除。
- `audit_email_truth.py` 曾保留 CLI 和 `collect_truth_table` 工具 API；该文件已按扩大的退役范围删除。
- `.github/workflows/windows.yml`：仅保留 .NET 构建、测试、发布包审计和打包发布 job；仓库已不包含 Python 测试。

## Task 1: 建立依赖与删除边界

**Files:**

- Inspect: 根目录全部 `*.py`、`tests/**/*.py`、`requirements.txt`、`.github/workflows/windows.yml`
- Modify: `docs/superpowers/plans/2026-09-25-python-runtime-retirement.md`（实施时在本任务记录经验证的清单）

- [x] **Step 1: 运行当前基线**

```powershell
Remove-Item Env:INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST -ErrorAction SilentlyContinue
dotnet test InvoiceFlowAI.sln -c Release --nologo
pytest -q
```

- .NET：775 passed，2 个 opt-in NativeWebView skipped。初次裸跑时，当前 shell 的 `INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST=1` 启用了原生 UI 测试；清除变量后标准全套通过。
- Python：767 passed，109 subtests passed。修正了 `test_batch_validation.py` 将固定性能基线与当前主机指纹错误要求相等的断言，以及 `test_refactor_contracts.py` 过期的窗口 API 断言。

- [x] **Step 2: 建立静态导入、动态导入和测试消费者清单**

```powershell
$pythonFiles = Get-ChildItem -Path . -Recurse -File -Filter *.py | Where-Object { $_.FullName -notmatch '\\.venv\\' }
$pythonFiles | Select-String -Pattern '^\s*(from\s+[A-Za-z_][\w.]*\s+import|import\s+[A-Za-z_])|import_module\(|runpy\.run_path|subprocess\.(run|Popen)|sys\.executable'
$pythonFiles | Select-String -Pattern 'app_api|audit_email_truth|build_truth_dataset|strict_truth_audit|batch_validation|artifact_verifier|model_calibration|email_fetcher|mailbox_scanner|invoice_extractor|pdf_converter|provider_|url_recovery|pairing_engine'
```

清单逐项标记为桌面 runtime、保留工具、共享依赖或测试专用。历史清单曾记录 `app_api.py` 对审计工具的动态导入；Task 6 已解除该耦合，Task 7 已删除旧 runtime 和独立工具闭包。`build_truth_dataset.py` 曾延迟导入其他工具模块；相关文件已按用户决定删除。

AST 导入图盘点到 43 个根模块。最初识别的 19 个独立工具闭包为：`artifact_verifier`、`audit_email_truth`、`batch_validation`、`build_truth_dataset`、`email_body_receipts`、`email_channel`、`email_fetcher`、`mailbox_scanner`、`model_calibration`、`pdf_converter`、`pinned_http`、`provider_baiwang`、`provider_direct_invoice`、`run_evidence`、`strict_truth_audit`、`truth_contracts`、`url_security`、`url_trace_sanitizer`、`user_settings`。该分类只表示它们彼此构成工具闭包；用户扩大退役范围后均已删除。`diagnostics_trace.py` 不存在。

- [x] **Step 3: 固定保留工具本地测试集合**

原保留工具本地测试白名单为 `tests/test_audit_email_truth.py`、`tests/test_build_truth_dataset_imap.py`、`tests/test_strict_truth_audit.py`、`tests/test_batch_validation.py`、`tests/test_artifact_verifier.py`、`tests/test_model_calibration.py`、`tests/test_xml_parity.py`、`tests/test_pinned_http.py` 和 `tests/test_url_security.py`。用户于 2026-09-26 明确批准删除全部 Python 测试及工具源码；上述白名单、其他 `tests/*.py`、根级 Python 工具和 `requirements.txt` 均已退役。

**验收：** 清单中每个根目录 Python 模块及测试文件均有分类；保留工具的传递导入闭包无未分类边；未解决的边阻止删除任务。

## Task 2: 补齐 IMAP 可观察合同

**Files:**

- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Mail/MailKitMailboxScannerTests.cs`
- Modify: `src/InvoiceFlowAI.Application/Mail/MailboxScanner.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/Mail/IMailboxSession.cs`、`src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxSession.cs`、`src/InvoiceFlowAI.Infrastructure/Mail/MailKitMailboxScanner.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`

- [x] **Step 1: 为 SEARCH 和 FETCH 失败语义添加失败测试**

在现有 MailKit scanner 测试中覆盖：SEARCH 协议失败不得返回“空邮箱”成功；选中的 UID 集合与逐 UID FETCH 集合相同；某个 UID FETCH 失败时保留其他成功邮件、保留 UID 统计并返回部分失败；单 UID 失败有界终止，不得无限重试。用现有 fake MailKit session/transport 模式，不加入网络邮箱或真实邮件内容。

- [x] **Step 2: 运行定向测试并确认新断言失败**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~MailKitMailboxScannerTests --nologo
```

新测试必须因缺失合同失败；现有日期边界、上海时区、`INTERNALDATE` 优先级、未知日期保留和选取/FETCH UID 集合测试保持通过。

新增单 UID FETCH 测试最初因结果类型缺少逐 UID failure 合同而编译失败；实现后 session 与 scanner 两个窄回归均通过。

- [x] **Step 3: 按最小行为修复并复跑**

只在失败测试指向 `MailKitMailboxSession.SearchAsync` 或 `MailKitMailboxScanner` 的行为缺口时修改对应实现。MailKit 逐 UID 访问已替代 Python 递归分批机制时，不重建递归算法；以逐 UID 部分结果、错误分类和有界失败作为行为合同。

结果合同为每个 UID 最多尝试两次；普通 FETCH 异常在两次均失败后映射为 `IMAP_MESSAGE_FETCH_FAILED`，取消仍向上传播，失败原文不进入 failure record。`MailboxScanResult.HighestUid` 包含成功和失败 UID。新测试先确认单次尝试实现失败，再加入一次有界 retry；session pure suite 40/40、scanner suite 29/29 通过。

- [x] **Step 4: 更新 IMAP 矩阵行**

将日期/`INTERNALDATE`、逐 UID 部分失败/retry、recursive split、SEARCH 解析与 25 UID 上限分别记录。25 UID 上限由每次单 UID fetch 满足并标为 `verified`；recursive batch split 和无法注入的 malformed SEARCH wire 解析仍单列未决，不作未获批准的退役决定。

当前证据：单 UID transient retry、二次失败上限、部分成功保留及取消传播已验证；C# scanner suite 29 项、纯 session suite 40 项、Python 参考 `tests/test_mailbox_scanner.py` 19 项通过。`MailKitMalformedSearchProtocolTests` 的两个本地脚本 IMAP 用例分别验证 malformed SEARCH wire item 被拒绝，以及实际 INTERNALDATE summary 被用于 date-window filtering；scanner 安全协议失败映射另有覆盖。仍未关闭的是最终报告/terminal 对 `FetchFailures` 的呈现及 recursive batch-splitting 算法是否要求保留。每次单 UID fetch 满足 25 UID buffer 上限；不再单独要求复现 Python batching 算法。

**验收命令：** `dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~MailKitMailboxScannerTests --nologo`。

## Task 3: 补齐 URL 安全、显式代理和 worker 清理合同

**Files:**

- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlPolicyTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PinnedHttpUrlRecoveryTransportTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/PublicUrlRecoveryClientTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryWorkerClientTests.cs`
- Modify: `tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/WorkerHostTests.cs`
- Modify: 对应 `src/InvoiceFlowAI.Infrastructure/Url/`、`src/InvoiceFlowAI.UrlRecovery.Worker/` 的拥有者实现，仅在测试证明行为缺失时
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`

- [x] **Step 1: 为代理请求添加合成公网证明测试**

验证显式配置的代理请求保留目标 URL 的公网 DNS/peer 证明；代理不能绕过 `PublicUrlPolicy`、peer 校验、每跳重定向复验或响应大小上限。覆盖直连与代理两种 transport 路径，以及私网、环回、DNS peer 不匹配和重定向到非公网目标的拒绝结果。

- [x] **Step 2: 为 worker job 清理失败添加隐私测试**

模拟 job 目录删除失败，断言恢复结果为失败；诊断只包含不透明 job 标识和脱敏原因，不包含源 URL、query、请求头或 Cookie；错误明确表达本地清理未完成。成功清理路径仍断言 job 目录已删除。测试只使用 `.invalid`/合成地址与假文件系统。

- [x] **Step 3: 运行 URL 与 worker 定向测试**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~PublicUrlPolicyTests|FullyQualifiedName~PinnedHttpUrlRecoveryTransportTests|FullyQualifiedName~PublicUrlRecoveryClientTests|FullyQualifiedName~UrlRecoveryWorkerClientTests" --nologo
dotnet test tests/InvoiceFlowAI.UrlRecovery.Worker.Tests/InvoiceFlowAI.UrlRecovery.Worker.Tests.csproj -c Release --filter FullyQualifiedName~WorkerHostTests --nologo
```

当前证据：Infrastructure URL/client 定向测试 46 项通过，WorkerHost 定向测试 1 项通过。生产 DI 使用 `HttpClient.DefaultProxy`；测试覆盖目标公网 attestation、bypass/direct、代理 endpoint peer、HTTP Host 保留、HTTPS CONNECT 到 attested IP、HTTPS proxy TLS SNI 使用配置的 proxy hostname、逐跳重验和响应大小上限。worker cleanup failure 测试断言 fail closed、32 位 opaque job ID、清理未完成信息，且不泄露源 URL/query。

旧 Python 代理合同不转发代理凭据：`ProxyEndpoint` 只保留 scheme/host/port/address，`PinnedHttpTransport.build_plan` 构造不含 userinfo 的 proxy URL。

- [x] **Step 4: 补齐代理 TLS 身份与系统 bypass 兼容性证据**

使用本地合成 HTTPS proxy 断言 TLS SNI/目标身份使用配置的 proxy hostname；使用确定性 `IWebProxy` fake 断言 bypass 目标应用直连 DNS/peer 规则且 transport 不启用 proxy。复跑 Task 3 Step 3 的两条命令；两个定向测试集通过后将 URL 矩阵从 `partial` 改为 `verified`。

**验收：** 两条定向测试命令全部通过，且测试输出/持久化事件中无合成秘密值。

## Task 4: 补齐 provider 恢复和候选身份 parity

**Files:**

- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/Fixtures/provider-recovery.json`
- Create: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/Fixtures/candidate-identity.json`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/DesktopParityFixtureTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/NuonuoScanRecoveryStrategyTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/BaiwangArtifactSelectorTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/DirectInvoiceArtifactSelectorTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Url/UrlRecoveryPipelineIntegrationTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`

- [x] **Step 1: 扩展 provider 合成矩阵**

已有 fixture 覆盖 Baiwang wrapper/same-source 选择、Nuonuo transient detail retry 加 XML/PDF capture，以及六个 direct family 的 transient retry/PDF selection。新增 Nuonuo 双有效 PDF 歧义和 XML identity mismatch fail-closed fixture；Baiwang 匹配 OFD 关联同源 PDF、排除异实体 PDF及全候选 mismatch terminal fixture；六个 direct family 各自的 ZIP XML/PDF/OFD 配对、XML identity mismatch 和 unsupported-body 终态 fixture。ChinaTax、BWJF、PDD、JD、KPBYD 通过标准 302 后取回归档，Fpyun 保持专用 legacy handoff 合同。所有 case 使用固定合成字节和虚构 host，不访问 provider 网络。

- [x] **Step 2: 增加候选身份 fixture 和 C# 读取断言**

`candidate-identity.json` 每个 case 只含虚构 account/mailbox/UID、候选输入顺序、预期不透明 identity、是否保留为人工复核。测试分别断言输入顺序变化不改变 identity、URL 原文及 query 不出现在持久化 identity 中、异常候选仍进入人工复核而非丢弃。

已新增 fixture 和 `Candidate_identity_golden_fixtures_are_opaque_stable_and_retain_malformed_urls`；候选身份/收集定向测试 12 项、parity/provider 组合测试 30 项通过。身份不透明及人工复核有合成 fixture；source writer 不落盘 URL、report projection 不输出 URL/query 的回归在 Task 5 完成。

- [x] **Step 3: 运行 provider 与 parity 定向测试**

```powershell
```

当前执行结果：provider/parity 定向测试 34 项通过；兼容 pytest 8.4.2 环境的 `tests/test_provider_url_recovery.py` 为 49 passed。Nuonuo XML identity mismatch 暴露并修复了 C# 丢弃 XML 字段、忽略预期发票号的问题；Nuonuo 现在复用 direct XML 字段解析和选择器。Python 与 C# 现共同消费 Baiwang XML/OFD 同源选择和实体 mismatch fixture；provider 旧测试的 pytest 版本门槛已解除。候选 identity 持久化/report 隐私另由 Task 5 synthetic persistence/report 测试覆盖。

- [x] **Step 4: 按测试结果补齐策略并更新矩阵**

不改变 provider 已有接受条件来迎合 fixture；fixture 的预期身份字段和 artifact kind 必须与独立测试合同一致。provider 分类行继续保持 `verified`，各恢复/选择行只有完整矩阵通过后才能标记 `verified`。

**验收：** 定向测试通过；fixture 不包含真实域名、邮箱、签名 URL 或凭据。

## Task 5: 补齐编排、终态和归档/CWT 合同

**Files:**

- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Pipeline/DocumentExtractionStageTests.cs`
- Modify: `tests/InvoiceFlowAI.Application.Tests/Runs/RunExecutionServiceTests.cs`
- Modify: `tests/InvoiceFlowAI.Application.Tests/Runs/DesktopRunServiceTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/DocumentArchivingStageIntegrationTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/ArchiveRecoveryServiceTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/CwtCancellationFinalizerTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/Archive/CwtArchiveInventoryTests.cs`
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`

- [x] **Step 1: 覆盖提取和 URL 恢复调度**

用可控 barrier 和计数 transport 断言：本地 parser 命中时不调用远端；取消会终止等待并保留稳定结果顺序；同一 provider 组内候选串行；不同组总并发不超过 10，强 provider 组并发不超过 4；provider/browser 串行区不会重叠。

执行结果：`UrlRecoveryStageTests` 12/12 通过，包含 10 个 generic 独立组上限、4 个 provider 组上限、按逆序完成仍按输入顺序输出、同组候选串行和阻塞请求取消；`DocumentExtractionStageTests` 13/13 通过，覆盖整批本地 preflight、普通安全远端 max-2 overlap、逆序完成稳定输出、auth breaker、并发取消收尾及 provider/browser fallback 不重叠。恢复请求并发执行，artifact identity 与 source-store 写入按输入顺序串行。C# 当前没有 browser fallback 执行路径，该 barrier 锁定的是提取 stage 的 provider/browser fallback 串行边界。

- [x] **Step 2: 覆盖终态之后没有事件**

在 `RunExecutionServiceTests` 用可阻塞 sink：释放终态返回后再释放延迟的 trace/progress producer，断言 sink 没收到终态之后的新事件；进度值单调递增且总数稳定。

执行结果：C# 没有计划所述的后台 trace/progress producer；`IRunEventPublisher.Publish`/`WebViewRunEventPublisher.Publish` 是同步 `void`，所有进度和候选事件均经 awaited commit/publish，terminal 是 `ExecuteAsync` 的最后一次同步发布。`Run_publishes_monotonic_progress_with_scanned_email_count` 与 `Terminal_publish_waits_for_in_flight_progress_commit` 覆盖可观察合同；Python 专属延迟 producer 已在矩阵单列为 `out of desktop scope`，没有伪称覆盖不存在的 C# producer。

- [x] **Step 3: 覆盖接受拒绝、提交失败与 CWT 跨 run**

合成 URL-recovery-to-archive 链路断言身份不匹配的 provider 产物被 fail-closed 判定并路由人工复核，不进入 archive commit。注入 archive commit 失败，断言 temp/final 内容 SHA-256、artifact 状态和恢复记录符合回滚合同。CWT fixture 覆盖匹配、`其他` 类别、冲突和回滚；未登记文件不得生成 document/artifact 记录。旧 `.antigravity_history.json` history key 兼容已获用户批准退役，不要求迁移恢复。

执行结果：`Direct_provider_identity_mismatch_routes_to_review_without_archive_commit` 通过；direct selector mismatch 经过真实 `UrlRecoveryStage` 和 `DocumentArchivingStage` 后保留 review reason，archive coordinator 未调用且无 artifact。可归档来源时的 manual-review role 由 `Provider_identity_mismatch_is_routed_to_review_not_regular_archive` 覆盖；move interruption 的 SHA-256/Prepared recovery entry 与 CWT 冲突/回滚由 Step 4 archive suite 覆盖。

- [x] **Step 4: 分别运行定向测试**

```powershell
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentExtractionStageTests|FullyQualifiedName~RunExecutionServiceTests" --nologo
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --filter FullyQualifiedName~DocumentExtractionStageTests --nologo
dotnet test tests/InvoiceFlowAI.Application.Tests/InvoiceFlowAI.Application.Tests.csproj -c Release --filter "FullyQualifiedName~RunExecutionServiceTests|FullyQualifiedName~DesktopRunServiceTests" --nologo
dotnet test tests/InvoiceFlowAI.Infrastructure.Tests/InvoiceFlowAI.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentArchivingStageTests|FullyQualifiedName~DocumentArchivingStageIntegrationTests|FullyQualifiedName~ArchiveRecoveryServiceTests|FullyQualifiedName~CwtCancellationFinalizerTests|FullyQualifiedName~CwtArchiveInventoryTests" --nologo
```

执行结果：四条命令依次通过，分别为 9/9、9/9、15/15 和 32/32。新增 barrier 测试验证 URL recovery 等待中的取消传播，以及 RunExecutionService 等待在途 progress commit 后才发布终态；archive move interruption 测试验证 source/temp SHA-256、final 缺失及 Prepared recovery entry。随后 URL recovery 调度 suite 12/12、DocumentExtractionStage suite 13/13 及 recovery-to-archive mismatch integration 1/1 通过。用户已明确批准退役旧 `.antigravity_history.json` history key 兼容，矩阵单列为 `approved retired`。本轮又完成 per-UID FETCH failure 的非终态进度诊断、持久化快照回放与 Processing Center 映射；完整矩阵不再含 `partial`、`gap` 或 `decision needed`，用户也明确批准递归 batch-splitting 退役。

最终执行 `Remove-Item Env:INVOICEFLOW_RUN_NATIVE_WEBVIEW_TEST -ErrorAction SilentlyContinue; dotnet test InvoiceFlowAI.sln -c Release --nologo`：806 passed、2 个 opt-in NativeWebView skipped、0 failed（808 total）。MSBuild 报告 solution 中 `InvoiceFlowAI.Installer` 没有 `Release|Any CPU` 项目配置映射；该 warning 与本任务行为无关，未修改 solution 配置。此结果包含 safe extraction scheduler、bounded IMAP retry 和 URL 隐私回归。

- [x] **Step 5: 更新矩阵并确认每个剩余项的状态**

将已由 .NET 行为取代且获批准退役的内部 Python 算法标为 `approved retired`；真值数据 XML 解析工具标为 `out of desktop scope`；其余合同必须是 `verified`。矩阵中不得留下 `partial`、`gap` 或 `decision needed`。

**验收：** 上述四条定向测试命令全部通过；5 个原已 verified 的领域仍保持测试覆盖。

执行结果：矩阵全部未退役合同均为 `verified`，两项用户批准退役的旧算法标记为 `approved retired`，XML 真值工具保持 `out of desktop scope`；无 `partial`、`gap` 或 `decision needed`。验证包含 Task 5 四条定向测试记录、完整 .NET solution 812 total（810 passed、2 opt-in NativeWebView skipped、0 failed）、run progress RPC Node suite 10/10、保留 Python 白名单 222 passed 和 provider/mailbox 参考 suite 68 passed。WebView bundle `--check` 通过。MSBuild 仍报告 Installer 缺少 `Release|Any CPU` 映射警告。

## Task 6: 将保留工具与旧桌面 runtime 解耦，CI 保持 .NET-only

**Files:**

- Modify: `tests/test_audit_email_truth.py`、`tests/test_run_lifecycle.py`（只调整旧 runtime 专属调用测试）
- Modify: `tests/InvoiceFlowAI.Application.Tests/Runs/RunExecutionServiceTests.cs`、`tests/InvoiceFlowAI.Application.Tests/Runs/DesktopRunServiceTests.cs`
- Historical retain list at Task 6: `audit_email_truth.py`、`build_truth_dataset.py`、`strict_truth_audit.py`、`batch_validation.py`、`artifact_verifier.py`、`model_calibration.py`、`run_evidence.py`、`truth_contracts.py` 及当时确认的依赖闭包；Task 7 后续按用户决定全部删除

- [x] **Step 1: 在兼容版本环境验证保留工具白名单**

历史命令（相关测试文件现已删除，仅作执行记录）：

```powershell
python -m pytest -q tests/test_audit_email_truth.py tests/test_build_truth_dataset_imap.py tests/test_strict_truth_audit.py tests/test_batch_validation.py tests/test_artifact_verifier.py tests/test_model_calibration.py tests/test_xml_parity.py tests/test_pinned_http.py tests/test_url_security.py
```

历史记录：该命令曾在 workspace `.venv` 的 pytest 8.4.2 下以 222 passed 通过；Python 测试文件现已全部删除，此结果仅供追溯，不再作为当前门禁。

- [x] **Step 2: 确认 Windows CI 仅运行 .NET job**

用户决定 Windows CI 只保留 .NET job。workflow 不安装 Python、不运行 pytest；用户随后明确批准删除全部 Python 源码、测试和依赖清单。

- [x] **Step 3: 分离旧运行时的审计 hook 耦合**

历史执行阶段曾用 Python 测试验证 `audit_email_truth.collect_truth_table` 的脱敏输出合同和 CLI manifest 校验，并解除旧 runtime 对该 hook 的调用。对应工具和测试现已按用户后续决定删除。

执行结果：`InvoiceAppAPI._runtime_truth_audit_contract` 在 runtime 内产生同字段的脱敏启动摘要；`_start_truth_audit_async` 不再动态加载 `audit_email_truth`。生命周期测试改为注入 runtime 私有 seam，并新增断言验证没有工具导入、报告字段兼容及邮箱/认证码未泄露。`tests/test_run_lifecycle.py` 与 `tests/test_audit_email_truth.py` 合并定向运行 35 passed；保留工具的 `collect_truth_table` 未修改，CLI 增加了有效/无效 manifest 测试。

- [x] **Step 4: 验证本地 Python 白名单与 .NET CI 集合**

```powershell
python -m pytest -q tests/test_audit_email_truth.py tests/test_build_truth_dataset_imap.py tests/test_strict_truth_audit.py tests/test_batch_validation.py tests/test_artifact_verifier.py tests/test_model_calibration.py tests/test_xml_parity.py tests/test_pinned_http.py tests/test_url_security.py
dotnet test InvoiceFlowAI.sln -c Release --nologo
```

删除前执行结果：配置的 Python 3.14 `.venv` 使用 pytest 8.4.2，保留工具白名单 222 passed；关闭 opt-in NativeWebView 后完整 .NET solution 为 806 passed、2 skipped、0 failed（808 total）。Windows CI 集合已在 Step 2 确认为 .NET-only。

**历史验收：** 保留工具测试曾在不依赖桌面 runtime 的环境通过；当前验收以工具闭包静态引用检查和 .NET-only CI 为准。

## Task 7: 删除全部 Python runtime、工具源码与测试

**Files:**

- Delete: 根级 Python runtime 与工具模块、所有 `tests/*.py`、`scripts/__init__.py`、`requirements.txt`
- Verify: `.github/workflows/windows.yml`（仅有 .NET build/test、发布审计和打包发布 job）
- Modify: `tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md`
- Retain: Avalonia/.NET 应用、测试、构建和发布文件

- [x] **Step 1: 执行删除前引用门禁**

第一阶段删除前已逐项扫描静态/动态引用：19 个独立工具模块没有导入旧 runtime；`diagnostics_trace.py` 不存在；Avalonia 项目与 Windows workflow 没有 Python/app_api 启动引用。第二阶段扩大范围后，又扫描应用、构建和 CI 活动引用。

- [x] **Step 2: 删除经审计的 Python runtime 与工具闭包**

第一阶段删除 24 个旧 Python 桌面模块及 `tests/` 下全部 31 个 `test_*.py` 与 `conftest.py`；用户随后批准扩大范围，第二阶段删除余下 19 个工具模块、`scripts/__init__.py` 和 `requirements.txt`。最终共删除 44 个 Python 源文件及 32 个 Python 测试文件。第一阶段删除后 Avalonia App 测试 64 total、62 passed、2 opt-in skipped；最终完整 .NET solution 在 Task 8 通过。

- [x] **Step 3: 验证删除边界**

git grep -n -I -E 'app_api|app_archive_adapter|bounded_url_recovery|candidate_pipeline|company_rules|deferred_url_recovery|document_acceptance|document_types|extraction_pipeline|frontend_run_context|glm_runtime|invoice_domain|invoice_extractor|pairing_engine|report_service|run_coordinator|run_lifecycle|run_state_store|url_recovery_worker' -- '*.py' '*.yml' '*.yaml' '*.ps1' '*.toml' '*.txt' '*.csproj' '*.wxs'
最终删除后递归枚举（排除 `.venv` 与 `__pycache__`）没有 Python 源文件，`requirements.txt` 不存在；对 .NET 源码、WebView、构建与 CI 的 Python 模块/脚本引用扫描无结果。此前 `build_identity` 函数名与 C# 设置 RPC `user_settings` 曾产生词面误报，已用导入语法与脚本文件名重跑并排除。

**验收：** 无 Python 源文件、测试或依赖清单；Avalonia/.NET 桌面入口与工具链完整；矩阵没有未关闭状态。

## Task 8: 全量验证和发布包审计

**Files:**

- Verify: `InvoiceFlowAI.sln`、`.github/workflows/windows.yml`、`build/windows/publish_dotnet_app.ps1`
- Verify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`、`src/InvoiceFlowAI.Application/Release/NoPythonRuntimeReleaseAudit.cs`
- Verify: `publish/win-x64/` 与本地 Windows 安装包产物

- [x] **Step 1: 删除后运行完整 .NET 回归**

```powershell
dotnet test InvoiceFlowAI.sln -c Release --nologo
```

- 全部 44 个 Python 源文件、32 个 Python 测试文件及 `requirements.txt` 删除后，完整 .NET solution 812 total、810 passed、2 个 opt-in NativeWebView skipped、0 failed。MSBuild 仍报告 Installer 缺少 `Release|Any CPU` 配置映射警告。

- [x] **Step 2: 删除后重新发布并执行 Python-free 发布审计**

```powershell
$auditRoot = Join-Path $env:TEMP ("invoiceflow-python-retirement-" + [guid]::NewGuid().ToString("N"))
$publishRoot = Join-Path $auditRoot "publish"
pwsh -File build/windows/publish_dotnet_app.ps1 -Configuration Release -PublishRoot $publishRoot
$env:INVOICEFLOWAI_PUBLISH_ROOT = $publishRoot
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj -c Release --no-build --no-restore --nologo --filter FullyQualifiedName~Configured_publish_tree_passes_no_python_runtime_release_audit
```

随后检查发布树内无 Python 可执行文件、运行时 DLL、`site-packages`、旧 runtime 模块或仅服务于 Python 的 sidecar；保留已存在的 Python-free 审计测试作为 .NET 发布门禁。

- fresh self-contained publish 成功，release manifest 包含 642 个资产；`Configured_publish_tree_passes_no_python_runtime_release_audit` 1/1 通过，递归扫描未发现 Python 可执行文件、runtime DLL、`.py`/`.pyc`/`.pyd` 或 `site-packages`。临时发布目录已清理，未覆盖仓库现有发布树。

- [x] **Step 3: 构建 Windows MSI 并做全仓终检**

```powershell
$env:WIX_PATH = "$env:USERPROFILE\.dotnet\tools"
$installerPath = Join-Path $auditRoot "InvoiceFlowAI-setup.msi"
wix build src/InvoiceFlowAI.Installer/Product.wxs -arch x64 -out $installerPath
$openRows = Select-String -Path tests/InvoiceFlowAI.Infrastructure.Tests/DesktopParity/desktop-parity-matrix.md -Pattern '\|\s*(partial|gap|decision needed)\s*\|'
if ($openRows) { $openRows; throw "Parity matrix still contains open statuses." }
```

MSI 构建必须成功；矩阵状态扫描不得返回未关闭项；Windows CI 只包含 .NET 构建/测试、发布目录 Python-free 审计和打包/发布 job，不得添加 Python 安装或 pytest 步骤。

- 使用同一临时发布树构建 MSI 成功，大小 264,606,672 bytes；临时发布树与 MSI 已清理，没有覆盖 `publish/win-x64` 或 `dist/InvoiceFlowAI-setup.msi`。矩阵无未关闭状态；`node build/compile-webview-page.mjs --check` 与 `git diff --check` 通过。

Task 7 已完成：共删除 44 个 Python 源文件、32 个 Python 测试文件和 `requirements.txt`；仓库不存在 Python runtime 或独立工具源码。Task 8 的 .NET 回归、fresh publish audit、Python-free 产物扫描和临时 MSI 构建全部通过。

**最终验收：** 删除后 .NET 测试、Windows 发布包 Python-free 审计和 MSI 构建全部成功；全部 parity 行有测试证据或显式批准的退役/范围外状态；Windows CI 保持 .NET-only；无 Python 源文件、测试及依赖清单；没有 Git commit。

---

## 计划自检

- **设计覆盖：** Task 1 覆盖依赖边界；Task 2 覆盖 IMAP；Task 3 覆盖 URL SSRF、代理、重定向、大小上限、脱敏和清理；Task 4 覆盖 provider 与候选身份；Task 5 覆盖提取顺序/并发、事件终态、fail-closed、归档恢复和 CWT；Task 6 记录历史工具解耦与 CI 决定；Task 7 覆盖全部 Python 源码/测试删除；Task 8 覆盖最终 .NET 测试和发布门禁。
- **敏感数据：** 所有新 fixture 使用虚构标识和合成字节，不复制真值、邮箱内容、凭据或签名 URL。
- **删除安全：** 任何无法证明无消费者的模块都不删除；动态导入、测试消费者和 CI 消费者与静态导入同等对待。
- **状态一致性：** 退役前更新矩阵，不允许 `partial`、`gap`、`decision needed` 留存。
