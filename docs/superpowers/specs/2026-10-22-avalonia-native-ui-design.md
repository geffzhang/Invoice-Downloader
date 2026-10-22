# 设计:用 Avalonia 原生 UI(AtomUI)替换 WebView

- 日期:2026-10-22
- 分支:`avalonia-native`
- 状态:设计已确认(§1–§5 逐节通过),待用户复核本文档后进入实现计划

## 1. 背景与动机

当前架构:Avalonia 窗口壳,整个窗口只有一个 `NativeWebView`(WebView2),全部 UI 在
`templates/` 的 HTML/JS 中(约 5,500 行:index.html 1311 行、index_app.js 1614 行、
编译产物 2058 行,静态资源 6.9MB)。C# 侧通过 `src/InvoiceFlowAI.App/Rpc/`(22 个文件)
暴露 JSON-RPC handler 供前端调用。仅支持 Windows(`net10.0-windows10.0.19041.0`)。

替换动机(全部确认):

1. **原生体验与设计升级** — 采用 AtomUI(Ant Design 6 组件体系 for Avalonia,v6.2.8)
2. **跨平台铺路** — WebView2 仅 Windows;原生 UI 为将来 macOS/Linux 版铺路
3. **维护性:单一技术栈** — 消灭 JS + C# 双语言栈
4. **启动性能与体积** — 摆脱 WebView2 运行时依赖与首屏加载

现状利好:Application 层已有成体系的接口(`IAccountApplicationService`、
`IDesktopRunService`、`IReportApplicationService` 等),现有 RPC handler 只是薄适配层;
事件流已有 `IRunEventPublisher` 接缝,管线不关心消费者是谁。

## 2. 已确认决策

| 编号 | 决策 | 内容 |
|---|---|---|
| D1 | 技术路线 | 方案 A:原生优先壳 + 直接 DI + 逐页替换,RPC 层随 WebView 逐步"饿死" |
| D2 | 迁移路径 | 渐进式,每个阶段可编译可发布(里程碑建议:1b、2、3 各发一次) |
| D3 | 跨平台节奏 | 铺路不落地:UI 全部用跨平台控件与 API,发布目标暂留 Windows;TFM 切换与 macOS/Linux 流水线留到将来 |
| D4 | 许可证 | 接受 AtomUI LGPL-3.0:独立 DLL 随 MSI 分发(现有 WiX 打包天然合规),补 THIRD-PARTY-NOTICES,不启用单文件发布/裁剪 |
| D5 | 分支 | 实现放在 `avalonia-native`(main 已合并入内) |
| D6 | 测试栈 | xUnit v3 4.0.2 + Microsoft.Testing.Platform(global.json `test.runner`) |

## 3. 总体结构与分层

### 3.1 窗口结构(阶段 1a 的终点形态)

- MainWindow 保持**无边框 + 自绘窗口按钮**(最小化/最大化/关闭用 AtomUI 按钮实现,延续现有设计语言)
- 内容区 Grid:
  - 左侧:AtomUI 侧边栏,3 个导航项(启动配置 / 处理中心 / 结果分析)+ 底部按钮区
  - 右侧:内容区 = 原生页面(UserControl)+ 一个 WebView 承载面板(未迁移页面用,过渡期存在)
- 页面切换 = 侧边栏选中 → 内容区切换控件,不再走 URL 路由

### 3.2 App 项目内部分层

```
src/InvoiceFlowAI.App/
  MainWindow.axaml(.cs)          # 壳:窗口 chrome + 侧边栏 + 内容区
  Views/                         # SettingsPageView / ProcessingPageView / AnalysisPageView / Dialogs/
  ViewModels/                    # 对应 VM + 对话框 VM
  Services/                      # 平台能力封装:窗口控制、StorageProvider 目录选择、文件打开
  Rpc/                           # 过渡期保留,随 WebView 页面迁移逐步删除
  WebView/                       # 嵌入模式桥(临时)
```

### 3.3 关键选型

| 决策点 | 选择 | 理由 |
|---|---|---|
| MVVM 框架 | CommunityToolkit.Mvvm | 源生成器 `[ObservableProperty]`/`[RelayCommand]`,Avalonia 主流组合,样板最少 |
| 依赖注入 | 沿用 `AppServiceProviderFactory`(MS.Extensions.Hosting) | VM/View/Service 全注册进 DI,组合根不变 |
| AtomUI 接入 | App.axaml 合并 AtomUI 主题资源,默认**暗色主题**(对齐现状),token 可微调 | 设计升级动机,Ant Design 6 体系整体采用 |
| 图标 | AtomUI.Icons.AntDesign 替换 Material Symbols | 设计体系切换的一部分,风格统一 |
| 窗口 | 无边框 + AtomUI 自绘按钮 | 延续现有视觉,避免回到系统标题栏的突兀感 |

## 4. 数据流

**核心原则:UI 层只依赖 Application 层接口;`IRunEventPublisher` 是事件流的唯一入口,原生侧加一个实现即可。**

### 4.1 各页面接线(直接 DI,替换 RPC)

| 原生页面 | 注入的服务 | 对应现 RPC |
|---|---|---|
| SettingsViewModel | `IAccountApplicationService`、配置服务、`IProviderConnectionTester` | account.list/save/test、provider.test、settings |
| ProcessingViewModel | `IDesktopRunService`(启动/停止/安全停止) | run.start、run.stop |
| AnalysisViewModel | `IReportApplicationService`(汇总/复核/导出) | report.* |

启动时先经 `IDesktopRunService` 取**当前运行上下文快照**(现 `run.context.get` 等价物,
支撑重启后恢复进度),再订阅增量事件。

### 4.2 事件流(处理中心的进度/日志)

- App 层新增 `IAppEventBus`(`IObservable<RunEvent>` 风格,内部用 Rx Subject 或 Channel)
- 新增 `NativeRunEventPublisher : IRunEventPublisher`,把事件灌进 `IAppEventBus`
- **过渡期**:fan-out 组合发布器同时喂 WebView 桥与 `IAppEventBus`;WebView 删除后 fan-out 随之删除
- VM 订阅后经 `Dispatcher.UIThread` 封送到 UI 线程;日志列表用 Avalonia 虚拟化控件承载

### 4.3 平台能力抽象(`Services/` 文件夹,一对一替换 RPC handler)

| 新接口 | 职责 | 替代的 RPC |
|---|---|---|
| `IWindowActionService` | 最小化/最大化/关闭 | window.minimize/maximize/close |
| `IFolderPickerService` | StorageProvider 目录选择 | directory.choose |
| `IFileOpenService` | 在资源管理器中打开文件/文件夹 | run.file.open、manual.review.open |

Windows 实现用 Avalonia StorageProvider + `Process.Start("explorer")`;接口平台无关,
将来 macOS/Linux 各加实现即可。

### 4.4 过渡期桥(未迁移页面)

- WebView 面板以"嵌入模式"加载 SPA:URL 带参数隐藏其自带侧边栏与窗口按钮(一次性 JS 改动,约几十行)
- 原生侧边栏导航 = 命令 WebView 跳转到对应 path;该页 RPC 照常工作
- **饥饿式退役**:每迁移一个页面,其专属 RPC handler 在同一 PR 中删除,不留孤儿

### 4.5 线程与生命周期

- 后端管线在后台线程跑,事件到达后统一 `Dispatcher.UIThread.Post` 封送
- 运行中关窗:保留现状语义(确认对话框 + 安全停止),在 `MainWindow.Closing` 里处理

## 5. 迁移阶段划分

每个阶段结束都保持可编译、可发布。

| 阶段 | 内容 | 出口条件 |
|---|---|---|
| **0 · 基础** | 引入 AtomUI.Desktop.Controls / Icons.AntDesign / CommunityToolkit.Mvvm;App.axaml 合并暗色主题;补 THIRD-PARTY-NOTICES | 编译通过,WebView 整壳照常运行(无可见变化) |
| **1a · 原生壳** | MainWindow 换为原生结构:无边框窗口 + AtomUI 自绘按钮 + AtomUI 侧边栏 + 内容区;三页全部仍走 WebView 嵌入模式(SPA 隐藏自带侧边栏/按钮);`IWindowActionService`、`IFolderPickerService`、`IFileOpenService`、`IAppEventBus` + fan-out 发布器就位 | 可见变化:新窗口 chrome + 新侧边栏 + Ant Design 暗色主题;三页功能不变 |
| **1b · 设置页原生** | SettingsPageView/VM 原生化:账号 CRUD/测试、供应商测试、邮箱/API/日期、初始化状态、受控复跑锁定提示;目录选择走 StorageProvider | 删除 account.*、provider.*、directory.choose、window.* RPC handler;设置页完全原生 |
| **2 · 处理中心原生** | ProcessingPageView/VM:进度条、统计(邮件/票据/错误)、虚拟化日志列表、停止/安全停止、配额与邮箱拉取失败提示;启动时上下文快照重放 | 删除 run.* RPC;WebView 只剩分析页 |
| **3 · 分析页原生** | AnalysisPageView/VM:结果汇总、待复核清单 + 打开文件夹、导出 | 删除 report.* RPC;**移除 WebView 控件、WebView/、Rpc/、templates/、bridge JS、`Avalonia.Controls.WebView` 依赖** |
| **4 · 合规收尾** | CI 确认无单文件发布;THIRD-PARTY-NOTICES 最终化;README 架构图与说明按现状事实更新(遵守 README 保守改动规矩) | 发布前 checklist 全绿 |

关键设计点:

- 阶段 1a 是最大的"基建步",拆出它让风险与可见收益分离:壳先上线,页面逐个换
- 每页迁移 = UI + 接线 + 删除对应 RPC 在同一 PR 完成
- 阶段 4 的 README 更新只动架构事实部分,不做风格重写(按 AGENTS.md 的 README 规矩)
- 工作量估计:原生侧合计约 3–5k 行 XAML + C#

## 6. 错误处理

**分层原则:阻断型错误占满页面、操作型错误用对话框/通知、流式错误用横幅,且所有用户可见文案走产品语言(AGENTS.md 防泄漏规矩)。**

### 6.1 三层呈现

| 层级 | 场景 | 呈现 |
|---|---|---|
| 页面级阻断 | 初始化/引导失败(现 bootstrap error 状态) | 全页错误状态,AtomUI Result/Alert + 重启建议 |
| 操作级失败 | 账号保存/测试失败、导出失败、打开文件夹失败 | AtomUI 消息条/对话框;取消(目录选择)不算错误 |
| 流式错误 | 运行中配额耗尽、邮箱拉取失败、`last_error` | 处理中心顶部 Alert 横幅 + 日志条目,不打断任务流 |

### 6.2 错误映射

VM 层统一捕获服务异常 → 映射为 `UserFacingMessage`(中文产品文案):已知业务错误(配额、
认证失败、目录不可写、受控复跑锁定)有专门文案;未知异常给通用文案,细节只进 Serilog
日志,堆栈与实现细节**永不渲染到 UI**。

### 6.3 取消与安全停止

- 停止按钮:幂等 `StopAsync`,`stopPending` 状态反映在按钮禁用与状态文案(沿用现"已收到安全停止指令"语义)
- 运行中关窗:`MainWindow.Closing` 拦截 → 确认对话框(沿用"关闭程序会直接结束当前窗口与任务进程"语义)→ 确认后发起安全停止再退出

### 6.4 异步纪律

VM 命令全部走 `AsyncRelayCommand`;异常在命令包装层统一兜底,杜绝未处理后台异常打到 UI。

## 7. 测试策略

测试栈:xUnit v3 4.0.2,`dotnet test` 走 Microsoft.Testing.Platform 新体验
(global.json `"test": { "runner": "Microsoft.Testing.Platform" }`)。策略以"VM 单测为主、
UI 冒烟为辅、人工清单兜底"为性价比平衡点。

1. **ViewModel 单元测试(xUnit v3,新增于 App.Tests)**
   - 全部 VM 依赖接口注入,用假服务驱动:
     - 设置页:账号加载/保存/测试流程、受控复跑锁定态、初始化失败 → 页面级错误态
     - 处理中心:喂入假事件序列(`IAppEventBus` 直接投递),断言进度/统计/配额横幅/停止按钮状态转换
     - 分析页:汇总、复核清单、导出成功/失败
   - **Dispatcher 封送抽象为 `IUiDispatcher` 接口**(生产实现走 Avalonia 调度器,测试注入同步实现),并入阶段 1a 的基础设施
2. **Headless UI 冒烟(Avalonia.Headless)**
   - 壳装配冒烟:MainWindow 能创建、侧边栏选中切换、三页控件实例化不崩
   - **阶段 0 就验证 AtomUI 在 headless 下的兼容性**;若有问题,缩小为最小冒烟范围(只测壳结构)
3. **RPC 饥饿式退役的静态护栏(过渡期专用)**
   - 每次迁移 PR:被删 handler 的符号无引用由编译保证;另加静态检查脚本,断言「剩余 RPC handler 集合 == WebView 剩余页面实际调用的方法集合」,防漏删/错删
4. **功能等价人工清单**
   - 每个里程碑发布前按页核对:设置保存/测试、进度实时性、导出产物 — 沿用现有发布流程,写成 checklist 文档
5. **LGPL 合规 CI(阶段 4 落地)**
   - CI 断言发布产物:AtomUI DLL 独立存在、无单文件合并、THIRD-PARTY-NOTICES 存在
6. **回归基线**
   - 后端不动,现有 Domain/Application/Infrastructure 测试全程保持绿;每个阶段全量跑

## 8. LGPL-3.0 合规要求

- 现有 WiX 打包为松散 DLL,AtomUI 以独立 DLL 分发即合规(已核实 CI 未开单文件发布)
- 补齐 THIRD-PARTY-NOTICES 声明(AtomUI LGPL-3.0 + 许可文本)
- 禁止:PublishSingleFile、裁剪/合并成单文件、静态链接式合并
- 阶段 4 在 CI 中断言上述约束持续成立

## 9. 风险与缓解

| 风险 | 缓解 |
|---|---|
| AtomUI 在 Avalonia.Headless 下不兼容,冒烟测试写不了 | 阶段 0 提前验证;降级为只测壳结构的最小冒烟范围 |
| 嵌入模式改造 SPA 影响现有 WebView 版行为 | 一次性、几十行 JS;加参数隔离,默认不带参数时行为不变 |
| 过渡期双 UI 并存增加维护面 | 双 UI 并存期尽量短;饥饿式退役保证 RPC 无孤儿 |
| 大阶段 1a 一次改动过大 | 壳与页面拆成 1a/1b,分别可发布 |
