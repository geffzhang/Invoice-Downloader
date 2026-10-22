# Avalonia 原生 UI(AtomUI)替换 WebView 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 Avalonia + AtomUI 6.2.8 原生页面替换 WebView2 承载的 SPA,窗口壳原生化,三页逐个迁移,RPC 层随页面迁移饥饿式删除,最终移除 WebView 依赖。

**Architecture:** 保留现有 Application/Infrastructure 分层与 DI 组合根(`AppServiceProviderFactory`)。壳层(MainWindow + AtomUI NavMenu)先落地,未迁移页面通过"嵌入模式"以 WebView 面板继续工作;每迁移一页,新增原生 ViewModel/View 并同步删除该页专属 RPC handler。事件流经 `IAppEventBus` 扇出,原生页面用轮询 + 事件触发刷新,不解析事件 payload。

**Tech Stack:** .NET SDK 10.0.401(`global.json` rollForward disable)、`net10.0-windows10.0.19041.0`、Avalonia 12.1.4、AtomUI.Desktop.Controls 6.2.8、AtomUI.Icons.AntDesign 6.2.8、CommunityToolkit.Mvvm 8.4.2、xUnit v3 4.0.2 + Microsoft.Testing.Platform、FluentAssertions 8.11.0、Node.js 内置测试运行器(过渡期)。

**Spec:** `docs/superpowers/specs/2026-10-22-avalonia-native-ui-design.md`

**参考资料:**

- 「一天时间将一个 web 组件库完全迁移成(Avalonia)原生组件库」(dotNET跨平台):TKCode.Arc(Arc Web 组件库的 Avalonia 移植,基于 AtomUI + Avalonia 12.1.4 精确锁定)的迁移经验,与本计划同技术栈,可对照其命名冲突与 headless 测试处理:<https://mp.weixin.qq.com/s/6ZyC0IIa5bVMebxSX5cixA>
- TKCode.Arc 仓库 <https://github.com/AtomUICommunity/TKCode.Arc>:1400+ 测试含无头 Avalonia 测试;其记录 Avalonia 12.1.4 的 `ITransition` 无法由外部实现(`TransitionBase.Apply` 为 internal)——本计划不使用自定义动效,沿用 AtomUI ControlTheme 默认过渡即可。

## Global Constraints

- 分支固定 `avalonia-native`;每个任务结束时代码必须可编译、测试全绿、可独立提交。
- 每个任务结束时 `dotnet build InvoiceFlowAI.sln -c Debug` 与 `dotnet test InvoiceFlowAI.sln --no-build` 必须通过。
- 目标框架保持 `net10.0-windows10.0.19041.0`,`TargetPlatformMinVersion` 保持 `10.0.17763.0`;本计划不切换 TFM、不新增非 Windows 发布目标(设计 §2 D3)。
- 包版本精确锁定,禁止浮动版本范围:Avalonia 家族 12.1.4、AtomUI 家族 6.2.8、CommunityToolkit.Mvvm 8.4.2。
- LGPL-3.0 合规:AtomUI 必须以独立 DLL 随 MSI 分发;禁止 `PublishSingleFile`、`PublishTrimmed`、程序集合并或静态链接(设计 §8)。
- 秘密约束:授权码与 API Key 不得进入任何 DTO、日志、事件、fingerprint;UI 只允许显示 `MaskedEmailAddress`、`CredentialConfigured`;秘密写入必须经 `ISecretRetentionStore`(`Persistent` / `Session`)。
- 用户可见文案只来自产品语言(AGENTS.md):禁止把 `StatusText`、`LastError`、`FailureCode`、`SafeMessage`、`ReasonCode`、`label`、`RunLogEntry.Message` 等后端原文/错误码渲染到界面;所有页面文案必须是固定中文产品文案或由快照数值拼装的中文。
- 饥饿式退役:删除某个 RPC handler 的任务必须同时删除其专用测试与 App 注册;不得留孤儿 handler。
- 测试栈为 xUnit v3 4.0.2 + Microsoft.Testing.Platform;禁止引入 `Avalonia.Headless.XUnit`(与 v3 4.0.2 不兼容,设计 §7.2);异步测试必须传 `TestContext.Current.CancellationToken`。
- 控件名冲突:AtomUI 与 Avalonia 存在同名类型(实测 `AtomUI.Desktop.Controls.Window` 与 `Avalonia.Controls.Window` 冲突报 CS0104;TKCode.Arc 文档另列 `Button`/`Calendar`/`DatePicker`/`Slider`/`SplitButton`/`TabItem`/`TimePicker`/`TreeView`/`ContextMenu`/`Carousel`)。XAML 一律用 `atom:` 前缀;代码中同时引用两个命名空间时,冲突类型必须用别名或全限定名(本计划 `MainWindow` 用全限定 `AtomUI.Desktop.Controls.Window`,新代码遵循同样规则)。
- 不迁移 SPA 的 QA 会话自动启动钩子(`autostart_enabled` / `autostart_token` / `isControlledAutostartConsumed`);受控复跑的**锁定**行为(输出目录、日期、邮箱)必须保留。
- 每个提交信息的最后一行固定为:`Co-Authored-By: Claude Code <noreply@anthropic.com>`。

## 文件地图

```
src/InvoiceFlowAI.App/
  Program.cs                        # AtomUI 引导 + Serilog 文件日志
  App.axaml(.cs)                    # 主题、启动 try/catch、从 DI 取 MainWindow
  Bootstrap/                        # AtomUiConfiguration / LogPaths / BootstrapErrorWindow
  MainWindow.axaml(.cs)             # 壳:atom:Window + NavMenu + 内容区 + 关闭确认
  Navigation/                       # IAppNavigator / AppNavigator / IPageViewModel / WebPageViewModel
  ViewModels/                       # ShellViewModel / 三个页面 VM / *Copy 固定中文文案
  Views/                            # 三个原生页面 + EmbedWebViewHost(任务 12 删除)+ DisclaimerWindow
  Services/                         # IScopedOperationRunner / IDialogService / INotificationService / IRunSession / IRunShutdownService
  Events/                           # IAppEventBus / AppEventBus / AppRunEvent / NativeRunEventPublisher / RunEventPublisherFanOut(任务 12 删除)
  Desktop/                          # 现有平台能力 + ScopedDesktopRunExecutorLeaseFactory(任务 12 移入)
  Rpc/                              # 过渡期保留,按任务逐页删除,任务 12 删除整个目录
  WebView/                          # 过渡期保留,任务 12 删除
templates/                          # 过渡期保留,任务 12 删除
tests/InvoiceFlowAI.App.Tests/
  Headless/                         # AtomUiHeadlessHost / AtomUiTestApplication / 冒烟测试
  ViewModels/                       # 三个页面 VM 与 ShellViewModel 测试
  TestSupport/                      # 测试替身(TestScopedOperationRunner / Fakes)
  Rpc/                              # 现有 RPC 与 JS 测试,按任务删除
```

## 任务依赖顺序

阶段 0:Task 1 → Task 2。阶段 1a:Task 3 → Task 4 → Task 5。阶段 1b:Task 6 → Task 7。阶段 2:Task 8 → Task 9。阶段 3:Task 10 → Task 11 → Task 12。阶段 4:Task 13。

---

### Task 1: 阶段 0 — AtomUI 引导、日志、LGPL 基线与 headless 冒烟

**Files:**
- Modify: `Directory.Packages.props:22-29`
- Modify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj:17-27`
- Modify: `src/InvoiceFlowAI.App/Program.cs`
- Modify: `src/InvoiceFlowAI.App/App.axaml`
- Modify: `src/InvoiceFlowAI.App/App.axaml.cs`
- Create: `src/InvoiceFlowAI.App/Bootstrap/AtomUiConfiguration.cs`
- Create: `src/InvoiceFlowAI.App/Bootstrap/LogPaths.cs`
- Create: `src/InvoiceFlowAI.App/Bootstrap/BootstrapErrorWindow.axaml`
- Create: `src/InvoiceFlowAI.App/Bootstrap/BootstrapErrorWindow.axaml.cs`
- Create: `licenses/LGPL-3.0.txt`
- Modify: `licenses/THIRD-PARTY-NOTICES.txt`
- Modify: `build/windows/publish_dotnet_app.ps1:58`
- Modify: `src/InvoiceFlowAI.Installer/Product.wxs:79-86`
- Modify: `tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj`
- Create: `tests/InvoiceFlowAI.App.Tests/Headless/AtomUiTestApplication.cs`
- Create: `tests/InvoiceFlowAI.App.Tests/Headless/AtomUiHeadlessHost.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Headless/AtomUiBootstrapSmokeTests.cs`

**Interfaces:**
- Produces: `InvoiceFlowAI.App.Bootstrap.AtomUiConfiguration.Configure(IAtomUIBuilder builder)` — 真实 App 与测试 App 共用的唯一 AtomUI 配置入口。
- Produces: `InvoiceFlowAI.App.Bootstrap.LogPaths.ApplicationLogFile`、`LogPaths.LogDirectory`、`LogPaths.EnsureLogDirectory()`。
- Produces: `InvoiceFlowAI.App.Tests.Headless.AtomUiHeadlessHost.Run<T>(Func<T>)` / `Run(Action)` — 在专用 STA 线程上执行 Avalonia 工作。
- Produces: AtomUI 桌面控件与图标包对 App 项目可见(后续所有任务依赖)。

- [ ] **Step 1: 先写失败的冒烟测试**

创建 `tests/InvoiceFlowAI.App.Tests/Headless/AtomUiBootstrapSmokeTests.cs`:

```csharp
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Styling;
using FluentAssertions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Headless;

public sealed class AtomUiBootstrapSmokeTests
{
    [Fact]
    public void AtomUI_bootstrap_provides_desktop_controls_and_dark_theme()
    {
        var snapshot = AtomUiHeadlessHost.Run(() =>
        {
            var application = Application.Current!;
            var navMenu = new NavMenu { Mode = NavMenuMode.Vertical };
            var lineEdit = new LineEdit { Watermark = "测试" };
            return new BootstrapSnapshot(
                application.Styles.Count,
                navMenu.Mode,
                lineEdit.Watermark,
                application.RequestedThemeVariant);
        });

        snapshot.StyleCount.Should().BeGreaterThan(0);
        snapshot.NavMenuMode.Should().Be(NavMenuMode.Vertical);
        snapshot.Watermark.Should().Be("测试");
        snapshot.ThemeVariant.Should().Be(ThemeVariant.Dark);
    }

    private sealed record BootstrapSnapshot(
        int StyleCount,
        NavMenuMode NavMenuMode,
        string? Watermark,
        ThemeVariant? ThemeVariant);
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter FullyQualifiedName~AtomUiBootstrapSmokeTests
```

Expected: 编译失败,`AtomUiHeadlessHost` 与 `AtomUI.Desktop.Controls` 类型不存在。

- [ ] **Step 3: 加包版本与引用**

`Directory.Packages.props` 在 Avalonia 区块后追加:

```xml
    <!-- AtomUI 6.2.8 (LGPL-3.0, shipped as separate DLLs) -->
    <PackageVersion Include="AtomUI.Desktop.Controls" Version="6.2.8" />
    <PackageVersion Include="AtomUI.Icons.AntDesign" Version="6.2.8" />
    <!-- MVVM -->
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.2" />
```

`src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj` 的 PackageReference 组内追加:

```xml
    <PackageReference Include="AtomUI.Desktop.Controls" />
    <PackageReference Include="AtomUI.Icons.AntDesign" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
```

`tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj` 的 PackageReference 组内追加(测试项目需要自己的分析器与图标类型):

```xml
    <PackageReference Include="AtomUI.Desktop.Controls" />
    <PackageReference Include="AtomUI.Icons.AntDesign" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
```

- [ ] **Step 4: 写共享 AtomUI 配置**

创建 `src/InvoiceFlowAI.App/Bootstrap/AtomUiConfiguration.cs`:

```csharp
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;

namespace InvoiceFlowAI.App.Bootstrap;

/// <summary>
/// 真实应用与 headless 测试共用的 AtomUI 启动配置。
/// 默认主题使用 DaybreakBlue + [Default, Dark] 算法,即暗色外观。
/// </summary>
public static class AtomUiConfiguration
{
    public static void Configure(IAtomUIBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseDesktopControls();
        builder.UseLanguages(LanguageTags.ZhCN, [LanguageTags.ZhCN, LanguageTags.EnUS]);
        builder.Theme.WithInitialTheme(
            IThemeManager.DEFAULT_THEME_ID,
            new ThemeConfigBuilder()
                .WithAlgorithms(ThemeAlgorithm.Default, ThemeAlgorithm.Dark)
                .Build());
    }
}
```

创建 `src/InvoiceFlowAI.App/Bootstrap/LogPaths.cs`:

```csharp
namespace InvoiceFlowAI.App.Bootstrap;

public static class LogPaths
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InvoiceFlowAI",
        "logs");

    public static string ApplicationLogFile => Path.Combine(LogDirectory, "invoiceflow-.log");

    public static void EnsureLogDirectory() => Directory.CreateDirectory(LogDirectory);
}
```

- [ ] **Step 5: 写 headless 宿主与测试 Application**

创建 `tests/InvoiceFlowAI.App.Tests/Headless/AtomUiTestApplication.cs`:

```csharp
using AtomUI;
using Avalonia;
using InvoiceFlowAI.App.Bootstrap;

namespace InvoiceFlowAI.App.Tests.Headless;

/// <summary>
/// Test-only Application. RegisterServices 必须调用 ApplicationStartup.Attach,
/// 否则 AtomUI 的 AfterApplicationSetup 校验会抛异常。
/// </summary>
public sealed class AtomUiTestApplication : Application
{
    public override void RegisterServices()
    {
        base.RegisterServices();
        ApplicationStartup.Attach(this);
    }
}
```

创建 `tests/InvoiceFlowAI.App.Tests/Headless/AtomUiHeadlessHost.cs`:

```csharp
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using AtomUI;
using InvoiceFlowAI.App.Bootstrap;

namespace InvoiceFlowAI.App.Tests.Headless;

/// <summary>
/// Avalonia.Headless.XUnit 12.1.4 与 xunit.v3 4.0.2 不兼容(设计 §7.2);另外
/// SetupWithoutStarting 会把 Avalonia dispatcher 绑定到调用线程,而 xunit v3 可能在
/// 不同线程上跑测试。这里用一条专用 STA 线程持有 headless 应用与调度器,所有 UI 工作
/// 通过队列在这条线程上执行。
/// </summary>
public static class AtomUiHeadlessHost
{
    private static readonly BlockingCollection<PendingWork> s_queue = new();
    private static readonly Lazy<Thread> s_thread = new(StartThread, LazyThreadSafetyMode.ExecutionAndPublication);

    public static T Run<T>(Func<T> work) => (T)RunCore(() => work())!;

    public static void Run(Action work) => RunCore(() =>
    {
        work();
        return null;
    });

    private static object? RunCore(Func<object?> work)
    {
        _ = s_thread.Value;
        var pending = new PendingWork(work);
        s_queue.Add(pending);
        return pending.Completion.Task.GetAwaiter().GetResult();
    }

    private static Thread StartThread()
    {
        var thread = new Thread(() =>
        {
            AppBuilder.Configure<AtomUiTestApplication>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .UseAtomUI(AtomUiConfiguration.Configure)
                .SetupWithoutStarting();

            foreach (var pending in s_queue.GetConsumingEnumerable())
            {
                try
                {
                    pending.Completion.SetResult(pending.Work());
                }
                catch (Exception exception)
                {
                    pending.Completion.SetException(exception);
                }
            }
        })
        {
            Name = "AtomUI headless host",
            IsBackground = true,
        };

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        return thread;
    }

    private sealed record PendingWork(Func<object?> Work)
    {
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
```

- [ ] **Step 6: 运行冒烟测试,确认通过**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter FullyQualifiedName~AtomUiBootstrapSmokeTests
```

Expected: PASS。若 `ThemeVariant` 断言失败,检查 `AtomUiConfiguration` 的 `WithAlgorithms(Default, Dark)` 顺序(后者生效)与 `UseAtomUI` 是否在 `UseHeadless` 之后调用。

- [ ] **Step 7: 改 Program.cs 为 AtomUI 引导 + Serilog 文件日志**

替换 `src/InvoiceFlowAI.App/Program.cs`:

```csharp
using Avalonia;
using InvoiceFlowAI.App.Bootstrap;
using Serilog;

namespace InvoiceFlowAI.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LogPaths.EnsureLogDirectory();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                LogPaths.ApplicationLogFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "应用启动失败。");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseAtomUIPlatformDetect()
            .WithInterFont()
            .WithAtomUIDefaultOptions()
            .UseAtomUI(AtomUiConfiguration.Configure)
            .LogToTrace();
}
```

顺序说明:`WithAtomUIDefaultOptions()` 会覆盖 `FontManagerOptions`,`WithInterFont()` 必须在它之前调用,最终保留 AtomUI 的 Microsoft YaHei 字体回退(中文界面必需)。

- [ ] **Step 8: 改 App.axaml 与 App.axaml.cs**

`src/InvoiceFlowAI.App/App.axaml` 删除 `RequestedThemeVariant="Light"`,保留 FluentTheme(AtomUI 启动时自动插入自己的样式与资源):

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="InvoiceFlowAI.App.App">
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

`src/InvoiceFlowAI.App/App.axaml.cs` 增加启动兜底(保留现有 WebView 窗口,Task 5 再替换):

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using InvoiceFlowAI.App.Bootstrap;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Settings;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace InvoiceFlowAI.App;

/// <summary>
/// Avalonia UI application root and desktop RPC composition point.
/// </summary>
public partial class App : Avalonia.Application
{
    private ServiceProvider? _serviceProvider;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _serviceProvider = AppServiceProviderFactory.Create();
                var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                var dispatcher = AppRpcComposition.CreateDispatcher(
                    _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    version);
                var windowAccessor = _serviceProvider.GetRequiredService<AvaloniaMainWindowAccessor>();
                var runEventPublisher = _serviceProvider.GetRequiredService<WebViewRunEventPublisher>();
                desktop.MainWindow = new MainWindow(dispatcher, windowAccessor, runEventPublisher);
                desktop.Exit += (_, _) =>
                {
                    _serviceProvider?.Dispose();
                    _serviceProvider = null;
                };
            }
            catch (Exception exception)
            {
                Log.Fatal(exception, "桌面服务初始化失败。");
                desktop.MainWindow = new BootstrapErrorWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

创建 `src/InvoiceFlowAI.App/Bootstrap/BootstrapErrorWindow.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="InvoiceFlowAI.App.Bootstrap.BootstrapErrorWindow"
        Title="InvoiceFlowAI"
        Width="560" Height="300"
        WindowStartupLocation="CenterScreen">
  <StackPanel Margin="32" Spacing="14">
    <TextBlock FontSize="22" FontWeight="SemiBold" Text="应用初始化失败" />
    <TextBlock TextWrapping="Wrap"
               Text="本地数据未能正常加载。请关闭程序后重新打开;若问题持续,请把日志目录中的最新日志文件提供给维护人员。" />
    <TextBlock x:Name="LogPathText" TextWrapping="Wrap" Opacity="0.6" FontSize="12" />
    <Button Content="退出" HorizontalAlignment="Right" Click="OnExitClick" />
  </StackPanel>
</Window>
```

创建 `src/InvoiceFlowAI.App/Bootstrap/BootstrapErrorWindow.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InvoiceFlowAI.App.Bootstrap;

public partial class BootstrapErrorWindow : Window
{
    public BootstrapErrorWindow()
    {
        InitializeComponent();
        LogPathText.Text = $"日志目录:{LogPaths.LogDirectory}";
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 9: 编译并手动确认窗口仍工作**

```bash
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj -c Debug
```

Expected: 0 errors。启动应用,确认 WebView 整壳照常显示、界面为暗色、中文正常(阶段 0 的唯一可见变化是主题)。

- [ ] **Step 10: 补 LGPL 许可与通知文件**

创建 `licenses/LGPL-3.0.txt`(从 AtomUI 包或 GNU 官方取全文,文件首行必须为 `GNU LESSER GENERAL PUBLIC LICENSE`):

```powershell
Copy-Item -LiteralPath "$env:USERPROFILE\.nuget\packages\atomui.core\6.2.8\LICENSE" -Destination "licenses\LGPL-3.0.txt" -Force
```

若包内无 LICENSE,则下载 `https://www.gnu.org/licenses/lgpl-3.0.txt` 保存为该文件,并确认文件以 `GNU LESSER GENERAL PUBLIC LICENSE` 开头。

在 `licenses/THIRD-PARTY-NOTICES.txt` 中新增 AtomUI 与 CommunityToolkit 条目(放在 Avalonia 条目之前):

```
------------------------------------------------------------------------
AtomUI 6.2.8 (Desktop Controls, Icons.AntDesign)
------------------------------------------------------------------------
  Copyright (c) AtomUI contributors.
  Licensed under the GNU Lesser General Public License, version 3.0.
  Shipped as separate, unmodified DLLs alongside InvoiceFlowAI.exe;
  the full license text is installed at licenses\LGPL-3.0.txt.
  https://github.com/AtomUI/AtomUI

------------------------------------------------------------------------
CommunityToolkit.Mvvm 8.4.2
------------------------------------------------------------------------
  Copyright (c) .NET Foundation and Contributors.
  Licensed under the MIT License.
  https://github.com/CommunityToolkit/dotnet
```

同时修正既有版本号事实错误:.NET Runtime + SDK 10.0.100 → 10.0.401、Avalonia 12.1.0 → 12.1.4、Avalonia.Controls.WebView 12.1.0 → 12.1.1、Microsoft.Extensions.AI 10.10.0 → 10.11.0、MailKit 4.18.0 → 4.18.1、Sdcb.SimdPaddleOCR 1.4.2 → 2.0.0、PDFiumCore 155.0.8057 → 157.0.8100、Microsoft.Playwright 1.62.0 → 1.63.0 并把 `(test-only)` 改为 `(URL recovery dependency)`、`xunit 2.x` → `xunit.v3 4.0.2`、Serilog 3.x → 4.4.0。

- [ ] **Step 11: 打包链路带上 LGPL 文本**

`build/windows/publish_dotnet_app.ps1` 在复制 THIRD-PARTY-NOTICES 的同一行后追加:

```powershell
    Copy-Item -LiteralPath (Join-Path $RepoRoot "licenses\LGPL-3.0.txt") -Destination (Join-Path $LicenseDirectory "LGPL-3.0.txt") -Force
```

`src/InvoiceFlowAI.Installer/Product.wxs` 的 `LicenseFiles` ComponentGroup 追加:

```xml
      <File Id="LgplLicense"
            Name="LGPL-3.0.txt"
            Source="licenses\LGPL-3.0.txt" />
```

- [ ] **Step 12: 全量测试并提交**

```bash
dotnet test InvoiceFlowAI.sln
git add Directory.Packages.props src/InvoiceFlowAI.App tests/InvoiceFlowAI.App.Tests licenses build/windows/publish_dotnet_app.ps1 src/InvoiceFlowAI.Installer/Product.wxs
git commit -m "feat(app): bootstrap AtomUI 6.2.8 with dark theme and headless smoke test

- add AtomUI.Desktop.Controls, AtomUI.Icons.AntDesign, CommunityToolkit.Mvvm pins
- configure AtomUI (DaybreakBlue + dark algorithms, zh-CN) via shared AtomUiConfiguration
- log to %LOCALAPPDATA%\\InvoiceFlowAI\\logs and show a plain fallback window on startup failure
- ship LGPL-3.0 text and update third-party notices

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 阶段 0 — 作用域操作抽象与页面服务接口

**Files:**
- Create: `src/InvoiceFlowAI.App/Services/IScopedOperationRunner.cs`
- Create: `src/InvoiceFlowAI.App/Services/ScopedOperationRunner.cs`
- Modify: `src/InvoiceFlowAI.App/Desktop/IDesktopActionService.cs:23-26`
- Modify: `src/InvoiceFlowAI.App/Desktop/AvaloniaDesktopActionService.cs`(`AvaloniaUiDispatcher`)
- Modify: `src/InvoiceFlowAI.Application/Accounts/AccountApplicationService.cs:7`
- Modify: `src/InvoiceFlowAI.Application/Accounts/AccountTestService.cs:12`
- Modify: `src/InvoiceFlowAI.Application/Runs/RunResultsQueryService.cs:8`
- Create: `src/InvoiceFlowAI.Application/Accounts/IAccountApplicationService.cs`
- Create: `src/InvoiceFlowAI.Application/Accounts/IAccountTestService.cs`
- Create: `src/InvoiceFlowAI.Application/Runs/IRunResultsQueryService.cs`
- Modify: `src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs:41-42`
- Modify: `src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs:230-231`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs:43-51`
- Test: `tests/InvoiceFlowAI.App.Tests/Services/ScopedOperationRunnerTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Services/PageServiceAliasTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Desktop/AvaloniaDesktopActionServiceTests.cs`(`FakeAvaloniaUiDispatcher` 补新成员)

**Interfaces:**
- Produces: `IScopedOperationRunner.RunAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>>, CancellationToken)`、`IScopedOperationRunner.RunAsync(Func<IServiceProvider, CancellationToken, Task>, CancellationToken)`。单例页面 VM 只能通过它访问 scoped 服务。
- Produces: `IAvaloniaUiDispatcher.Post(Action)` 与 `IAvaloniaUiDispatcher.InvokeAsync(Action, CancellationToken)`。
- Produces: `IAccountApplicationService { Task<AccountListResult> ListAsync(CancellationToken); Task<MailboxAccountSnapshot> SaveAsync(AccountSaveRequest, CancellationToken); }`。
- Produces: `IAccountTestService { Task<AccountTestResult> TestMailboxAsync(AccountTestRequest, CancellationToken); Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest, CancellationToken); }`。
- Produces: `IRunResultsQueryService { Task<RunResultsSnapshot> GetAsync(string? runId, CancellationToken); }`。

- [ ] **Step 1: 写失败的测试**

创建 `tests/InvoiceFlowAI.App.Tests/Services/ScopedOperationRunnerTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Services;

public sealed class ScopedOperationRunnerTests
{
    [Fact]
    public async Task Each_operation_gets_its_own_scope()
    {
        var services = new ServiceCollection();
        services.AddScoped<Probe>();
        await using var provider = services.BuildServiceProvider();
        var runner = new ScopedOperationRunner(provider.GetRequiredService<IServiceScopeFactory>());

        var first = await runner.RunAsync(
            (scope, _) => Task.FromResult(scope.GetRequiredService<Probe>().Id),
            TestContext.Current.CancellationToken);
        var second = await runner.RunAsync(
            (scope, _) => Task.FromResult(scope.GetRequiredService<Probe>().Id),
            TestContext.Current.CancellationToken);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Scoped_service_is_disposed_when_operation_completes()
    {
        var probe = new Probe();
        var services = new ServiceCollection();
        services.AddScoped(_ => probe);
        await using var provider = services.BuildServiceProvider();
        var runner = new ScopedOperationRunner(provider.GetRequiredService<IServiceScopeFactory>());

        await runner.RunAsync((scope, _) =>
        {
            scope.GetRequiredService<Probe>();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        probe.Disposed.Should().BeTrue();
    }

    private sealed class Probe : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
```

创建 `tests/InvoiceFlowAI.App.Tests/Services/PageServiceAliasTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Runs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Services;

public sealed class PageServiceAliasTests
{
    [Fact]
    public void Account_and_results_aliases_resolve_to_scoped_implementations()
    {
        using var provider = AppServiceProviderFactory.Create();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAccountApplicationService>()
            .Should().BeOfType<AccountApplicationService>();
        scope.ServiceProvider.GetRequiredService<IAccountTestService>()
            .Should().BeOfType<AccountTestService>();
        scope.ServiceProvider.GetRequiredService<IRunResultsQueryService>()
            .Should().BeOfType<RunResultsQueryService>();
        scope.ServiceProvider.GetRequiredService<IScopedOperationRunner>()
            .Should().BeOfType<ScopedOperationRunner>();
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~ScopedOperationRunnerTests|FullyQualifiedName~PageServiceAliasTests"
```

Expected: 编译失败,`ScopedOperationRunner` 与三个接口不存在。

- [ ] **Step 3: 实现 runner 与接口**

创建 `src/InvoiceFlowAI.App/Services/IScopedOperationRunner.cs`:

```csharp
namespace InvoiceFlowAI.App.Services;

/// <summary>
/// 单例页面 ViewModel 访问 scoped 应用服务的唯一通道。每次操作创建一个新作用域,
/// 避免长生命周期对象持有 DbContext。
/// </summary>
public interface IScopedOperationRunner
{
    Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task RunAsync(
        Func<IServiceProvider, CancellationToken, Task> operation,
        CancellationToken cancellationToken);
}
```

创建 `src/InvoiceFlowAI.App/Services/ScopedOperationRunner.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Services;

public sealed class ScopedOperationRunner(IServiceScopeFactory scopeFactory) : IScopedOperationRunner
{
    public async Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await operation(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(
        Func<IServiceProvider, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var scope = scopeFactory.CreateAsyncScope();
        await operation(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }
}
```

创建 `src/InvoiceFlowAI.Application/Accounts/IAccountApplicationService.cs`:

```csharp
using InvoiceFlowAI.Contracts.Accounts;

namespace InvoiceFlowAI.Application.Accounts;

public interface IAccountApplicationService
{
    Task<AccountListResult> ListAsync(CancellationToken cancellationToken);
    Task<MailboxAccountSnapshot> SaveAsync(AccountSaveRequest request, CancellationToken cancellationToken);
}
```

创建 `src/InvoiceFlowAI.Application/Accounts/IAccountTestService.cs`:

```csharp
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Accounts;

public interface IAccountTestService
{
    Task<AccountTestResult> TestMailboxAsync(AccountTestRequest request, CancellationToken cancellationToken);
    Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken);
}
```

创建 `src/InvoiceFlowAI.Application/Runs/IRunResultsQueryService.cs`:

```csharp
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.Application.Runs;

public interface IRunResultsQueryService
{
    Task<RunResultsSnapshot> GetAsync(string? runId, CancellationToken cancellationToken);
}
```

把三个实现类改为实现接口:`public sealed class AccountApplicationService : IAccountApplicationService`,`public sealed class AccountTestService : IAccountTestService`,`public sealed class RunResultsQueryService : IRunResultsQueryService`。

- [ ] **Step 4: 扩展 UI 调度接口**

`src/InvoiceFlowAI.App/Desktop/IDesktopActionService.cs` 的 `IAvaloniaUiDispatcher` 改为:

```csharp
public interface IAvaloniaUiDispatcher
{
    Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken);
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
    void Post(Action action);
}
```

`AvaloniaDesktopActionService.cs` 的 `AvaloniaUiDispatcher` 补充:

```csharp
    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        => InvokeAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }
```

`tests/InvoiceFlowAI.App.Tests/Desktop/AvaloniaDesktopActionServiceTests.cs` 的 `FakeAvaloniaUiDispatcher` 同步补上新成员:

```csharp
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            InvocationCount++;
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        public void Post(Action action) => action();
```

- [ ] **Step 5: 注册别名**

`src/InvoiceFlowAI.Infrastructure/InvoiceFlowAI.InfrastructureServiceCollectionExtensions.cs` 在 `AddScoped<AccountApplicationService>();` 之后追加:

```csharp
        services.AddScoped<IAccountApplicationService>(provider => provider.GetRequiredService<AccountApplicationService>());
        services.AddScoped<IAccountTestService>(provider => provider.GetRequiredService<AccountTestService>());
```

`src/InvoiceFlowAI.Application/Pipeline/PipelineRecipeGraphFactory.cs` 在 `AddScoped<RunResultsQueryService>();` 之后追加:

```csharp
        services.AddScoped<IRunResultsQueryService>(provider => provider.GetRequiredService<RunResultsQueryService>());
```

`src/InvoiceFlowAI.App/AppServiceProviderFactory.cs` 在 `services.AddSingleton<IDirectoryPicker, AvaloniaDirectoryPicker>();` 之后追加:

```csharp
        services.AddSingleton<IScopedOperationRunner, ScopedOperationRunner>();
```

并补 `using InvoiceFlowAI.App.Services;`。

- [ ] **Step 6: 运行测试**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~ScopedOperationRunnerTests|FullyQualifiedName~PageServiceAliasTests"
```

Expected: PASS。

- [ ] **Step 7: 全量测试并提交**

```bash
dotnet test InvoiceFlowAI.sln
git add src/InvoiceFlowAI.App src/InvoiceFlowAI.Application src/InvoiceFlowAI.Infrastructure tests/InvoiceFlowAI.App.Tests
git commit -m "feat(app): add scoped operation runner and page-level application interfaces

- IScopedOperationRunner creates a fresh scope per page operation
- extract IAccountApplicationService, IAccountTestService, IRunResultsQueryService
- add IAvaloniaUiDispatcher.Post and Action overloads

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 阶段 1a — 应用事件总线与发布器扇出

**Files:**
- Create: `src/InvoiceFlowAI.App/Events/AppRunEvent.cs`
- Create: `src/InvoiceFlowAI.App/Events/IAppEventBus.cs`
- Create: `src/InvoiceFlowAI.App/Events/AppEventBus.cs`
- Create: `src/InvoiceFlowAI.App/Events/NativeRunEventPublisher.cs`
- Create: `src/InvoiceFlowAI.App/Events/RunEventPublisherFanOut.cs`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs:44-48`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`(事件发布器断言)
- Test: `tests/InvoiceFlowAI.App.Tests/Events/AppEventBusTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Events/RunEventPublisherFanOutTests.cs`

**Interfaces:**
- Consumes: `IRunEventPublisher.Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)`。
- Produces: `AppRunEvent(string EventName, string RunId, long EventSequence, DateTimeOffset EmittedAtUtc, object Payload)`。
- Produces: `IAppEventBus { void Publish(AppRunEvent runEvent); IDisposable Subscribe(Action<AppRunEvent> handler); }`。
- Produces: `IRunEventPublisher` 在容器中解析为 `RunEventPublisherFanOut`(过渡期同时喂 WebView 与原生总线)。

- [ ] **Step 1: 写失败的测试**

创建 `tests/InvoiceFlowAI.App.Tests/Events/AppEventBusTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Events;

public sealed class AppEventBusTests
{
    [Fact]
    public void Subscriber_receives_published_event_until_disposed()
    {
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var received = new List<AppRunEvent>();
        var subscription = bus.Subscribe(received.Add);

        var first = new AppRunEvent("run.progress", "run-1", 1, DateTimeOffset.UnixEpoch, new object());
        bus.Publish(first);
        subscription.Dispose();
        bus.Publish(new AppRunEvent("run.progress", "run-1", 2, DateTimeOffset.UnixEpoch, new object()));

        received.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void Throwing_subscriber_does_not_block_other_subscribers()
    {
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        var received = 0;
        bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe(_ => received++);

        bus.Publish(new AppRunEvent("run.terminal", "run-1", 3, DateTimeOffset.UnixEpoch, new object()));

        received.Should().Be(1);
    }

    [Fact]
    public void Native_publisher_maps_publisher_arguments_to_event()
    {
        var bus = new AppEventBus(NullLogger<AppEventBus>.Instance);
        AppRunEvent? captured = null;
        bus.Subscribe(runEvent => captured = runEvent);
        var payload = new object();
        var emittedAt = DateTimeOffset.UnixEpoch;

        new NativeRunEventPublisher(bus).Publish("run.terminal", payload, "run-9", 42, emittedAt);

        captured.Should().NotBeNull();
        captured!.EventName.Should().Be("run.terminal");
        captured.RunId.Should().Be("run-9");
        captured.EventSequence.Should().Be(42);
        captured.EmittedAtUtc.Should().Be(emittedAt);
        captured.Payload.Should().BeSameAs(payload);
    }
}
```

创建 `tests/InvoiceFlowAI.App.Tests/Events/RunEventPublisherFanOutTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.Application.Runs;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Events;

public sealed class RunEventPublisherFanOutTests
{
    [Fact]
    public void Fan_out_publishes_to_every_inner_publisher()
    {
        var first = new RecordingPublisher();
        var second = new RecordingPublisher();
        var fanOut = new RunEventPublisherFanOut([first, second]);

        fanOut.Publish("run.progress", new object(), "run-1", 7, DateTimeOffset.UnixEpoch);

        first.Calls.Should().Be(1);
        second.Calls.Should().Be(1);
        first.LastRunId.Should().Be("run-1");
        second.LastSequence.Should().Be(7);
    }

    private sealed class RecordingPublisher : IRunEventPublisher
    {
        public int Calls { get; private set; }
        public string? LastRunId { get; private set; }
        public long LastSequence { get; private set; }

        public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
        {
            Calls++;
            LastRunId = runId;
            LastSequence = eventSequence;
        }
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~AppEventBusTests|FullyQualifiedName~RunEventPublisherFanOutTests"
```

Expected: 编译失败,`AppEventBus`、`NativeRunEventPublisher`、`RunEventPublisherFanOut` 不存在。

- [ ] **Step 3: 实现事件类型与总线**

创建 `src/InvoiceFlowAI.App/Events/AppRunEvent.cs`:

```csharp
namespace InvoiceFlowAI.App.Events;

/// <summary>
/// 运行事件的 UI 侧副本。Payload 只作刷新触发器使用,页面不得解析其内容,
/// 以免把后端原始字段(状态码、英文日志)泄漏到界面。
/// </summary>
public sealed record AppRunEvent(
    string EventName,
    string RunId,
    long EventSequence,
    DateTimeOffset EmittedAtUtc,
    object Payload);
```

创建 `src/InvoiceFlowAI.App/Events/IAppEventBus.cs`:

```csharp
namespace InvoiceFlowAI.App.Events;

public interface IAppEventBus
{
    void Publish(AppRunEvent runEvent);
    IDisposable Subscribe(Action<AppRunEvent> handler);
}
```

创建 `src/InvoiceFlowAI.App/Events/AppEventBus.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.Events;

public sealed class AppEventBus(ILogger<AppEventBus> logger) : IAppEventBus
{
    private readonly object _gate = new();
    private ImmutableArray<Action<AppRunEvent>> _handlers = [];

    public void Publish(AppRunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        var handlers = Volatile.Read(ref _handlers);
        foreach (var handler in handlers)
        {
            try
            {
                handler(runEvent);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "运行事件订阅者处理失败:{EventName}", runEvent.EventName);
            }
        }
    }

    public IDisposable Subscribe(Action<AppRunEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _handlers = _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<AppRunEvent> handler)
    {
        lock (_gate)
        {
            _handlers = _handlers.Remove(handler);
        }
    }

    private sealed class Subscription(AppEventBus owner, Action<AppRunEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(handler);
            }
        }
    }
}
```

- [ ] **Step 4: 实现两个发布器**

创建 `src/InvoiceFlowAI.App/Events/NativeRunEventPublisher.cs`:

```csharp
using InvoiceFlowAI.Application.Runs;

namespace InvoiceFlowAI.App.Events;

public sealed class NativeRunEventPublisher(IAppEventBus bus) : IRunEventPublisher
{
    public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
        => bus.Publish(new AppRunEvent(eventName, runId, eventSequence, emittedAtUtc, payload));
}
```

创建 `src/InvoiceFlowAI.App/Events/RunEventPublisherFanOut.cs`:

```csharp
using InvoiceFlowAI.Application.Runs;

namespace InvoiceFlowAI.App.Events;

/// <summary>
/// 过渡期发布器:同一个运行事件同时送给 WebView 桥与原生事件总线。
/// 任务 12 删除 WebView 后,本类型与其注册一并删除。
/// </summary>
public sealed class RunEventPublisherFanOut : IRunEventPublisher
{
    private readonly IReadOnlyList<IRunEventPublisher> _publishers;

    public RunEventPublisherFanOut(IEnumerable<IRunEventPublisher> publishers)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        _publishers = publishers.ToArray();
    }

    public void Publish(string eventName, object payload, string runId, long eventSequence, DateTimeOffset emittedAtUtc)
    {
        foreach (var publisher in _publishers)
        {
            publisher.Publish(eventName, payload, runId, eventSequence, emittedAtUtc);
        }
    }
}
```

- [ ] **Step 5: 改 DI 注册**

`src/InvoiceFlowAI.App/AppServiceProviderFactory.cs` 用以下代码替换第 48 行的 `IRunEventPublisher` 注册:

```csharp
        services.AddSingleton<IAppEventBus, AppEventBus>();
        services.AddSingleton<NativeRunEventPublisher>();
        services.AddSingleton<RunEventPublisherFanOut>(provider => new RunEventPublisherFanOut(
        [
            provider.GetRequiredService<WebViewRunEventPublisher>(),
            provider.GetRequiredService<NativeRunEventPublisher>(),
        ]));
        services.AddSingleton<IRunEventPublisher>(provider => provider.GetRequiredService<RunEventPublisherFanOut>());
```

补 `using InvoiceFlowAI.App.Events;`。

- [ ] **Step 6: 更新既有工厂测试断言**

`tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs` 中找到断言 `IRunEventPublisher` 为 `WebViewRunEventPublisher` 的语句,改为:

```csharp
        provider.GetRequiredService<IRunEventPublisher>().Should().BeOfType<RunEventPublisherFanOut>();
        provider.GetRequiredService<IAppEventBus>().Should().BeOfType<AppEventBus>();
```

- [ ] **Step 7: 运行测试**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~AppEventBusTests|FullyQualifiedName~RunEventPublisherFanOutTests|FullyQualifiedName~AppServiceProviderFactoryTests"
```

Expected: PASS。

- [ ] **Step 8: 提交**

```bash
dotnet test InvoiceFlowAI.sln
git add src/InvoiceFlowAI.App tests/InvoiceFlowAI.App.Tests
git commit -m "feat(app): add IAppEventBus with native publisher and transition fan-out

- native run events flow into an in-process bus for view models
- IRunEventPublisher fans out to WebView bridge and native bus until WebView removal

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 阶段 1a — SPA 嵌入模式

**Files:**
- Create: `templates/embed_mode.js`
- Modify: `templates/index.html:1303-1313`(脚本标签)与 CSS 追加(文件末尾 `</style>` 前)
- Modify: `templates/index_app.js:1597-1610`(`App()`)
- Modify: `templates/index_app.compiled.js`(由构建脚本生成)
- Modify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`(Content 项)
- Test: `tests/InvoiceFlowAI.App.Tests/Rpc/embed_mode.test.mjs`
- Modify: `.github/workflows/windows.yml`(Node 测试与编译检查)

**Interfaces:**
- Produces: `window.__invoiceFlowEmbed = { enabled: boolean, initialPath: string }`。
- Produces: `window.__invoiceFlowHostNavigate(path: string)` — 由 React Router 的 `navigate` 注册,宿主调用它切换页面。
- Produces: `body.is-embedded` — 隐藏 `.app-sidebar`、`.window-chrome`、`.bridge-status`。

- [ ] **Step 1: 写失败的 Node 测试**

创建 `tests/InvoiceFlowAI.App.Tests/Rpc/embed_mode.test.mjs`:

```javascript
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../../templates/embed_mode.js", import.meta.url), "utf8");

function runEmbedMode(search, bodyClasses) {
    const classes = new Set(bodyClasses || []);
    const context = {
        window: { location: { search } },
        document: { body: { classList: { add: (value) => classes.add(value) } } },
    };
    context.window.document = context.document;
    vm.createContext(context);
    vm.runInContext(source, context);
    return { window: context.window, classes };
}

test("embed mode maps view names to spa routes", () => {
    const { window } = runEmbedMode("?embed=1&view=processing", []);
    assert.equal(window.__invoiceFlowEmbed.enabled, true);
    assert.equal(window.__invoiceFlowEmbed.initialPath, "/processing");
});

test("embed mode hides the spa chrome", () => {
    const { classes } = runEmbedMode("?embed=1&view=settings", []);
    assert.equal(classes.has("is-embedded"), true);
});

test("non embed mode keeps the spa intact", () => {
    const { window, classes } = runEmbedMode("", []);
    assert.equal(window.__invoiceFlowEmbed.enabled, false);
    assert.equal(window.__invoiceFlowEmbed.initialPath, "/");
    assert.equal(classes.has("is-embedded"), false);
});

test("unknown view falls back to the settings route", () => {
    const { window } = runEmbedMode("?embed=1&view=unknown", []);
    assert.equal(window.__invoiceFlowEmbed.initialPath, "/");
});
```

运行:

```bash
node --test tests/InvoiceFlowAI.App.Tests/Rpc/embed_mode.test.mjs
```

Expected: FAIL,`ENOENT`(文件不存在)。

- [ ] **Step 2: 实现嵌入模式脚本**

创建 `templates/embed_mode.js`:

```javascript
(function () {
    "use strict";

    var VIEW_PATHS = {
        settings: "/",
        processing: "/processing",
        analysis: "/analysis",
    };

    var params = new URLSearchParams(window.location.search);
    var enabled = params.get("embed") === "1";
    var requested = params.get("view") || "";
    var initialPath = enabled ? (VIEW_PATHS[requested] || "/") : "/";

    window.__invoiceFlowEmbed = { enabled: enabled, initialPath: initialPath };

    if (enabled && document.body) {
        document.body.classList.add("is-embedded");
    }
})();
```

- [ ] **Step 3: 在 index.html 中加载并隐藏 SPA 外壳**

`templates/index.html` 的脚本顺序改为(在 `rpc_client.js` 之前插入):

```html
    <script src="./embed_mode.js"></script>
    <script src="./rpc_client.js"></script>
```

在 `</style>` 之前追加:

```css
        /* Embedded mode: the native Avalonia shell owns the sidebar and window chrome. */
        body.is-embedded .app-shell { border-radius: 0; }
        body.is-embedded .app-main { border-radius: 0; }
        body.is-embedded .app-sidebar,
        body.is-embedded .window-chrome,
        body.is-embedded .bridge-status { display: none; }
```

- [ ] **Step 4: 让 React 路由接受宿主导航**

`templates/index_app.js` 把 `App()` 替换为:

```javascript
function EmbedNavigationBridge() {
    const navigate = useNavigate();
    useEffect(() => {
        window.__invoiceFlowHostNavigate = (path) => navigate(path);
        return () => {
            delete window.__invoiceFlowHostNavigate;
        };
    }, [navigate]);
    return null;
}

function App() {
    const [showDisclaimer, setShowDisclaimer] = useState(false);
    const embed = window.__invoiceFlowEmbed || { enabled: false, initialPath: "/" };
    return (
        <>
            <MemoryRouter initialEntries={[embed.initialPath || "/"]}>
                <EmbedNavigationBridge />
                <Routes>
                    <Route path="/" element={<SettingsPage onOpenDisclaimer={() => setShowDisclaimer(true)} />} />
                    <Route path="/processing" element={<ProcessingPage onOpenDisclaimer={() => setShowDisclaimer(true)} />} />
                    <Route path="/analysis" element={<AnalysisPage onOpenDisclaimer={() => setShowDisclaimer(true)} />} />
                </Routes>
            </MemoryRouter>
            <DisclaimerDialog open={showDisclaimer} onClose={() => setShowDisclaimer(false)} />
        </>
    );
}
```

- [ ] **Step 5: 重新生成编译产物并跑全部 JS 测试**

```bash
node build/compile-webview-page.mjs
node build/compile-webview-page.mjs --check
node --test tests/InvoiceFlowAI.App.Tests/Rpc/
```

Expected: `--check` 退出码 0;全部 Node 测试 PASS。

- [ ] **Step 6: 把新脚本纳入构建输出**

`src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj` 的 Content 组内追加:

```xml
    <Content Include="..\..\templates\embed_mode.js" Link="Web\embed_mode.js"
             CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="Always" />
```

- [ ] **Step 7: CI 增加前端校验**

`.github/workflows/windows.yml` 在 `dotnet test` 之前插入(Node 已随 windows runner 提供):

```yaml
      - name: Verify embedded SPA assets
        shell: pwsh
        run: |
          node build/compile-webview-page.mjs --check
          node --test tests/InvoiceFlowAI.App.Tests/Rpc/
```

- [ ] **Step 8: 提交**

```bash
git add templates src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj tests/InvoiceFlowAI.App.Tests/Rpc/embed_mode.test.mjs .github/workflows/windows.yml
git commit -m "feat(spa): add embed mode for the native shell transition

- ?embed=1&view=<settings|processing|analysis> selects the initial route
- window.__invoiceFlowHostNavigate lets the native sidebar drive the router
- body.is-embedded hides the SPA sidebar, window chrome and bridge status

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 阶段 1a — 原生壳(MainWindow + 侧边栏 + 嵌入宿主)

**Files:**
- Create: `src/InvoiceFlowAI.App/Navigation/IPageViewModel.cs`
- Create: `src/InvoiceFlowAI.App/Navigation/IAppNavigator.cs`
- Create: `src/InvoiceFlowAI.App/Navigation/AppNavigator.cs`
- Create: `src/InvoiceFlowAI.App/Navigation/WebPageViewModel.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/ShellViewModel.cs`
- Create: `src/InvoiceFlowAI.App/Services/IDialogService.cs`
- Create: `src/InvoiceFlowAI.App/Services/AtomUiDialogService.cs`
- Create: `src/InvoiceFlowAI.App/Services/INotificationService.cs`
- Create: `src/InvoiceFlowAI.App/Services/AtomUiNotificationService.cs`
- Create: `src/InvoiceFlowAI.App/Services/IDisclaimerService.cs`
- Create: `src/InvoiceFlowAI.App/Services/DisclaimerService.cs`
- Create: `src/InvoiceFlowAI.App/Services/IRunSession.cs`
- Create: `src/InvoiceFlowAI.App/Services/IRunShutdownService.cs`
- Create: `src/InvoiceFlowAI.App/Services/RunShutdownService.cs`
- Create: `src/InvoiceFlowAI.App/Views/DisclaimerWindow.axaml`
- Create: `src/InvoiceFlowAI.App/Views/DisclaimerWindow.axaml.cs`
- Create: `src/InvoiceFlowAI.App/Views/EmbedWebViewHost.cs`
- Modify: `src/InvoiceFlowAI.App/MainWindow.axaml`(整体替换)
- Modify: `src/InvoiceFlowAI.App/MainWindow.axaml.cs`(整体替换)
- Modify: `src/InvoiceFlowAI.App/App.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs:29-31,63-65`
- Modify: `src/InvoiceFlowAI.App/Desktop/IDesktopActionService.cs`(删除 `ExecuteWindowCommandAsync` 与 `IWindowCommandDispatcher`)
- Modify: `src/InvoiceFlowAI.App/Desktop/AvaloniaDesktopActionService.cs:15-25,142-160,235-260`
- Delete: `src/InvoiceFlowAI.App/Rpc/WindowCommandRpcHandler.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Navigation/AppNavigatorTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/ViewModels/ShellViewModelTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Desktop/AvaloniaDesktopActionServiceTests.cs`(删除 window 命令测试与相应假实现)

**Interfaces:**
- Produces: `IPageViewModel { string Key; string Title; string Eyebrow; Task OnActivatedAsync(CancellationToken); Task OnDeactivatedAsync(CancellationToken); }`。
- Produces: `IAppNavigator.NavigateAsync(string key, CancellationToken cancellationToken = default)`;`AppNavigator.Attach(Func<string, CancellationToken, Task> handler)`。
- Produces: `ShellViewModel(IAppNavigator, IEnumerable<IPageViewModel>, IDisclaimerService, IDesktopPathLauncher)`,成员 `Pages`、`CurrentPage`、`Version`、`InitializeAsync`、`NavigateAsync`、`FlushAsync`、命令 `GoHomeCommand` / `ShowDisclaimerCommand` / `OpenWebsiteCommand`。
- Produces: `IDialogService.ShowConfirmAsync(string title, string message, string confirmText, string cancelText, CancellationToken = default)`。
- Produces: `INotificationService.Show(string message, NotificationKind kind)`,`NotificationKind { Information, Success, Warning, Error }`。
- Produces: `IRunSession { string? RunId { get; } void SetRunId(string? runId); }`(单例 `RunSession`)。
- Produces: `IRunShutdownService.ConfirmCloseAsync(CancellationToken)` → `RunCloseDecision { Close, Cancel }`。
- Consumes: Task 3 的 `IAppEventBus`、Task 2 的 `IScopedOperationRunner`。
- 平台能力说明(设计 §4.3 的落地方式):仓库已有 `IDesktopActionService`(文件/文件夹打开)与 `IDirectoryPicker`(目录选择),本计划直接复用,不新增 `IWindowActionService` / `IFolderPickerService` / `IFileOpenService`;窗口最小化/最大化/关闭由 `atom:Window` 标题栏按钮原生完成,不再经服务层。
- 说明:MainWindow 的 headless 冒烟测试推迟到 Task 12,因为 `EmbedWebViewHost` 内的 `NativeWebView` 无法在 Avalonia.Headless 下构造。

- [ ] **Step 1: 写失败的测试**

创建 `tests/InvoiceFlowAI.App.Tests/Navigation/AppNavigatorTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Navigation;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Navigation;

public sealed class AppNavigatorTests
{
    [Fact]
    public async Task Navigate_before_attach_is_a_no_op()
    {
        var navigator = new AppNavigator();
        await navigator.NavigateAsync("settings", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Navigate_forwards_to_attached_handler()
    {
        var navigator = new AppNavigator();
        var calls = new List<string>();
        navigator.Attach((key, _) =>
        {
            calls.Add(key);
            return Task.CompletedTask;
        });

        await navigator.NavigateAsync("processing", TestContext.Current.CancellationToken);

        calls.Should().Equal("processing");
    }
}
```

创建 `tests/InvoiceFlowAI.App.Tests/ViewModels/ShellViewModelTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.ViewModels;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    [Fact]
    public async Task Initialize_activates_the_first_page_in_shell_order()
    {
        var analysis = new FakePage("analysis");
        var settings = new FakePage("settings");
        var processing = new FakePage("processing");
        var shell = CreateShell(analysis, settings, processing);

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        shell.CurrentPage.Should().BeSameAs(settings);
        settings.Activations.Should().Be(1);
        analysis.Activations.Should().Be(0);
    }

    [Fact]
    public async Task Navigation_deactivates_previous_page_and_activates_target()
    {
        var settings = new FakePage("settings");
        var processing = new FakePage("processing");
        var shell = CreateShell(settings, processing);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await shell.NavigateAsync("processing", TestContext.Current.CancellationToken);

        settings.Deactivations.Should().Be(1);
        processing.Activations.Should().Be(1);
        shell.CurrentPage.Should().BeSameAs(processing);
    }

    [Fact]
    public async Task Navigation_to_unknown_or_current_page_is_ignored()
    {
        var settings = new FakePage("settings");
        var shell = CreateShell(settings);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await shell.NavigateAsync("missing", TestContext.Current.CancellationToken);
        await shell.NavigateAsync("settings", TestContext.Current.CancellationToken);

        settings.Activations.Should().Be(1);
        settings.Deactivations.Should().Be(0);
    }

    [Fact]
    public async Task Page_can_navigate_through_injected_navigator()
    {
        var settings = new FakePage("settings");
        var processing = new FakePage("processing");
        var shell = CreateShell(settings, processing);
        var navigator = (AppNavigator)shell.GetType()
            .GetField("_navigator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(shell)!;
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await navigator.NavigateAsync("processing", TestContext.Current.CancellationToken);

        shell.CurrentPage.Should().BeSameAs(processing);
    }

    private static ShellViewModel CreateShell(params FakePage[] pages)
        => new(
            new AppNavigator(),
            pages,
            new FakeDisclaimerService(),
            new FakePathLauncher());

    private sealed class FakePage(string key) : IPageViewModel
    {
        public string Key { get; } = key;
        public string Title => key;
        public string Eyebrow => "Step";
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }

        public Task OnActivatedAsync(CancellationToken cancellationToken)
        {
            Activations++;
            return Task.CompletedTask;
        }

        public Task OnDeactivatedAsync(CancellationToken cancellationToken)
        {
            Deactivations++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDisclaimerService : IDisclaimerService
    {
        public Task ShowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePathLauncher : IDesktopPathLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
```

> 反射取 `_navigator` 只为验证"页面经注入的导航器跳转";实现时把字段命名为 `_navigator` 即可。若不想用反射,可改为向测试暴露 `internal IAppNavigator Navigator => _navigator;` 并加 `InternalsVisibleTo`,二选一,保持测试可读。

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~AppNavigatorTests|FullyQualifiedName~ShellViewModelTests"
```

Expected: 编译失败,`AppNavigator`、`ShellViewModel`、`IDisclaimerService` 不存在。

- [ ] **Step 3: 实现导航契约**

创建 `src/InvoiceFlowAI.App/Navigation/IPageViewModel.cs`:

```csharp
namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 页面契约。实现不得从 <see cref="OnActivatedAsync"/> / <see cref="OnDeactivatedAsync"/>
/// 中发起导航:由导航触发的回调持有导航锁,该场景会立即抛出
/// <see cref="InvalidOperationException"/>。
/// </summary>
public interface IPageViewModel
{
    string Key { get; }
    string Title { get; }
    string Eyebrow { get; }
    Task OnActivatedAsync(CancellationToken cancellationToken);
    Task OnDeactivatedAsync(CancellationToken cancellationToken);
}
```

创建 `src/InvoiceFlowAI.App/Navigation/IAppNavigator.cs`:

```csharp
namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 页面导航入口。不要在 <see cref="IPageViewModel"/> 的生命周期回调中调用:
/// 回调期间导航锁已被持有,该场景会立即抛出 <see cref="InvalidOperationException"/>。
/// </summary>
public interface IAppNavigator
{
    Task NavigateAsync(string key, CancellationToken cancellationToken = default);
}
```

创建 `src/InvoiceFlowAI.App/Navigation/AppNavigator.cs`:

```csharp
namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 星型拓扑导航:页面只依赖 IAppNavigator,由 ShellViewModel 在初始化时 Attach 真正的处理器,
/// 避免页面之间互相引用造成循环依赖。
/// </summary>
public sealed class AppNavigator : IAppNavigator
{
    private Func<string, CancellationToken, Task>? _handler;

    public void Attach(Func<string, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public Task NavigateAsync(string key, CancellationToken cancellationToken = default)
        => _handler is null ? Task.CompletedTask : _handler(key, cancellationToken);
}
```

创建 `src/InvoiceFlowAI.App/Navigation/WebPageViewModel.cs`:

```csharp
namespace InvoiceFlowAI.App.Navigation;

/// <summary>
/// 过渡期占位页面:内容由嵌入模式的 WebView 承载,原生侧只负责标题与路由。
/// 每迁移一页,对应注册改用原生 ViewModel,Task 12 删除本类型。
/// </summary>
public sealed class WebPageViewModel(string key, string title, string eyebrow, string path) : IPageViewModel
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Eyebrow { get; } = eyebrow;
    public string Path { get; } = path;

    public Task OnActivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnDeactivatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: 实现 ShellViewModel**

创建 `src/InvoiceFlowAI.App/ViewModels/ShellViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly string[] PageOrder = ["settings", "processing", "analysis"];

    private readonly AppNavigator _navigator;
    private readonly IDisclaimerService _disclaimer;
    private readonly IDesktopPathLauncher _pathLauncher;
    private readonly IReadOnlyDictionary<string, IPageViewModel> _pages;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);

    // 生命周期回调持有导航锁;标记用于识别回调内发起的导航并快速失败,避免自死锁。
    private static readonly AsyncLocal<bool> LifecycleCallbackActive = new();

    [ObservableProperty]
    private IPageViewModel? _currentPage;

    private bool _initialized;

    public ShellViewModel(
        IAppNavigator navigator,
        IEnumerable<IPageViewModel> pages,
        IDisclaimerService disclaimer,
        IDesktopPathLauncher pathLauncher)
    {
        // 壳是导航图的唯一装配点,因此必须持有可 Attach 的具体实现。
        _navigator = navigator as AppNavigator ?? throw new ArgumentException(
            "Shell navigation requires an AppNavigator instance.",
            nameof(navigator));
        _disclaimer = disclaimer ?? throw new ArgumentNullException(nameof(disclaimer));
        _pathLauncher = pathLauncher ?? throw new ArgumentNullException(nameof(pathLauncher));
        ArgumentNullException.ThrowIfNull(pages);

        Pages = pages
            .OrderBy(page => Array.IndexOf(PageOrder, page.Key) is var index && index >= 0 ? index : int.MaxValue)
            .ToArray();
        _pages = Pages.ToDictionary(page => page.Key, StringComparer.Ordinal);
        Version = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public IReadOnlyList<IPageViewModel> Pages { get; }

    public string Version { get; }

    public string WebsiteUrl => "https://github.com/EthanYoQ/Invoice-Downloader";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _navigator.Attach(NavigateAsync);
        if (Pages.Count > 0)
        {
            await NavigateAsync(Pages[0].Key, cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task NavigateAsync(string key, CancellationToken cancellationToken)
    {
        if (LifecycleCallbackActive.Value)
        {
            throw new InvalidOperationException("导航不能在页面生命周期回调期间发起。");
        }

        if (!_pages.TryGetValue(key, out var target) || ReferenceEquals(CurrentPage, target))
        {
            return;
        }

        await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        LifecycleCallbackActive.Value = true;
        try
        {
            var previous = CurrentPage;
            if (previous is not null)
            {
                await previous.OnDeactivatedAsync(cancellationToken).ConfigureAwait(true);
            }

            CurrentPage = target;
            await target.OnActivatedAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            LifecycleCallbackActive.Value = false;
            _navigationGate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (CurrentPage is { } page)
        {
            await page.OnDeactivatedAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task GoHomeAsync(CancellationToken cancellationToken) => NavigateAsync("settings", cancellationToken);

    [RelayCommand]
    private Task ShowDisclaimerAsync(CancellationToken cancellationToken) => _disclaimer.ShowAsync(cancellationToken);

    [RelayCommand]
    private Task OpenWebsiteAsync(CancellationToken cancellationToken) => _pathLauncher.OpenAsync(WebsiteUrl, cancellationToken);
}
```

- [ ] **Step 5: 实现对话框、通知、免责声明与关闭确认**

创建 `src/InvoiceFlowAI.App/Services/IDialogService.cs`:

```csharp
namespace InvoiceFlowAI.App.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken = default);
}
```

创建 `src/InvoiceFlowAI.App/Services/AtomUiDialogService.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Media;
using AtomUI.Desktop.Controls;
using InvoiceFlowAI.App.Settings;

namespace InvoiceFlowAI.App.Services;

public sealed class AtomUiDialogService(IMainWindowAccessor windowAccessor) : IDialogService
{
    public async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        CancellationToken cancellationToken = default)
    {
        var content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        var options = new MessageBoxOptions
        {
            Title = title,
            OkButtonText = confirmText,
            CancelButtonText = cancelText,
            IsDragMovable = true,
            IsMaskClosable = false,
        };

        var result = await MessageBox.ShowMessageBoxModalAsync(
            content,
            options: options,
            topLevel: windowAccessor.MainWindow,
            cancellationToken: cancellationToken).ConfigureAwait(true);

        return result is DialogCode.Accepted;
    }
}
```

创建 `src/InvoiceFlowAI.App/Services/INotificationService.cs`:

```csharp
namespace InvoiceFlowAI.App.Services;

public enum NotificationKind
{
    Information,
    Success,
    Warning,
    Error,
}

public interface INotificationService
{
    void Show(string message, NotificationKind kind);
}
```

创建 `src/InvoiceFlowAI.App/Services/AtomUiNotificationService.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using AtomUI.Desktop.Controls;
using InvoiceFlowAI.App.Settings;

namespace InvoiceFlowAI.App.Services;

public sealed class AtomUiNotificationService(IMainWindowAccessor windowAccessor) : INotificationService
{
    private WindowMessageManager? _manager;

    public void Show(string message, NotificationKind kind)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Show(message, kind));
            return;
        }

        _manager ??= new WindowMessageManager(windowAccessor.MainWindow);
        _manager.Show(new Message(message, ToMessageType(kind)));
    }

    private static MessageType ToMessageType(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => MessageType.Success,
        NotificationKind.Warning => MessageType.Warning,
        NotificationKind.Error => MessageType.Error,
        _ => MessageType.Information,
    };
}
```

创建 `src/InvoiceFlowAI.App/Services/IDisclaimerService.cs`:

```csharp
namespace InvoiceFlowAI.App.Services;

public interface IDisclaimerService
{
    Task ShowAsync(CancellationToken cancellationToken = default);
}
```

创建 `src/InvoiceFlowAI.App/Services/DisclaimerService.cs`:

```csharp
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.Views;

namespace InvoiceFlowAI.App.Services;

public sealed class DisclaimerService(IMainWindowAccessor windowAccessor) : IDisclaimerService
{
    public Task ShowAsync(CancellationToken cancellationToken = default)
    {
        var dialog = new DisclaimerWindow();
        var owner = windowAccessor.MainWindow;
        if (owner is null)
        {
            dialog.Show();
            return Task.CompletedTask;
        }

        _ = dialog.ShowDialog(owner);
        return Task.CompletedTask;
    }
}
```

创建 `src/InvoiceFlowAI.App/Views/DisclaimerWindow.axaml`:

```xml
<atom:Window xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:atom="https://atomui.net"
             x:Class="InvoiceFlowAI.App.Views.DisclaimerWindow"
             Title="免责声明"
             Width="640" Height="580"
             WindowStartupLocation="CenterOwner">
  <ScrollViewer>
    <StackPanel Margin="28" Spacing="14">
      <TextBlock Text="免责声明" FontSize="12" Opacity="0.55" />
      <TextBlock Text="使用前请确认以下事项" FontSize="24" FontWeight="SemiBold" />

      <TextBlock Text="软件说明" FontWeight="SemiBold" />
      <ItemsControl x:Name="SoftwareItems">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding}" TextWrapping="Wrap" Margin="0,0,0,6" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

      <TextBlock Text="风险提示" FontWeight="SemiBold" />
      <ItemsControl x:Name="RiskItems">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding}" TextWrapping="Wrap" Margin="0,0,0,6" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

      <atom:Button ButtonType="Primary" Content="我已知晓"
                   HorizontalAlignment="Right"
                   Click="OnAcknowledgeClick" />
    </StackPanel>
  </ScrollViewer>
</atom:Window>
```

创建 `src/InvoiceFlowAI.App/Views/DisclaimerWindow.axaml.cs`:

```csharp
using Avalonia.Interactivity;
using AtomUI.Desktop.Controls;

namespace InvoiceFlowAI.App.Views;

public partial class DisclaimerWindow : AtomUI.Desktop.Controls.Window
{
    private static readonly string[] SoftwareItems =
    [
        "本软件用于票据整理、归档与复核辅助处理。",
        "本软件不附带任何真实邮箱凭据或 API Key。",
        "请仅填写并使用你自己的邮箱授权信息与 API Key。",
        "涉及报销、入账或合规判断的重要票据,请务必进行人工复核。",
    ];

    private static readonly string[] RiskItems =
    [
        "本工具用于票据整理与归档辅助,识别结果应结合业务流程进行复核。",
        "对于识别、归档、遗漏、误判及由此产生的后果,工具方不承担责任。",
        "使用者应自行遵守所在公司关于大模型、API 使用和数据安全管理的规定。",
        "涉及报销、入账或合规判断的重要票据,请务必进行人工复核。",
    ];

    public DisclaimerWindow()
    {
        InitializeComponent();
        SoftwareItemsControl.ItemsSource = SoftwareItems;
        RiskItemsControl.ItemsSource = RiskItems;
    }

    private ItemsControl SoftwareItemsControl => SoftwareItems;

    private ItemsControl RiskItemsControl => RiskItems;

    private void OnAcknowledgeClick(object? sender, RoutedEventArgs e) => Close();
}
```

> 上面的 `SoftwareItemsControl` / `RiskItemsControl` 命名与字段冲突,实现时请改为直接使用生成的命名控件:
> `x:Name="SoftwareList"`、`x:Name="RiskList"`,构造函数里 `SoftwareList.ItemsSource = SoftwareItems; RiskList.ItemsSource = RiskItems;`,并删除两个私有属性。

创建 `src/InvoiceFlowAI.App/Services/IRunSession.cs`:

```csharp
namespace InvoiceFlowAI.App.Services;

public interface IRunSession
{
    string? RunId { get; }
    void SetRunId(string? runId);
}

public sealed class RunSession : IRunSession
{
    public string? RunId { get; private set; }
    public void SetRunId(string? runId) => RunId = string.IsNullOrWhiteSpace(runId) ? null : runId;
}
```

创建 `src/InvoiceFlowAI.App/Services/IRunShutdownService.cs`:

```csharp
namespace InvoiceFlowAI.App.Services;

public enum RunCloseDecision
{
    Close,
    Cancel,
}

public interface IRunShutdownService
{
    Task<RunCloseDecision> ConfirmCloseAsync(CancellationToken cancellationToken);
}
```

创建 `src/InvoiceFlowAI.App/Services/RunShutdownService.cs`:

```csharp
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.Services;

public sealed class RunShutdownService(
    IScopedOperationRunner operations,
    IDialogService dialogs,
    ILogger<RunShutdownService> logger) : IRunShutdownService
{
    public async Task<RunCloseDecision> ConfirmCloseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runId = await operations.RunAsync(
                async (provider, token) =>
                {
                    var runService = provider.GetRequiredService<IDesktopRunService>();
                    var context = await runService.GetContextAsync(token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(context.RunId))
                    {
                        return null;
                    }

                    var progress = await runService.GetProgressAsync(context.RunId, token).ConfigureAwait(false);
                    return progress.IsRunning ? context.RunId : null;
                },
                cancellationToken).ConfigureAwait(true);

            if (runId is null)
            {
                return RunCloseDecision.Close;
            }

            var confirmed = await dialogs.ShowConfirmAsync(
                "关闭程序",
                "关闭程序会直接结束当前窗口与任务进程,请仅在确认后执行。",
                "停止并关闭",
                "取消",
                cancellationToken).ConfigureAwait(true);

            if (!confirmed)
            {
                return RunCloseDecision.Cancel;
            }

            await operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                    .StopAsync(new RunStopRequest(runId), token),
                cancellationToken).ConfigureAwait(true);

            return RunCloseDecision.Close;
        }
        catch (OperationCanceledException)
        {
            return RunCloseDecision.Cancel;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "关闭前的停止确认失败。");
            return RunCloseDecision.Cancel;
        }
    }
}
```

- [ ] **Step 6: 实现嵌入 WebView 宿主**

创建 `src/InvoiceFlowAI.App/Views/EmbedWebViewHost.cs`:

```csharp
using Avalonia.Controls;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Rpc;

namespace InvoiceFlowAI.App.Views;

/// <summary>
/// 过渡期宿主:一个共享的 WebView,以嵌入模式加载 SPA。
/// 首次导航设置 Source;后续导航通过 window.__invoiceFlowHostNavigate 在页内切换,
/// JS 桥未就绪时回退为重新加载。Task 12 随 WebView 一起删除。
/// </summary>
public sealed class EmbedWebViewHost : UserControl, IDisposable
{
    private readonly NativeWebView _webView = new();
    private readonly WebViewRunEventPublisher _runEventPublisher;
    private WebViewRpcBridge? _bridge;
    private bool _loaded;
    private bool _disposed;

    public EmbedWebViewHost(IRpcDispatcher rpcDispatcher, WebViewRunEventPublisher runEventPublisher)
    {
        ArgumentNullException.ThrowIfNull(rpcDispatcher);
        _runEventPublisher = runEventPublisher ?? throw new ArgumentNullException(nameof(runEventPublisher));
        Content = _webView;

        var channel = new AvaloniaNativeWebViewMessageChannel(_webView);
        _bridge = new WebViewRpcBridge(rpcDispatcher, channel, new WebViewRpcBridgeOptions());
        _runEventPublisher.Attach(_bridge);
        _bridge.Start();
    }

    public async Task NavigateAsync(WebPageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_disposed)
        {
            return;
        }

        if (!_loaded)
        {
            _loaded = true;
            _webView.Source = BuildIndexUri(page.Key);
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                // WebView2 的 InvokeScript 返回 JSON 编码后的原始结果,数字不带引号,
                // 因此脚本返回数字而不是字符串,这里与 "1" 比较。
                var ready = await _webView.InvokeScript("window.__invoiceFlowHostNavigate ? 1 : 0");
                if (_disposed)
                {
                    return;
                }

                if (ready == "1")
                {
                    await _webView.InvokeScript(
                        $"window.__invoiceFlowHostNavigate('{page.Path.Replace("'", "\\'")}');");
                    return;
                }
            }
            catch (Exception)
            {
                // 页面尚未完成加载;继续重试,超时后回退为整页加载。
            }

            await Task.Delay(100);
            if (_disposed)
            {
                return;
            }
        }

        _webView.Source = BuildIndexUri(page.Key);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_bridge is not null)
        {
            _bridge.Stop();
            _runEventPublisher.Detach(_bridge);
            _bridge = null;
        }
    }

    private static Uri BuildIndexUri(string viewKey)
    {
        var indexPath = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
        return new UriBuilder(new Uri(indexPath))
        {
            Query = $"embed=1&view={Uri.EscapeDataString(viewKey)}",
        }.Uri;
    }
}
```

- [ ] **Step 7: 替换 MainWindow**

`src/InvoiceFlowAI.App/MainWindow.axaml` 整体替换为:

```xml
<atom:Window xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:atom="https://atomui.net"
             xmlns:vm="using:InvoiceFlowAI.App.ViewModels"
             x:Class="InvoiceFlowAI.App.MainWindow"
             x:DataType="vm:ShellViewModel"
             Title="InvoiceFlowAI"
             Width="1180" Height="760"
             MinWidth="980" MinHeight="640"
             WindowStartupLocation="CenterScreen"
             IsMinimizeCaptionButtonVisible="True"
             IsMaximizeCaptionButtonVisible="True"
             IsCloseCaptionButtonVisible="True">
  <Grid ColumnDefinitions="252,*">
    <Grid Grid.Column="0" RowDefinitions="*,Auto" Margin="12,16,8,12">
      <atom:NavMenu x:Name="NavigationMenu" Mode="Vertical" />
      <StackPanel Grid.Row="1" Spacing="2">
        <atom:Button ButtonType="Text"
                     HorizontalAlignment="Stretch"
                     HorizontalContentAlignment="Left"
                     Content="回到首页"
                     Command="{Binding GoHomeCommand}" />
        <atom:Button ButtonType="Text"
                     HorizontalAlignment="Stretch"
                     HorizontalContentAlignment="Left"
                     Content="免责声明"
                     Command="{Binding ShowDisclaimerCommand}" />
        <atom:Button ButtonType="Text"
                     HorizontalAlignment="Stretch"
                     HorizontalContentAlignment="Left"
                     Content="产品官网"
                     Command="{Binding OpenWebsiteCommand}" />
        <TextBlock Text="{Binding Version}"
                   Margin="10,8,0,0"
                   FontSize="11"
                   Opacity="0.45" />
      </StackPanel>
    </Grid>
    <Grid x:Name="ContentHost" Grid.Column="1" Margin="0,8,8,8">
      <ContentControl x:Name="NativeContent" />
    </Grid>
  </Grid>
</atom:Window>
```

`src/InvoiceFlowAI.App/MainWindow.axaml.cs` 整体替换为:

```csharp
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using AtomUI.Icons.AntDesign;
using InvoiceFlowAI.App.Rpc;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.App.Views;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App;

public partial class MainWindow : AtomUI.Desktop.Controls.Window
{
    private readonly ShellViewModel _shell;
    private readonly AvaloniaMainWindowAccessor _windowAccessor;
    private readonly IRunShutdownService _shutdown;
    private readonly ILogger<MainWindow> _logger;
    private readonly EmbedWebViewHost _embedHost;
    private readonly Dictionary<string, NavMenuNode> _nodes = new(StringComparer.Ordinal);
    private bool _syncingSelection;
    private bool _closeConfirmed;

    public MainWindow(
        ShellViewModel shell,
        AvaloniaMainWindowAccessor windowAccessor,
        IRpcDispatcher rpcDispatcher,
        WebViewRunEventPublisher runEventPublisher,
        IRunShutdownService shutdown,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _windowAccessor = windowAccessor ?? throw new ArgumentNullException(nameof(windowAccessor));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DataContext = shell;
        _windowAccessor.Set(this);
        BuildNavigation();

        _embedHost = new EmbedWebViewHost(rpcDispatcher, runEventPublisher) { IsVisible = false };
        ContentHost.Children.Add(_embedHost);

        NavigationMenu.NavMenuNodeSelected += OnNavigationNodeSelected;
        _shell.PropertyChanged += OnShellPropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void BuildNavigation()
    {
        var icons = new Dictionary<string, PathIcon>(StringComparer.Ordinal)
        {
            ["settings"] = new SettingOutlined(),
            ["processing"] = new ThunderboltOutlined(),
            ["analysis"] = new FundOutlined(),
        };

        var nodes = new List<NavMenuNode>(_shell.Pages.Count);
        foreach (var page in _shell.Pages)
        {
            var node = new NavMenuNode
            {
                Header = page.Title,
                ItemKey = new EntityKey(page.Key),
                Icon = icons.GetValueOrDefault(page.Key),
            };
            _nodes[page.Key] = node;
            nodes.Add(node);
        }

        NavigationMenu.ItemsSource = nodes;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            await _shell.InitializeAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "壳初始化失败。");
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            UpdateContent();
        }
    }

    private async void UpdateContent()
    {
        try
        {
            var page = _shell.CurrentPage;
            var isWeb = page is WebPageViewModel;
            NativeContent.Content = isWeb ? null : page;
            _embedHost.IsVisible = isWeb;
            SyncSelection(page);

            if (page is WebPageViewModel webPage)
            {
                await _embedHost.NavigateAsync(webPage);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "页面切换失败。");
        }
    }

    private void SyncSelection(InvoiceFlowAI.App.Navigation.IPageViewModel? page)
    {
        if (page is null || !_nodes.TryGetValue(page.Key, out var node))
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            NavigationMenu.SelectedItem = node;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private async void OnNavigationNodeSelected(object? sender, NavMenuNodeSelectedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        var key = (e.NavMenuNode as NavMenuNode)?.ItemKey?.Value;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            await _shell.NavigateAsync(key, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "导航失败:{PageKey}", key);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        try
        {
            await _shell.FlushAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "关闭前保存设置失败。");
        }

        try
        {
            var decision = await _shutdown.ConfirmCloseAsync(CancellationToken.None);
            if (decision == RunCloseDecision.Close)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "关闭确认失败。");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _shell.PropertyChanged -= OnShellPropertyChanged;
        NavigationMenu.NavMenuNodeSelected -= OnNavigationNodeSelected;
        _windowAccessor.Set(null);
        _embedHost.Dispose();
    }
}
```

- [ ] **Step 8: 改 App.axaml.cs 与 DI**

`src/InvoiceFlowAI.App/App.axaml.cs` 的 `try` 块内改为:

```csharp
                _serviceProvider = AppServiceProviderFactory.Create();
                desktop.MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                desktop.Exit += (_, _) =>
                {
                    _serviceProvider?.Dispose();
                    _serviceProvider = null;
                };
```

删除不再需要的 `AppRpcComposition.CreateDispatcher` 调用与 `AvaloniaMainWindowAccessor` / `WebViewRunEventPublisher` 解析。

`AppServiceProviderFactory.cs` 在 `services.AddSingleton<IScopedOperationRunner, ScopedOperationRunner>();` 之后追加(`IAppEventBus` / `NativeRunEventPublisher` / `RunEventPublisherFanOut` 与 `IRunEventPublisher` 的注册在 Task 3 已完成,**不要重复注册**):

```csharp
        services.AddSingleton<IRpcDispatcher>(provider => AppRpcComposition.CreateDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0"));
        services.AddSingleton<IRunSession, RunSession>();
        services.AddSingleton<IAppNavigator, AppNavigator>();
        services.AddSingleton<IDialogService, AtomUiDialogService>();
        services.AddSingleton<INotificationService, AtomUiNotificationService>();
        services.AddSingleton<IDisclaimerService, DisclaimerService>();
        services.AddSingleton<IRunShutdownService, RunShutdownService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IPageViewModel>(new WebPageViewModel("settings", "启动配置", "Step 1", "/"));
        services.AddSingleton<IPageViewModel>(new WebPageViewModel("processing", "处理中心", "Step 2", "/processing"));
        services.AddSingleton<IPageViewModel>(new WebPageViewModel("analysis", "结果分析", "Step 3", "/analysis"));
```

补 `using InvoiceFlowAI.App.Events; using InvoiceFlowAI.App.Navigation; using InvoiceFlowAI.App.Services; using InvoiceFlowAI.App.Rpc; using InvoiceFlowAI.App.ViewModels;`。同时删除 `services.AddSingleton<IWindowCommandDispatcher, ...>();`、三行 `AddScoped<Window*RpcHandler>();` 以及 `AppRpcComposition.CreateDispatcher` 在 `App.axaml.cs` 中的旧调用。

- [ ] **Step 9: 删除 window.* RPC**

删除文件 `src/InvoiceFlowAI.App/Rpc/WindowCommandRpcHandler.cs`。

`src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs`:从 `V1Methods` 删除 `"window.minimize"`、`"window.maximize"`、`"window.close"`,并删除三行 `dispatcher.RegisterHandler("window.*", new ScopedRpcHandler<Window*RpcHandler>(...));`。

`src/InvoiceFlowAI.App/Desktop/IDesktopActionService.cs`:删除 `ExecuteWindowCommandAsync` 方法与整个 `IWindowCommandDispatcher` 接口。

`src/InvoiceFlowAI.App/Desktop/AvaloniaDesktopActionService.cs`:删除 `ExecuteWindowCommandAsync` 实现、`AvaloniaWindowCommandDispatcher` 类,以及构造函数中的 `IWindowCommandDispatcher windowCommands` 参数。

- [ ] **Step 10: 更新既有测试**

`tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`:从期望方法列表删除三行 window 方法;把 `hello` 断言里的 `"window.close"` 换成 `"run.stop"`。

`tests/InvoiceFlowAI.App.Tests/Rpc/DesktopActionRpcHandlerTests.cs`:删除 `Window_handler_binds_command_to_registered_method_and_accepts_empty_params` 测试方法,并删除测试内 `FakeDesktopActionService` 的 `WindowCommand` 属性与 `ExecuteWindowCommandAsync` 方法(接口成员已移除)。其余文件夹/文件测试保留到 Task 11。

`tests/InvoiceFlowAI.App.Tests/Desktop/AvaloniaDesktopActionServiceTests.cs`:删除 `Window_commands_are_delegated_to_the_ui_dispatcher` 与 `Avalonia_window_dispatcher_executes_commands_through_ui_thread_dispatcher` 两个测试,并删除 `FakeWindowCommandDispatcher` 与 `FakeAvaloniaUiDispatcher` 两个嵌套类;`CreateService` 去掉 `FakeWindowCommandDispatcher? window = null` 参数与构造调用末尾的 `window ?? new FakeWindowCommandDispatcher()` 实参。

`tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`:删除对 `Window*RpcHandler` 的注册/解析断言(如存在),并补充:

```csharp
        provider.GetRequiredService<IAppNavigator>().Should().BeOfType<AppNavigator>();
        provider.GetRequiredService<IRunSession>().Should().BeOfType<RunSession>();
        provider.GetRequiredService<ShellViewModel>().Pages.Should().HaveCount(3);
        provider.GetServices<IPageViewModel>()
            .Should().Contain(page => page is WebPageViewModel web && web.Key == "settings");
```

- [ ] **Step 11: 运行测试并手动验收 1a**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj -c Debug
```

启动应用,确认:AtomUI 暗色窗口、左侧三个导航项可切换、右侧仍是原 SPA 内容且没有 SPA 自带侧边栏与窗口按钮、关闭窗口时会弹出确认。

- [ ] **Step 12: 提交**

```bash
git add -A src/InvoiceFlowAI.App tests/InvoiceFlowAI.App.Tests/Rpc tests/InvoiceFlowAI.App.Tests/Navigation tests/InvoiceFlowAI.App.Tests/ViewModels
git commit -m "feat(app): replace the WebView window shell with a native AtomUI shell

- atom:Window + NavMenu sidebar with native page switching
- unmigrated pages host the SPA in embed mode through a shared WebView
- window commands are handled natively; window.* RPC handlers are deleted
- add shell navigation, dialog, notification, disclaimer and run-session services

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 6: 阶段 1b — 设置页 ViewModel(数据与命令)

**Files:**
- Create: `src/InvoiceFlowAI.App/ViewModels/SettingsViewModel.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/SettingsCopy.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/SettingsUiStatus.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/TestSupport/TestScopedOperationRunner.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `IScopedOperationRunner`(Task 2)、`IAccountApplicationService` / `IAccountTestService`(Task 2)、`IUserSettingsStore`、`ISecretRetentionStore`、`ISecretStore`、`IPersistentSecretStore`、`IDesktopRunService`、`IDirectoryPicker`、`IRunSession` / `IAppNavigator` / `INotificationService`(Task 5)。
- Produces: `SettingsViewModel : ObservableObject, IPageViewModel`,成员见下:
  - 可绑定属性:`Email`、`AuthCode`、`ApiKey`、`RememberSettings`、`CompanyName`、`OutputDirectory`、`DateFrom`(`DateTime?`)、`DateTo`、`IsBusy`、`PageError`、`EmailStatus`、`EmailStatusMessage`、`ApiStatus`、`ApiStatusMessage`、`OutputDirectoryLocked`、`DateRangeLocked`、`EmailLocked`、`NeedsInitialSetup`。
  - 命令:`TestEmailCommand`、`TestApiKeyCommand`、`ChooseDirectoryCommand`、`StartCommand`。
  - 方法:`LoadAsync(CancellationToken)`、`SaveAsync(CancellationToken)`;`OnActivatedAsync` 调 `LoadAsync`,`OnDeactivatedAsync` 调 `SaveAsync`。
- Produces(测试辅助):`TestScopedOperationRunner.Create(Action<IServiceCollection>)`,后续 Task 8/10 复用。
- 本任务**不注册** `SettingsViewModel`;注册与视图在 Task 7 同一提交内完成,避免出现"有 VM 无视图"的空白页。

- [ ] **Step 1: 写测试辅助与失败的测试**

创建 `tests/InvoiceFlowAI.App.Tests/TestSupport/TestScopedOperationRunner.cs`:

```csharp
using InvoiceFlowAI.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlowAI.App.Tests.TestSupport;

/// <summary>
/// 测试用作用域执行器:用真实容器构建作用域,但里面注册的全是假实现。
/// </summary>
public sealed class TestScopedOperationRunner(IServiceProvider provider) : IScopedOperationRunner
{
    public static TestScopedOperationRunner Create(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return new TestScopedOperationRunner(services.BuildServiceProvider());
    }

    public async Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        return await operation(scope.ServiceProvider, cancellationToken);
    }

    public async Task RunAsync(
        Func<IServiceProvider, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        await operation(scope.ServiceProvider, cancellationToken);
    }
}
```

创建 `tests/InvoiceFlowAI.App.Tests/ViewModels/SettingsViewModelTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Load_applies_locked_output_directory_and_dates()
    {
        var harness = SettingsHarness.Create(new RunContextSnapshot(
            ExplicitRunContext: true,
            ControlledRun: true,
            AutostartEnabled: false,
            AutostartDelayMs: 0,
            RunId: null,
            LockedOutputPath: @"D:\runs\locked",
            LockedDateFrom: new DateOnly(2026, 9, 1),
            LockedDateTo: new DateOnly(2026, 9, 30),
            LockedEmail: "locked@163.com",
            AccountId: null));
        var viewModel = harness.CreateViewModel();

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        viewModel.OutputDirectory.Should().Be(@"D:\runs\locked");
        viewModel.OutputDirectoryLocked.Should().BeTrue();
        viewModel.DateRangeLocked.Should().BeTrue();
        viewModel.EmailLocked.Should().BeTrue();
        viewModel.Email.Should().Be("locked@163.com");
        viewModel.ChooseDirectoryCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Save_persists_secrets_account_and_profile_settings()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@163.com";
        viewModel.AuthCode = "auth-123";
        viewModel.ApiKey = "sk-123";
        viewModel.CompanyName = "示例科技";
        viewModel.OutputDirectory = @"D:\out";

        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        harness.Secrets.Requests.Should().Contain(r =>
            r.Name == "mail.imap.auth-code"
            && r.Value == "auth-123"
            && r.Retention == SecretRetention.Persistent);
        harness.Secrets.Requests.Should().Contain(r =>
            r.Name == "deepseek.api-key" && r.Value == "sk-123");
        harness.Accounts.Saved.Should().ContainSingle();
        harness.Accounts.Saved[0].Account.EmailAddress.Should().Be("alice@163.com");
        harness.Accounts.Saved[0].Account.ImapHost.Should().Be("imap.163.com");
        harness.Accounts.Saved[0].Account.ImapPort.Should().Be(993);
        harness.Accounts.Saved[0].ExpectedRevision.Should().Be(0);
        harness.Settings.Updates.Should().ContainSingle();
        harness.Settings.Updates[0].CompanyName.Should().Be("示例科技");
        harness.Settings.Updates[0].LastOutputDirectory.Should().Be(@"D:\out");
        viewModel.AuthCode.Should().BeEmpty();
        viewModel.ApiKey.Should().BeEmpty();
    }

    [Fact]
    public async Task Turning_off_remember_deletes_persisted_secrets_and_clears_profile()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        viewModel.RememberSettings = false;
        await viewModel.SaveAsync(TestContext.Current.CancellationToken);

        harness.SecretsStore.Deleted.Should().BeEquivalentTo("mail.imap.auth-code", "deepseek.api-key");
        harness.Settings.Updates.Should().ContainSingle();
        harness.Settings.Updates[0].CompanyName.Should().BeEmpty();
        harness.Settings.Updates[0].LastOutputDirectory.Should().BeNull();
    }

    [Fact]
    public async Task Test_email_rejects_unsupported_domain_without_calling_the_service()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.Email = "alice@gmail.com";

        await viewModel.TestEmailCommand.ExecuteAsync(null);

        viewModel.EmailStatus.Should().Be(SettingsUiStatus.Error);
        viewModel.EmailStatusMessage.Should().Be("当前支持 QQ 邮箱和 163 邮箱。");
        harness.Tests.MailboxCalls.Should().Be(0);
    }

    [Fact]
    public async Task Test_email_maps_failure_code_to_product_copy()
    {
        var harness = SettingsHarness.Create();
        harness.Tests.MailboxResult = new AccountTestResult("default-mailbox", false, "INBOX", "Mailbox login failed.", "IMAP_LOGIN_FAILED");
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        await viewModel.TestEmailCommand.ExecuteAsync(null);

        viewModel.EmailStatus.Should().Be(SettingsUiStatus.Error);
        viewModel.EmailStatusMessage.Should().Be("邮箱授权码无效或已过期,请重新生成后重试。");
    }

    [Fact]
    public async Task Start_requires_company_and_output_directory()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.CompanyName = "";
        viewModel.OutputDirectory = "";
        viewModel.DateFrom = DateTime.Today.AddDays(-7);
        viewModel.DateTo = DateTime.Today;

        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.PageError.Should().Be("请填写公司名称。");
        harness.Runs.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_starts_the_run_and_navigates_to_processing()
    {
        var harness = SettingsHarness.Create();
        var viewModel = harness.CreateViewModel();
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        viewModel.CompanyName = "示例科技";
        viewModel.OutputDirectory = @"D:\out";
        viewModel.DateFrom = new DateTime(2026, 9, 1);
        viewModel.DateTo = new DateTime(2026, 9, 30);

        await viewModel.StartCommand.ExecuteAsync(null);

        harness.Runs.Started.Should().ContainSingle();
        harness.Runs.Started[0].CompanyName.Should().Be("示例科技");
        harness.Runs.Started[0].DateFrom.Should().Be(new DateOnly(2026, 9, 1));
        harness.Runs.Started[0].DateTo.Should().Be(new DateOnly(2026, 9, 30));
        harness.Session.RunId.Should().Be(harness.Runs.Started[0].RunId);
        harness.Navigator.Requested.Should().Equal("processing");
    }

    private sealed class SettingsHarness
    {
        private SettingsHarness(RunContextSnapshot context)
        {
            Context = context;
            Accounts = new FakeAccountApplicationService();
            Tests = new FakeAccountTestService();
            Settings = new FakeUserSettingsStore();
            Secrets = new FakeSecretRetentionStore();
            SecretsStore = new FakeSecretStore();
            Picker = new FakeDirectoryPicker();
            Runs = new FakeDesktopRunService(context);
            Session = new FakeRunSession();
            Navigator = new RecordingNavigator();
        }

        public RunContextSnapshot Context { get; }
        public FakeAccountApplicationService Accounts { get; }
        public FakeAccountTestService Tests { get; }
        public FakeUserSettingsStore Settings { get; }
        public FakeSecretRetentionStore Secrets { get; }
        public FakeSecretStore SecretsStore { get; }
        public FakeDirectoryPicker Picker { get; }
        public FakeDesktopRunService Runs { get; }
        public FakeRunSession Session { get; }
        public RecordingNavigator Navigator { get; }

        public static SettingsHarness Create(RunContextSnapshot? context = null) => new(context ?? new RunContextSnapshot(
            ExplicitRunContext: false,
            ControlledRun: false,
            AutostartEnabled: false,
            AutostartDelayMs: 0,
            RunId: null,
            LockedOutputPath: null,
            LockedDateFrom: null,
            LockedDateTo: null,
            LockedEmail: null,
            AccountId: null));

        public SettingsViewModel CreateViewModel()
        {
            var operations = TestScopedOperationRunner.Create(services =>
            {
                services.AddSingleton<IAccountApplicationService>(Accounts);
                services.AddSingleton<IAccountTestService>(Tests);
                services.AddSingleton<IUserSettingsStore>(Settings);
                services.AddSingleton<ISecretRetentionStore>(Secrets);
                services.AddSingleton<ISecretStore>(SecretsStore);
                services.AddSingleton<IDesktopRunService>(Runs);
            });

            return new SettingsViewModel(
                operations,
                Picker,
                Session,
                Navigator,
                new NullNotificationService(),
                NullLogger<SettingsViewModel>.Instance);
        }
    }

    private sealed class FakeAccountApplicationService : IAccountApplicationService
    {
        public List<AccountSaveRequest> Saved { get; } = [];
        public MailboxAccountSnapshot? Current { get; private set; }

        public Task<AccountListResult> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AccountListResult(Current is null ? [] : [Current]));

        public Task<MailboxAccountSnapshot> SaveAsync(AccountSaveRequest request, CancellationToken cancellationToken)
        {
            Saved.Add(request);
            Current = new MailboxAccountSnapshot(
                request.Account.AccountId,
                request.Account.EmailAddress,
                request.Account.ImapHost,
                request.Account.ImapPort,
                request.Account.UseTls,
                request.Account.CredentialName,
                request.Account.DisplayName,
                1,
                true,
                "al***@163.com",
                DateTimeOffset.UtcNow,
                request.Account.DefaultMailbox);
            return Task.FromResult(Current);
        }
    }

    private sealed class FakeAccountTestService : IAccountTestService
    {
        public int MailboxCalls { get; private set; }
        public AccountTestResult MailboxResult { get; set; } = new("default-mailbox", true, "INBOX", "");

        public Task<AccountTestResult> TestMailboxAsync(AccountTestRequest request, CancellationToken cancellationToken)
        {
            MailboxCalls++;
            return Task.FromResult(MailboxResult);
        }

        public Task<ProviderTestResult> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ProviderTestResult(request.ProviderId, true));
    }

    private sealed class FakeUserSettingsStore : IUserSettingsStore
    {
        public List<SettingsUpdateRequest> Updates { get; } = [];

        public Task<UserSettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Create(0, "", null));

        public Task<UserSettingsSnapshot> UpdateAsync(SettingsUpdateRequest request, CancellationToken cancellationToken)
        {
            Updates.Add(request);
            return Task.FromResult(Create(
                request.ExpectedRevision + 1,
                request.CompanyName ?? "",
                request.LastOutputDirectory));
        }

        private static UserSettingsSnapshot Create(int revision, string companyName, string? outputDirectory)
            => new(
                revision,
                "default-mailbox",
                "INBOX",
                new MailboxFilterRules(),
                new PipelineOptionsPatch(),
                true,
                "default",
                1,
                "fingerprint",
                DateTimeOffset.UtcNow,
                companyName,
                outputDirectory);
    }

    private sealed class FakeSecretRetentionStore : ISecretRetentionStore
    {
        public List<SecretSetRequest> Requests { get; } = [];

        public Task<SecretMutationResult> SetAsync(SecretSetRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new SecretMutationResult(
                request.Name,
                true,
                request.Retention == SecretRetention.Persistent));
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public List<string> Deleted { get; } = [];

        public Task SaveAsync(string name, string value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectoryPicker : IDirectoryPicker
    {
        public Task<DirectoryChooseResult> ChooseAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DirectoryChooseResult(false, @"D:\picked"));
    }

    private sealed class FakeDesktopRunService(RunContextSnapshot context) : IDesktopRunService
    {
        public List<RunStartRequest> Started { get; } = [];

        public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
            => Task.FromResult(context);

        public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
        {
            Started.Add(request);
            return Task.FromResult(new RunStartResult(true, request.RunId, null));
        }

        public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
```

其余假实现:

```csharp
    public sealed class FakeRunSession : IRunSession
    {
        public string? RunId { get; private set; }
        public void SetRunId(string? runId) => RunId = runId;
    }

    private sealed class RecordingNavigator : IAppNavigator
    {
        public List<string> Requested { get; } = [];
        public Task NavigateAsync(string key, CancellationToken cancellationToken = default)
        {
            Requested.Add(key);
            return Task.CompletedTask;
        }
    }

    private sealed class NullNotificationService : INotificationService
    {
        public void Show(string message, NotificationKind kind) { }
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"
```

Expected: 编译失败,`SettingsViewModel`、`SettingsUiStatus`、`SettingsCopy` 不存在。

- [ ] **Step 3: 写产品文案常量**

创建 `src/InvoiceFlowAI.App/ViewModels/SettingsUiStatus.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

public enum SettingsUiStatus
{
    Idle,
    Testing,
    Success,
    Error,
}
```

创建 `src/InvoiceFlowAI.App/ViewModels/SettingsCopy.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

/// <summary>
/// 设置页全部用户可见文案。文案只表达产品意图与操作结果,
/// 不出现实现说明、错误码或异常细节。
/// </summary>
public static class SettingsCopy
{
    public const string Title = "启动配置";
    public const string Eyebrow = "Step 1";
    public const string ControlledNotice = "受控复跑已锁定输出目录与日期窗口。";
    public const string EmailRequired = "请输入邮箱地址。";
    public const string EmailUnsupported = "当前支持 QQ 邮箱和 163 邮箱。";
    public const string CompanyRequired = "请填写公司名称。";
    public const string DirectoryRequired = "请选择输出目录。";
    public const string DateRequired = "请选择开始日期和结束日期。";
    public const string DateOrderInvalid = "开始日期不能晚于结束日期。";
    public const string AccountMissing = "请先填写有效邮箱地址。";
    public const string TestingEmail = "正在测试邮箱授权码...";
    public const string TestingApiKey = "正在测试 API Key...";
    public const string EmailTestPassed = "邮箱授权验证通过。";
    public const string EmailTestFailed = "邮箱授权验证失败,请检查邮箱地址与授权码。";
    public const string ApiTestPassed = "API Key 验证通过。";
    public const string ApiTestFailed = "API Key 验证失败,请检查 Key 是否正确。";
    public const string SaveFailed = "配置暂未保存,请稍后重试。";
    public const string StartFailed = "任务启动失败,请稍后重试。";
    public const string RunAlreadyActive = "已有任务正在运行。";
    public const string RunSnapshotMissing = "邮箱账户配置已变化,请重新加载设置。";
    public const string RunParamsInvalid = "任务参数无效,请检查设置。";
    public const string DirectoryPickFailed = "选择目录失败,请稍后重试。";
    public const string StopAccepted = "已收到安全停止指令。";

    public static string MapEmailFailure(string? failureCode) => failureCode switch
    {
        "IMAP_LOGIN_FAILED" => "邮箱授权码无效或已过期,请重新生成后重试。",
        "IMAP_CONNECTION_FAILED" => "无法连接邮箱服务器,请检查网络后重试。",
        "IMAP_PROTOCOL_FAILED" => "邮箱服务器响应异常,请稍后重试。",
        _ => EmailTestFailed,
    };

    public static string MapApiFailure(string? failureCode) => failureCode switch
    {
        "PROVIDER_AUTH_FAILED" => "API Key 被服务端拒绝,请确认 Key 是否正确。",
        _ => ApiTestFailed,
    };

    public static string MapStartRejection(string? rejectionCode) => rejectionCode switch
    {
        "RUN_ALREADY_ACTIVE" => RunAlreadyActive,
        "RUN_CONFIGURATION_SNAPSHOT_MISSING" => RunSnapshotMissing,
        "RUN_INPUT_INVALID" or "RPC_INVALID_PARAMS" => RunParamsInvalid,
        _ => StartFailed,
    };
}
```

- [ ] **Step 4: 实现 SettingsViewModel**

创建 `src/InvoiceFlowAI.App/ViewModels/SettingsViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.Application.Accounts;
using InvoiceFlowAI.Application.Persistence;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Application.Security;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Settings;
using InvoiceFlowAI.Contracts.Accounts;
using InvoiceFlowAI.Contracts.Rpc;
using InvoiceFlowAI.Contracts.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IPageViewModel
{
    private const string AuthCodeSecretName = "mail.imap.auth-code";
    private const string ApiKeySecretName = "deepseek.api-key";
    private const string DefaultAccountId = "default-mailbox";
    private const string DefaultMailbox = "INBOX";

    private readonly IScopedOperationRunner _operations;
    private readonly IDirectoryPicker _directoryPicker;
    private readonly IRunSession _runSession;
    private readonly IAppNavigator _navigator;
    private readonly INotificationService _notifications;
    private readonly ILogger<SettingsViewModel> _logger;

    private UserSettingsSnapshot? _snapshot;
    private MailboxAccountSnapshot? _account;
    private bool _rememberAtLoad = true;
    private bool _loaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestEmailCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _authCode = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _rememberSettings = true;

    [ObservableProperty]
    private string _companyName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DateTime? _dateFrom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DateTime? _dateTo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseDirectoryCommand))]
    private bool _outputDirectoryLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _dateRangeLocked;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestEmailCommand))]
    private bool _emailLocked;

    [ObservableProperty]
    private string _pageError = string.Empty;

    [ObservableProperty]
    private SettingsUiStatus _emailStatus;

    [ObservableProperty]
    private string _emailStatusMessage = string.Empty;

    [ObservableProperty]
    private SettingsUiStatus _apiStatus;

    [ObservableProperty]
    private string _apiStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _needsInitialSetup;

    public SettingsViewModel(
        IScopedOperationRunner operations,
        IDirectoryPicker directoryPicker,
        IRunSession runSession,
        IAppNavigator navigator,
        INotificationService notifications,
        IPersistentSecretStore persistentSecrets,
        ILogger<SettingsViewModel> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _directoryPicker = directoryPicker ?? throw new ArgumentNullException(nameof(directoryPicker));
        _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Key => "settings";

    public string Title => SettingsCopy.Title;

    public string Eyebrow => SettingsCopy.Eyebrow;

    public bool HasPageError => !string.IsNullOrWhiteSpace(PageError);

    public bool HasEmailStatus => EmailStatus != SettingsUiStatus.Idle;

    public bool HasApiStatus => ApiStatus != SettingsUiStatus.Idle;

    public bool IsControlledRun => OutputDirectoryLocked || DateRangeLocked || EmailLocked;

    partial void OnPageErrorChanged(string value) => OnPropertyChanged(nameof(HasPageError));

    partial void OnEmailStatusChanged(SettingsUiStatus value) => OnPropertyChanged(nameof(HasEmailStatus));

    partial void OnApiStatusChanged(SettingsUiStatus value) => OnPropertyChanged(nameof(HasApiStatus));

    partial void OnOutputDirectoryLockedChanged(bool value) => OnPropertyChanged(nameof(IsControlledRun));

    partial void OnDateRangeLockedChanged(bool value) => OnPropertyChanged(nameof(IsControlledRun));

    partial void OnEmailLockedChanged(bool value) => OnPropertyChanged(nameof(IsControlledRun));

    public Task OnActivatedAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public Task OnDeactivatedAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            var state = await _operations.RunAsync(
                async (provider, token) =>
                {
                    var settingsStore = provider.GetRequiredService<IUserSettingsStore>();
                    var accounts = provider.GetRequiredService<IAccountApplicationService>();
                    var runs = provider.GetRequiredService<IDesktopRunService>();

                    var snapshot = await settingsStore.LoadAsync(token).ConfigureAwait(false);
                    var accountList = await accounts.ListAsync(token).ConfigureAwait(false);
                    var context = await runs.GetContextAsync(token).ConfigureAwait(false);
                    return new LoadedState(snapshot, accountList.Items.FirstOrDefault(), context);
                },
                cancellationToken).ConfigureAwait(true);

            _snapshot = state.Snapshot;
            _account = state.Account;

            EmailLocked = !string.IsNullOrWhiteSpace(state.Context.LockedEmail);
            OutputDirectoryLocked = !string.IsNullOrWhiteSpace(state.Context.LockedOutputPath);
            DateRangeLocked = state.Context.LockedDateFrom is not null || state.Context.LockedDateTo is not null;

            Email = state.Context.LockedEmail ?? state.Account?.EmailAddress ?? string.Empty;
            CompanyName = state.Snapshot.CompanyName;
            OutputDirectory = state.Context.LockedOutputPath ?? state.Snapshot.LastOutputDirectory ?? string.Empty;

            var today = DateTime.Today;
            DateFrom = ToDateTime(state.Context.LockedDateFrom) ?? today.AddDays(-29);
            DateTo = ToDateTime(state.Context.LockedDateTo) ?? today;

            NeedsInitialSetup = string.IsNullOrWhiteSpace(CompanyName)
                || string.IsNullOrWhiteSpace(OutputDirectory)
                || state.Account is null;

            _loaded = true;
            PageError = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载设置失败。");
            PageError = SettingsCopy.SaveFailed;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!_loaded || IsBusy)
        {
            return;
        }

        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);
            PageError = string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "保存设置失败。");
            PageError = SettingsCopy.SaveFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestEmail))]
    private async Task TestEmailAsync(CancellationToken cancellationToken)
    {
        var error = ValidateEmail(Email);
        if (error is not null)
        {
            EmailStatus = SettingsUiStatus.Error;
            EmailStatusMessage = error;
            return;
        }

        EmailStatus = SettingsUiStatus.Testing;
        EmailStatusMessage = SettingsCopy.TestingEmail;
        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);
            var accountId = _account?.AccountId;
            if (accountId is null)
            {
                EmailStatus = SettingsUiStatus.Error;
                EmailStatusMessage = SettingsCopy.AccountMissing;
                return;
            }

            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IAccountTestService>()
                    .TestMailboxAsync(new AccountTestRequest(accountId), token),
                cancellationToken).ConfigureAwait(true);

            EmailStatus = result.Succeeded ? SettingsUiStatus.Success : SettingsUiStatus.Error;
            EmailStatusMessage = result.Succeeded
                ? SettingsCopy.EmailTestPassed
                : SettingsCopy.MapEmailFailure(result.FailureCode);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "测试邮箱授权码失败。");
            EmailStatus = SettingsUiStatus.Error;
            EmailStatusMessage = SettingsCopy.EmailTestFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestApiKey))]
    private async Task TestApiKeyAsync(CancellationToken cancellationToken)
    {
        ApiStatus = SettingsUiStatus.Testing;
        ApiStatusMessage = SettingsCopy.TestingApiKey;
        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await SaveSecretAsync(ApiKeySecretName, ApiKey, cancellationToken).ConfigureAwait(true);
                ApiKey = string.Empty;
            }

            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IAccountTestService>()
                    .TestProviderAsync(new ProviderTestRequest("deepseek", ApiKeySecretName), token),
                cancellationToken).ConfigureAwait(true);

            ApiStatus = result.Succeeded ? SettingsUiStatus.Success : SettingsUiStatus.Error;
            ApiStatusMessage = result.Succeeded
                ? SettingsCopy.ApiTestPassed
                : SettingsCopy.MapApiFailure(result.FailureCode);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "测试 API Key 失败。");
            ApiStatus = SettingsUiStatus.Error;
            ApiStatusMessage = SettingsCopy.ApiTestFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChooseDirectory))]
    private async Task ChooseDirectoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _directoryPicker.ChooseAsync(cancellationToken).ConfigureAwait(true);
            if (!result.Cancelled && !string.IsNullOrWhiteSpace(result.Path))
            {
                OutputDirectory = result.Path;
                PageError = string.Empty;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "选择输出目录失败。");
            PageError = SettingsCopy.DirectoryPickFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var error = ValidateForStart();
        if (error is not null)
        {
            PageError = error;
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            await PersistAsync(cancellationToken).ConfigureAwait(true);

            var accountId = _account?.AccountId;
            if (accountId is null)
            {
                PageError = SettingsCopy.AccountMissing;
                return;
            }

            var request = new RunStartRequest(
                $"run-{Guid.NewGuid():N}",
                accountId,
                DateOnly.FromDateTime(DateFrom!.Value.Date),
                DateOnly.FromDateTime(DateTo!.Value.Date),
                OutputDirectory,
                CompanyName.Trim(),
                "full");

            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                    .StartAsync(request, token),
                cancellationToken).ConfigureAwait(true);

            if (!result.Accepted)
            {
                PageError = SettingsCopy.MapStartRejection(result.RejectionCode);
                return;
            }

            _runSession.SetRunId(result.RunId);
            await _navigator.NavigateAsync("processing", cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "启动任务失败。");
            PageError = SettingsCopy.StartFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanTestEmail() => _loaded && !IsBusy && !EmailLocked;

    private bool CanTestApiKey() => _loaded && !IsBusy;

    private bool CanChooseDirectory() => _loaded && !IsBusy && !OutputDirectoryLocked;

    private bool CanStart() => _loaded && !IsBusy;

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot ?? throw new InvalidOperationException("Settings must be loaded before saving.");
        var email = Email.Trim();
        var remember = RememberSettings;
        var companyName = remember ? CompanyName.Trim() : string.Empty;
        var outputDirectory = remember ? NullIfBlank(OutputDirectory) : null;

        var state = await _operations.RunAsync(
            async (provider, token) =>
            {
                var settingsStore = provider.GetRequiredService<IUserSettingsStore>();
                var secretRetention = provider.GetRequiredService<ISecretRetentionStore>();
                var secretStore = provider.GetRequiredService<ISecretStore>();
                var accounts = provider.GetRequiredService<IAccountApplicationService>();

                var retention = remember ? SecretRetention.Persistent : SecretRetention.Session;
                if (!string.IsNullOrWhiteSpace(AuthCode))
                {
                    await secretRetention.SetAsync(
                        new SecretSetRequest(AuthCodeSecretName, AuthCode.Trim(), retention), token).ConfigureAwait(false);
                    AuthCode = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(ApiKey))
                {
                    await secretRetention.SetAsync(
                        new SecretSetRequest(ApiKeySecretName, ApiKey.Trim(), retention), token).ConfigureAwait(false);
                    ApiKey = string.Empty;
                }

                if (_rememberAtLoad && !remember)
                {
                    await secretStore.DeleteAsync(AuthCodeSecretName, token).ConfigureAwait(false);
                    await secretStore.DeleteAsync(ApiKeySecretName, token).ConfigureAwait(false);
                }

                _rememberAtLoad = remember;

                var savedAccount = _account;
                if (email.Length > 0)
                {
                    var draft = new MailboxAccountDraft(
                        _account?.AccountId ?? DefaultAccountId,
                        email,
                        email.EndsWith("@163.com", StringComparison.OrdinalIgnoreCase) ? "imap.163.com" : "imap.qq.com",
                        993,
                        true,
                        AuthCodeSecretName,
                        email,
                        DefaultMailbox);

                    if (!Matches(_account, draft))
                    {
                        savedAccount = await accounts.SaveAsync(
                            new AccountSaveRequest(draft, _account?.Revision ?? 0), token).ConfigureAwait(false);
                    }
                }

                var accountId = savedAccount?.AccountId ?? snapshot.CurrentAccountId;
                if (!string.Equals(snapshot.CompanyName, companyName, StringComparison.Ordinal)
                    || !string.Equals(snapshot.LastOutputDirectory, outputDirectory, StringComparison.Ordinal)
                    || !string.Equals(snapshot.CurrentAccountId, accountId, StringComparison.Ordinal))
                {
                    snapshot = await settingsStore.UpdateAsync(
                        new SettingsUpdateRequest(
                            snapshot.Revision,
                            AccountId: accountId,
                            CompanyName: companyName,
                            LastOutputDirectory: outputDirectory),
                        token).ConfigureAwait(false);
                }

                return new PersistedState(savedAccount, snapshot);
            },
            cancellationToken).ConfigureAwait(true);

        _account = state.Account;
        _snapshot = state.Snapshot;
        Email = _account?.EmailAddress ?? Email;
        NeedsInitialSetup = string.IsNullOrWhiteSpace(_snapshot.CompanyName)
            || string.IsNullOrWhiteSpace(_snapshot.LastOutputDirectory)
            || _account is null;
    }

    private async Task SaveSecretAsync(string name, string value, CancellationToken cancellationToken)
    {
        var retention = RememberSettings ? SecretRetention.Persistent : SecretRetention.Session;
        await _operations.RunAsync(
            (provider, token) => provider.GetRequiredService<ISecretRetentionStore>()
                .SetAsync(new SecretSetRequest(name, value.Trim(), retention), token),
            cancellationToken).ConfigureAwait(true);
    }

    private string? ValidateForStart()
    {
        var emailError = ValidateEmail(Email);
        if (emailError is not null)
        {
            return emailError;
        }

        if (string.IsNullOrWhiteSpace(CompanyName))
        {
            return SettingsCopy.CompanyRequired;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return SettingsCopy.DirectoryRequired;
        }

        if (DateFrom is null || DateTo is null)
        {
            return SettingsCopy.DateRequired;
        }

        return DateFrom.Value.Date > DateTo.Value.Date ? SettingsCopy.DateOrderInvalid : null;
    }

    private static string? ValidateEmail(string email)
    {
        var value = email.Trim();
        if (value.Length == 0)
        {
            return SettingsCopy.EmailRequired;
        }

        return value.EndsWith("@qq.com", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("@163.com", StringComparison.OrdinalIgnoreCase)
            ? null
            : SettingsCopy.EmailUnsupported;
    }

    private static bool Matches(MailboxAccountSnapshot? account, MailboxAccountDraft draft)
        => account is not null
            && string.Equals(account.AccountId, draft.AccountId, StringComparison.Ordinal)
            && string.Equals(account.EmailAddress, draft.EmailAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(account.ImapHost, draft.ImapHost, StringComparison.Ordinal)
            && account.ImapPort == draft.ImapPort
            && account.UseTls == draft.UseTls
            && string.Equals(account.CredentialName, draft.CredentialName, StringComparison.Ordinal)
            && string.Equals(account.DisplayName, draft.DisplayName, StringComparison.Ordinal)
            && string.Equals(account.DefaultMailbox, draft.DefaultMailbox, StringComparison.Ordinal);

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);

    private sealed record LoadedState(
        UserSettingsSnapshot Snapshot,
        MailboxAccountSnapshot? Account,
        RunContextSnapshot Context);

    private sealed record PersistedState(MailboxAccountSnapshot? Account, UserSettingsSnapshot Snapshot);
}
```

- [ ] **Step 5: 运行测试**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests"
```

Expected: PASS(7 个用例)。

- [ ] **Step 6: 提交**

```bash
git add src/InvoiceFlowAI.App/ViewModels tests/InvoiceFlowAI.App.Tests/TestSupport tests/InvoiceFlowAI.App.Tests/ViewModels
git commit -m "feat(settings): add the native settings view model

- load settings, account and controlled-run context into one editable state
- persist secrets through ISecretRetentionStore and delete them when remember is off
- validate QQ/163 mailbox addresses and map failure codes to product copy
- start a run, store the run id in IRunSession and navigate to processing

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 7: 阶段 1b — 设置页视图与 RPC 退役

**Files:**
- Create: `src/InvoiceFlowAI.App/Converters/StatusToAlertTypeConverter.cs`
- Create: `src/InvoiceFlowAI.App/Views/SettingsPageView.axaml`
- Create: `src/InvoiceFlowAI.App/Views/SettingsPageView.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/App.axaml`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs:10-18,46-55`
- Delete: `src/InvoiceFlowAI.App/Rpc/AccountListRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/AccountSaveRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/AccountTestRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/ProviderTestRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/SettingsGetRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/SettingsUpdateRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/SecretSetRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/SecretDeleteRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/DirectoryChooseRpcHandler.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/SettingsRpcHandlerTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/SettingsSnapshotRpcHandlerTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/DirectoryChooseRpcHandlerTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/EndToEnd/SettingsSliceWebViewTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/EndToEnd/NativeWebViewSettingsIntegrationTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Views/SettingsPageViewTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`

**Interfaces:**
- Consumes: `SettingsViewModel`(Task 6)、`AtomUiHeadlessHost`(Task 1)。
- Produces: `SettingsPageView` 通过 `App.axaml` 的 `DataTemplate` 自动匹配 `SettingsViewModel`;`ShellViewModel` 的 `NativeContent` 直接放 VM 即可渲染。
- 退役护栏说明(设计 §7.3 的落地方式):过渡期 SPA 仍保留未挂载页面的 JS 调用点,无法用"剩余 handler == JS 调用点"的脚本做严格比对;本计划改用编译期符号检查 + `AppRpcCompositionTests` 的注册方法断言 + 每任务手工验收共同兜底,Task 12 删除 SPA 后该风险自然消失。
- 说明:`templates/settings_rpc.js` 与 `settings_page_rpc.test.mjs` **不在本任务删除**。SPA 只在 `SettingsPage` 挂载时调用 settings/account RPC,而嵌入模式只会加载 processing/analysis 两个视图;SPA 整体在 Task 12 一次性删除,避免半途改 bundle。

- [ ] **Step 1: 写失败的视图冒烟测试**

创建 `tests/InvoiceFlowAI.App.Tests/Views/SettingsPageViewTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.Views;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class SettingsPageViewTests
{
    [Fact]
    public void Settings_page_view_builds_its_control_tree()
    {
        var cardCount = AtomUiHeadlessHost.Run(() =>
        {
            var view = new SettingsPageView();
            view.Measure(new Avalonia.Size(1180, 760));
            return view.GetVisualDescendants().OfType<Card>().Count();
        });

        cardCount.Should().BeGreaterThanOrEqualTo(4);
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~SettingsPageViewTests"
```

Expected: 编译失败,`SettingsPageView` 不存在。

- [ ] **Step 3: 实现状态到 Alert 类型的转换器**

创建 `src/InvoiceFlowAI.App/Converters/StatusToAlertTypeConverter.cs`:

```csharp
using System.Globalization;
using Avalonia.Data.Converters;
using AtomUI.Desktop.Controls;
using InvoiceFlowAI.App.ViewModels;

namespace InvoiceFlowAI.App.Converters;

public sealed class StatusToAlertTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SettingsUiStatus status
            ? status switch
            {
                SettingsUiStatus.Success => AlertType.Success,
                SettingsUiStatus.Error => AlertType.Error,
                SettingsUiStatus.Testing => AlertType.Info,
                _ => AlertType.Info,
            }
            : AlertType.Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 4: 写设置页视图**

创建 `src/InvoiceFlowAI.App/Views/SettingsPageView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:atom="https://atomui.net"
             xmlns:vm="using:InvoiceFlowAI.App.ViewModels"
             xmlns:conv="using:InvoiceFlowAI.App.Converters"
             x:Class="InvoiceFlowAI.App.Views.SettingsPageView"
             x:DataType="vm:SettingsViewModel">
  <UserControl.Resources>
    <conv:StatusToAlertTypeConverter x:Key="StatusToAlertType" />
  </UserControl.Resources>

  <ScrollViewer>
    <StackPanel Margin="28,20,28,28" Spacing="18">
      <StackPanel Spacing="6">
        <TextBlock Text="{Binding Eyebrow}" FontSize="12" Opacity="0.55" />
        <TextBlock Text="{Binding Title}" FontSize="26" FontWeight="SemiBold" />
      </StackPanel>

      <atom:Alert Type="Error"
                  IsShowIcon="True"
                  IsClosable="False"
                  Message="{Binding PageError}"
                  IsVisible="{Binding HasPageError}" />

      <atom:Alert Type="Information"
                  IsShowIcon="True"
                  IsClosable="False"
                  Message="受控复跑已锁定输出目录与日期窗口。"
                  IsVisible="{Binding IsControlledRun}" />

      <Grid ColumnDefinitions="*,*" ColumnSpacing="16">
        <StackPanel Grid.Column="0" Spacing="16">
          <atom:Card Header="邮箱源配置" StyleVariant="Outlined">
            <StackPanel Spacing="10">
              <TextBlock Text="邮箱地址" FontWeight="SemiBold" />
              <atom:LineEdit Text="{Binding Email, Mode=TwoWay}"
                             Watermark="name@qq.com"
                             IsEnabled="{Binding !EmailLocked}" />
              <TextBlock Text="当前支持 QQ 邮箱与 163 邮箱,域名会决定 IMAP 通道。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap" />

              <TextBlock Text="授权码" FontWeight="SemiBold" Margin="0,6,0,0" />
              <atom:LineEdit Text="{Binding AuthCode, Mode=TwoWay}"
                             Watermark="请输入邮箱授权码"
                             PasswordChar="●"
                             IsEnableRevealButton="True" />

              <atom:Button ButtonType="Default"
                           Content="测试邮箱授权码"
                           HorizontalAlignment="Left"
                           Command="{Binding TestEmailCommand}" />

              <atom:Alert Type="{Binding EmailStatus, Converter={StaticResource StatusToAlertType}}"
                          IsShowIcon="True"
                          IsClosable="False"
                          Message="{Binding EmailStatusMessage}"
                          IsVisible="{Binding HasEmailStatus}" />
            </StackPanel>
          </atom:Card>

          <atom:Card Header="输出目录设置" StyleVariant="Outlined">
            <StackPanel Spacing="10">
              <atom:LineEdit Text="{Binding OutputDirectory, Mode=TwoWay}"
                             Watermark="请选择本地输出目录"
                             IsReadOnly="True" />
              <atom:Button ButtonType="Default"
                           Content="浏览目录"
                           HorizontalAlignment="Left"
                           Command="{Binding ChooseDirectoryCommand}" />
              <TextBlock Text="系统会在此目录下继续按发票类型落盘,并生成待人工复核目录。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap"
                         IsVisible="{Binding !OutputDirectoryLocked}" />
              <TextBlock Text="当前为受控复跑,本轮输出目录由诊断上下文锁定。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap"
                         IsVisible="{Binding OutputDirectoryLocked}" />
            </StackPanel>
          </atom:Card>
        </StackPanel>

        <StackPanel Grid.Column="1" Spacing="16">
          <atom:Card Header="智能处理引擎" StyleVariant="Outlined">
            <StackPanel Spacing="10">
              <Grid ColumnDefinitions="*,Auto">
                <TextBlock Grid.Column="0" Text="DeepSeek API Key" FontWeight="SemiBold" />
                <atom:Button Grid.Column="1"
                             ButtonType="Link"
                             Content="获取 API Key"
                             Click="OnOpenApiKeysClick" />
              </Grid>
              <atom:LineEdit Text="{Binding ApiKey, Mode=TwoWay}"
                             Watermark="sk-..."
                             PasswordChar="●"
                             IsEnableRevealButton="True" />
              <atom:Button ButtonType="Default"
                           Content="测试 API Key"
                           HorizontalAlignment="Left"
                           Command="{Binding TestApiKeyCommand}" />
              <TextBlock Text="额度不足时会直接提示,处理页也会同步显示配额相关警告。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap"
                         IsVisible="{Binding !HasApiStatus}" />
              <atom:Alert Type="{Binding ApiStatus, Converter={StaticResource StatusToAlertType}}"
                          IsShowIcon="True"
                          IsClosable="False"
                          Message="{Binding ApiStatusMessage}"
                          IsVisible="{Binding HasApiStatus}" />
            </StackPanel>
          </atom:Card>

          <atom:Card Header="提取时间范围" StyleVariant="Outlined">
            <StackPanel Spacing="10">
              <StackPanel Orientation="Horizontal" Spacing="10">
                <atom:DatePicker SelectedDateTime="{Binding DateFrom, Mode=TwoWay}"
                                 Format="yyyy-MM-dd"
                                 IsEnabled="{Binding !DateRangeLocked}" />
                <TextBlock Text="—" VerticalAlignment="Center" Opacity="0.55" />
                <atom:DatePicker SelectedDateTime="{Binding DateTo, Mode=TwoWay}"
                                 Format="yyyy-MM-dd"
                                 IsEnabled="{Binding !DateRangeLocked}" />
              </StackPanel>
              <TextBlock Text="确认起止日期后可直接开始提取。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap"
                         IsVisible="{Binding !DateRangeLocked}" />
              <TextBlock Text="当前为受控复跑,日期范围已按上下文锁定。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap"
                         IsVisible="{Binding DateRangeLocked}" />
            </StackPanel>
          </atom:Card>

          <atom:Card Header="公司配置" StyleVariant="Outlined">
            <StackPanel Spacing="10">
              <TextBlock Text="公司" FontWeight="SemiBold" />
              <atom:LineEdit Text="{Binding CompanyName, Mode=TwoWay}"
                             Watermark="填写用于匹配购买方的公司名称" />
              <TextBlock Text="按购买方包含公司名称进行匹配,明确不匹配的票据会单独进入“非目标公司发票”。"
                         FontSize="12" Opacity="0.55" TextWrapping="Wrap" />
            </StackPanel>
          </atom:Card>
        </StackPanel>
      </Grid>

      <Border BorderThickness="0,1,0,0" BorderBrush="#22888888" Padding="0,14,0,0">
        <Grid ColumnDefinitions="*,Auto">
          <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="10" VerticalAlignment="Center">
            <atom:ToggleSwitch IsChecked="{Binding RememberSettings, Mode=TwoWay}" />
            <TextBlock Text="自动记住当前配置" VerticalAlignment="Center" />
          </StackPanel>
          <atom:Button Grid.Column="1"
                       ButtonType="Primary"
                       Content="开始提取"
                       Command="{Binding StartCommand}" />
        </Grid>
      </Border>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

创建 `src/InvoiceFlowAI.App/Views/SettingsPageView.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InvoiceFlowAI.App.Views;

public partial class SettingsPageView : UserControl
{
    private static readonly Uri ApiKeysUri = new("https://platform.deepseek.com/api_keys");

    public SettingsPageView()
    {
        InitializeComponent();
    }

    private async void OnOpenApiKeysClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is not null)
        {
            await topLevel.Launcher.LaunchUriAsync(ApiKeysUri);
        }
    }
}
```

- [ ] **Step 5: 接上 DataTemplate 与 DI,删除已退役 RPC**

`src/InvoiceFlowAI.App/App.axaml` 整体替换为:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:InvoiceFlowAI.App.ViewModels"
             xmlns:views="using:InvoiceFlowAI.App.Views"
             x:Class="InvoiceFlowAI.App.App">
  <Application.DataTemplates>
    <DataTemplate DataType="{x:Type vm:SettingsViewModel}">
      <views:SettingsPageView />
    </DataTemplate>
  </Application.DataTemplates>
  <Application.Styles>
    <FluentTheme />
  </Application.Styles>
</Application>
```

`AppServiceProviderFactory.cs`:

- 删除 9 行 `services.AddScoped<AccountListRpcHandler>();` / `AccountSaveRpcHandler` / `AccountTestRpcHandler` / `ProviderTestRpcHandler` / `SettingsGetRpcHandler` / `SettingsUpdateRpcHandler` / `SecretSetRpcHandler` / `SecretDeleteRpcHandler` / `DirectoryChooseRpcHandler`。
- 把设置页的 `WebPageViewModel` 注册替换为:

```csharp
        services.AddSingleton<SettingsViewModel>(provider => new SettingsViewModel(
            provider.GetRequiredService<IScopedOperationRunner>(),
            provider.GetRequiredService<IDirectoryPicker>(),
            provider.GetRequiredService<IRunSession>(),
            provider.GetRequiredService<IAppNavigator>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IPersistentSecretStore>(),
            provider.GetRequiredService<ILogger<SettingsViewModel>>()));
        services.AddSingleton<IPageViewModel>(provider => provider.GetRequiredService<SettingsViewModel>());
```

补 `using Microsoft.Extensions.Logging;`(已存在则跳过)。

`AppRpcComposition.cs`:从 `V1Methods` 删除 `settings.get`、`settings.update`、`account.list`、`account.save`、`account.test`、`provider.test`、`secret.set`、`secret.delete`、`directory.choose`;并删除对应的 9 行 `dispatcher.RegisterHandler(...)`。

删除文件清单里的 9 个 handler、3 个 RPC 测试文件,以及两个只验证"WebView 设置切片"的端到端测试(`SettingsSliceWebViewTests`、`NativeWebViewSettingsIntegrationTests`)——设置页原生化后它们验证的通路已不存在。

- [ ] **Step 6: 更新 RPC 组合测试**

`tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`:从期望方法列表删除上列 9 项;`hello` 的 `registeredMethods` 断言同步删除。

`tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`:删除整个 `Typed_rpc_surface_dispatches_without_exposing_secrets_or_legacy_methods` 测试方法(它调用的 settings/account/provider/secret 方法已全部退役,后续任务还会删除剩余 run/report/window 方法;该方法失去意义),并删除因此不再被引用的 `DispatchAsync` 私有辅助方法、`JsonOptions` 等 using(保留仍在使用的项)。把"设置页解析为 `WebPageViewModel`"断言改为:

```csharp
        var pages = provider.GetServices<IPageViewModel>().ToArray();
        pages.Should().Contain(page => page is SettingsViewModel);
        pages.Should().NotContain(page => page is WebPageViewModel web && web.Key == "settings");
```

- [ ] **Step 7: 运行测试与构建**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj -c Debug
```

Expected: 全绿。启动应用确认:默认进入原生设置页;可以填写/保存;测试按钮给出中文状态;关闭再打开后公司名与目录仍在;点击"处理中心"仍显示嵌入的 SPA 且进度页正常。

- [ ] **Step 8: 提交**

```bash
git add -A src/InvoiceFlowAI.App tests/InvoiceFlowAI.App.Tests
git commit -m "feat(settings): render the settings page natively and retire its RPC handlers

- SettingsPageView with AtomUI cards, alerts, date pickers and line edits
- DataTemplate maps SettingsViewModel to the native view
- delete account.*, provider.*, settings.*, secret.* and directory.choose handlers

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 8: 阶段 2 — 处理中心 ViewModel(轮询、状态映射、停止)

**Files:**
- Create: `src/InvoiceFlowAI.App/ViewModels/ProcessingViewModel.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/ProcessingOptions.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/ProcessingCopy.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/ActivityLogEntry.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/TestSupport/Fakes.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/ViewModels/ProcessingViewModelTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/ViewModels/SettingsViewModelTests.cs`(删除已移出的三个嵌套假实现)

**Interfaces:**
- Consumes: `IDesktopRunService`、`IRunSession`、`IScopedOperationRunner`、`IAppNavigator`、`INotificationService`、`IAppEventBus`(Task 3)、`IAvaloniaUiDispatcher`。
- 事件流:页面激活时订阅 `IAppEventBus`,收到任意运行事件后经 `IAvaloniaUiDispatcher.Post` 触发一次立即刷新;事件 payload 不解析。轮询仍然保留,作为事件丢失时的兜底。
- Produces: `ProcessingOptions(TimeSpan PollInterval, TimeSpan TerminalNavigationDelay)`,静态 `Production`(1s / 1.2s)与 `Fast`(10ms / 0)。
- Produces: `ProcessingViewModel : ObservableObject, IPageViewModel`,成员:
  - 属性:`RunId`、`ProgressValue`、`StatusText`、`EmailCount`、`InvoiceCount`、`ErrorCount`、`IsRunning`、`CanStop`、`StopPending`、`QuotaMessage`、`HasQuotaWarning`、`MailboxFailureMessage`、`HasMailboxWarning`、`LastErrorMessage`、`HasLastError`、`PageError`、`HasPageError`、`Logs`(`ObservableCollection<ActivityLogEntry>`)。
  - 命令:`StopCommand`;方法:`RefreshAsync(CancellationToken)`(测试与轮询共用)。
- Produces(测试辅助):`TestSupport/Fakes.cs` 中的 `FakeRunSession`、`RecordingNavigator`、`NullNotificationService`、`FakeDesktopRunService`、`SynchronousUiDispatcher`。
- **关键约束**:`RunProgressSnapshot.StatusText` 是管线节点 id(`extract-documents` 等)、`LastError` 是失败码、`QuotaMessage` 是英文、`Logs[].Message` 是后端原始消息。四者都**不得**直接绑定到 UI;VM 必须映射为固定中文文案,活动日志由 VM 依据快照变化自行合成。

- [ ] **Step 1: 写共享测试假实现与失败的测试**

创建 `tests/InvoiceFlowAI.App.Tests/TestSupport/Fakes.cs`:

```csharp
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Rpc;

namespace InvoiceFlowAI.App.Tests.TestSupport;

public sealed class FakeRunSession : IRunSession
{
    public string? RunId { get; private set; }
    public void SetRunId(string? runId) => RunId = runId;
}

public sealed class RecordingNavigator : IAppNavigator
{
    public List<string> Requested { get; } = [];

    public Task NavigateAsync(string key, CancellationToken cancellationToken = default)
    {
        Requested.Add(key);
        return Task.CompletedTask;
    }
}

public sealed class NullNotificationService : INotificationService
{
    public List<(string Message, NotificationKind Kind)> Shown { get; } = [];

    public void Show(string message, NotificationKind kind) => Shown.Add((message, kind));
}

public sealed class SynchronousUiDispatcher : IAvaloniaUiDispatcher
{
    public Task<TResult> InvokeAsync<TResult>(Func<TResult> action, CancellationToken cancellationToken)
        => Task.FromResult(action());

    public Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        action();
        return Task.CompletedTask;
    }

    public void Post(Action action) => action();
}

public sealed class FakeDesktopRunService(RunContextSnapshot context) : IDesktopRunService
{
    public List<RunStartRequest> Started { get; } = [];
    public List<RunStopRequest> Stopped { get; } = [];
    public RunProgressSnapshot Progress { get; set; } = new(
        RunState.Idle,
        false,
        false,
        false,
        0,
        "processing",
        new RunProgressStats(0, 0, 0),
        [],
        null,
        false,
        null,
        null,
        null,
        null);

    public Task<RunContextSnapshot> GetContextAsync(CancellationToken cancellationToken)
        => Task.FromResult(context);

    public Task<RunStartResult> StartAsync(RunStartRequest request, CancellationToken cancellationToken)
    {
        Started.Add(request);
        return Task.FromResult(new RunStartResult(true, request.RunId, null));
    }

    public Task<RunProgressSnapshot> GetProgressAsync(string? runId, CancellationToken cancellationToken)
        => Task.FromResult(Progress);

    public Task<RunStopResult> StopAsync(RunStopRequest request, CancellationToken cancellationToken)
    {
        Stopped.Add(request);
        return Task.FromResult(new RunStopResult(true, false, null));
    }
}
```

从 `tests/InvoiceFlowAI.App.Tests/ViewModels/SettingsViewModelTests.cs` 删除嵌套的 `FakeRunSession`、`RecordingNavigator`、`NullNotificationService`,并让 harness 使用 `TestSupport` 中的同名类型(补 `using InvoiceFlowAI.App.Tests.TestSupport;`)。`FakeDesktopRunService` 同样改用共享实现,`Started` 断言不变。

创建 `tests/InvoiceFlowAI.App.Tests/ViewModels/ProcessingViewModelTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class ProcessingViewModelTests
{
    [Fact]
    public async Task Refresh_maps_stage_codes_to_product_copy_and_counts()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "extract-documents", progress: 42, stats: new(10, 3, 1));
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.ProgressValue.Should().Be(42);
        viewModel.StatusText.Should().Be("正在识别票据...");
        viewModel.EmailCount.Should().Be(10);
        viewModel.InvoiceCount.Should().Be(3);
        viewModel.ErrorCount.Should().Be(1);
        viewModel.StatusText.Should().NotContain("extract-documents");
    }

    [Fact]
    public async Task Refresh_maps_failure_code_to_product_copy_without_leaking_the_code()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Failed, stage: "scan-mailbox", lastError: "IMAP_LOGIN_FAILED");
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.StatusText.Should().Be("处理失败");
        viewModel.HasLastError.Should().BeTrue();
        viewModel.LastErrorMessage.Should().Be("邮箱授权码无效或已过期,请更新授权码后重试。");
        viewModel.LastErrorMessage.Should().NotContain("IMAP");
    }

    [Fact]
    public async Task Refresh_synthesizes_activity_log_from_snapshot_changes_only()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "scan-mailbox", progress: 10, stats: new(5, 0, 0));
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var afterFirst = viewModel.Logs.Count;
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var afterSecond = viewModel.Logs.Count;
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "extract-documents", progress: 30, stats: new(5, 2, 0));
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        afterFirst.Should().BeGreaterThan(0);
        afterSecond.Should().Be(afterFirst);
        viewModel.Logs.Count.Should().BeGreaterThan(afterFirst);
        viewModel.Logs.Should().OnlyContain(entry => !entry.Message.Contains("scan-mailbox"));
    }

    [Fact]
    public async Task Refresh_surfaces_quota_and_mailbox_failures_as_fixed_chinese_copy()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(
            RunState.Running,
            stage: "extract-documents",
            quota: true,
            quotaMessage: "Provider quota exhausted.",
            mailboxFailures: [new RunMailboxFetchFailureDiagnostic(12, "IMAP_MESSAGE_FETCH_FAILED")]);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.HasQuotaWarning.Should().BeTrue();
        viewModel.QuotaMessage.Should().Be("大模型额度已用尽，请补充额度后重新运行。");
        viewModel.HasMailboxWarning.Should().BeTrue();
        viewModel.MailboxFailureMessage.Should().Be("1 封邮件读取失败,已跳过。");
    }

    [Fact]
    public async Task Stop_requests_a_safe_stop_and_disables_the_button()
    {
        var harness = new ProcessingHarness();
        harness.Session.SetRunId("run-1");
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "scan-mailbox", canStop: true);
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.StopCommand.ExecuteAsync(null);

        harness.Runs.Stopped.Should().ContainSingle();
        harness.Runs.Stopped[0].RunId.Should().Be("run-1");
        viewModel.StopPending.Should().BeTrue();
        viewModel.StopCommand.CanExecute(null).Should().BeFalse();
        viewModel.Logs.Should().Contain(entry => entry.Message == "已收到安全停止指令,正在收尾...");
    }

    [Fact]
    public async Task Terminal_state_navigates_to_analysis_once()
    {
        var harness = new ProcessingHarness();
        harness.Runs.Progress = Snapshot(RunState.Completed, stage: "completed", progress: 100);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        harness.Navigator.Requested.Should().Equal("analysis");
    }

    [Fact]
    public async Task Run_events_trigger_an_immediate_refresh_without_reading_the_payload()
    {
        var harness = new ProcessingHarness(pollInterval: TimeSpan.FromHours(1));
        harness.Runs.Progress = Snapshot(RunState.Running, stage: "scan-mailbox", progress: 15, stats: new(4, 0, 0));
        var viewModel = harness.CreateViewModel();
        await viewModel.OnActivatedAsync(TestContext.Current.CancellationToken);

        harness.Bus.Publish(new AppRunEvent("run.progress", "run-1", 1, DateTimeOffset.UtcNow, new object()));
        await Task.Yield();

        viewModel.EmailCount.Should().Be(4);
        await viewModel.OnDeactivatedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_run_id_shows_a_page_error()
    {
        var harness = new ProcessingHarness(context: new RunContextSnapshot(
            false, false, false, 0, null, null, null, null, null, null));
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.HasPageError.Should().BeTrue();
        viewModel.PageError.Should().Be("任务记录不可用,请返回重新开始。");
    }

    private static RunProgressSnapshot Snapshot(
        RunState state,
        string stage,
        int progress = 0,
        RunProgressStats? stats = null,
        string? lastError = null,
        bool quota = false,
        string? quotaMessage = null,
        bool canStop = false,
        IReadOnlyList<RunMailboxFetchFailureDiagnostic>? mailboxFailures = null)
        => new(
            state,
            state is RunState.Running or RunState.Stopping,
            canStop,
            state == RunState.Stopping,
            progress,
            stage,
            stats ?? new RunProgressStats(0, 0, 0),
            [],
            lastError,
            quota,
            quotaMessage,
            null,
            null,
            null)
        {
            MailboxFetchFailures = mailboxFailures ?? [],
        };

    private sealed class ProcessingHarness
    {
        private readonly TimeSpan _pollInterval;

        public ProcessingHarness(RunContextSnapshot? context = null, TimeSpan? pollInterval = null)
        {
            Runs = new FakeDesktopRunService(context ?? new RunContextSnapshot(
                false, false, false, 0, "run-1", null, null, null, null, "default-mailbox"));
            _pollInterval = pollInterval ?? ProcessingOptions.Fast.PollInterval;
        }

        public FakeDesktopRunService Runs { get; }
        public FakeRunSession Session { get; } = new();
        public RecordingNavigator Navigator { get; } = new();
        public NullNotificationService Notifications { get; } = new();
        public AppEventBus Bus { get; } = new(NullLogger<AppEventBus>.Instance);
        public SynchronousUiDispatcher Dispatcher { get; } = new();

        public ProcessingViewModel CreateViewModel()
        {
            var operations = TestScopedOperationRunner.Create(services =>
                services.AddSingleton<IDesktopRunService>(Runs));

            return new ProcessingViewModel(
                operations,
                Session,
                Navigator,
                Notifications,
                Bus,
                Dispatcher,
                new ProcessingOptions(_pollInterval, TimeSpan.Zero),
                NullLogger<ProcessingViewModel>.Instance);
        }
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~ProcessingViewModelTests"
```

Expected: 编译失败,`ProcessingViewModel` 等类型不存在。

- [ ] **Step 3: 写文案、选项与日志条目**

创建 `src/InvoiceFlowAI.App/ViewModels/ActivityLogEntry.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

public sealed record ActivityLogEntry(DateTimeOffset Timestamp, string Message);
```

创建 `src/InvoiceFlowAI.App/ViewModels/ProcessingOptions.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

public sealed record ProcessingOptions(TimeSpan PollInterval, TimeSpan TerminalNavigationDelay)
{
    public static ProcessingOptions Production { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1200));

    public static ProcessingOptions Fast { get; } = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero);
}
```

创建 `src/InvoiceFlowAI.App/ViewModels/ProcessingCopy.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

/// <summary>
/// 处理中心的固定产品文案。后端阶段码、失败码与英文消息一律在此翻译,
/// 不进入任何绑定属性。
/// </summary>
public static class ProcessingCopy
{
    public const string Title = "处理中心";
    public const string Eyebrow = "Step 2";
    public const string Waiting = "等待任务开始...";
    public const string Running = "正在处理...";
    public const string StopRequested = "已收到安全停止指令,正在收尾...";
    public const string Completed = "处理完成";
    public const string Failed = "处理失败";
    public const string Cancelled = "任务已停止";
    public const string RunUnavailable = "任务记录不可用,请返回重新开始。";
    public const string LoadFailed = "进度加载失败,请稍后重试。";
    public const string StopAccepted = "已收到安全停止指令,正在收尾...";
    public const string StopRejected = "当前任务已无法停止。";
    public const string StopFailed = "停止指令发送失败。";
    public const string QuotaWarning = "大模型额度已用尽，请补充额度后重新运行。";
    public const string UnknownFailure = "本次处理出现异常,详情已写入日志。";
    public const string ProviderAuthFailure = "API Key 验证失败,请更新后重试。";
    public const string PipelineIncomplete = "本次处理未能完成,请查看结果或重新运行。";
    public const string MailboxLoginFailure = "邮箱授权码无效或已过期,请更新授权码后重试。";
    public const string MailboxConnectionFailure = "无法连接邮箱服务器,请检查网络后重试。";
    public const string MailboxFetchFailure = "部分邮件读取失败,已跳过并记录。";
    public const string ReadyLog = "已连接任务进度。";
    public const string CompletedLog = "处理完成。";
    public const string FailedLog = "处理失败。";
    public const string CancelledLog = "任务已停止。";

    public static string MapStage(string stage) => stage switch
    {
        "validate-request" => "正在校验任务参数...",
        "scan-mailbox" => "正在拉取邮件...",
        "collect-candidates" => "正在整理票据候选...",
        "recover-urls" => "正在补全票据链接...",
        "extract-documents" => "正在识别票据...",
        "pair-artifacts" => "正在复核与配对...",
        "manual_review" => "正在生成待复核记录...",
        "archive-documents" => "正在归档票据...",
        "export-report" => "正在生成结果...",
        _ => Running,
    };

    public static string MapFailureCode(string? failureCode) => failureCode switch
    {
        "IMAP_LOGIN_FAILED" => MailboxLoginFailure,
        "IMAP_CONNECTION_FAILED" => MailboxConnectionFailure,
        "IMAP_MESSAGE_FETCH_FAILED" => MailboxFetchFailure,
        "PROVIDER_AUTH_FAILED" => ProviderAuthFailure,
        "QUOTA_EXHAUSTED" => QuotaWarning,
        "RUN_PIPELINE_INCOMPLETE" or "RUN_EXECUTION_FAILED" => PipelineIncomplete,
        _ => UnknownFailure,
    };
}
```

- [ ] **Step 4: 实现 ProcessingViewModel**

创建 `src/InvoiceFlowAI.App/ViewModels/ProcessingViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Events;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class ProcessingViewModel : ObservableObject, IPageViewModel
{
    private const int MaxLogEntries = 200;

    private readonly IScopedOperationRunner _operations;
    private readonly IRunSession _runSession;
    private readonly IAppNavigator _navigator;
    private readonly INotificationService _notifications;
    private readonly IAppEventBus _bus;
    private readonly IAvaloniaUiDispatcher _dispatcher;
    private readonly ProcessingOptions _options;
    private readonly ILogger<ProcessingViewModel> _logger;

    private CancellationTokenSource? _pollCancellation;
    private IDisposable? _runEventSubscription;
    private string? _runId;
    private int _lastEmails;
    private int _lastInvoices;
    private int _lastErrors;
    private RunState _lastState = RunState.Idle;
    private bool _stopLogged;
    private bool _navigated;
    private bool _started;

    [ObservableProperty]
    private string _runIdLabel = string.Empty;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private string _statusText = ProcessingCopy.Waiting;

    [ObservableProperty]
    private int _emailCount;

    [ObservableProperty]
    private int _invoiceCount;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _canStop;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _stopPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private string _pageError = string.Empty;

    [ObservableProperty]
    private string _quotaMessage = string.Empty;

    [ObservableProperty]
    private string _mailboxFailureMessage = string.Empty;

    [ObservableProperty]
    private string _lastErrorMessage = string.Empty;

    public ProcessingViewModel(
        IScopedOperationRunner operations,
        IRunSession runSession,
        IAppNavigator navigator,
        INotificationService notifications,
        IAppEventBus bus,
        IAvaloniaUiDispatcher dispatcher,
        ProcessingOptions options,
        ILogger<ProcessingViewModel> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLogs));
    }

    public string Key => "processing";

    public string Title => ProcessingCopy.Title;

    public string Eyebrow => ProcessingCopy.Eyebrow;

    public ObservableCollection<ActivityLogEntry> Logs { get; } = [];

    public bool HasLogs => Logs.Count > 0;

    public bool HasPageError => !string.IsNullOrWhiteSpace(PageError);

    public bool HasQuotaWarning => !string.IsNullOrWhiteSpace(QuotaMessage);

    public bool HasMailboxWarning => !string.IsNullOrWhiteSpace(MailboxFailureMessage);

    public bool HasLastError => !string.IsNullOrWhiteSpace(LastErrorMessage);

    partial void OnPageErrorChanged(string value) => OnPropertyChanged(nameof(HasPageError));

    partial void OnQuotaMessageChanged(string value) => OnPropertyChanged(nameof(HasQuotaWarning));

    partial void OnMailboxFailureMessageChanged(string value) => OnPropertyChanged(nameof(HasMailboxWarning));

    partial void OnLastErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasLastError));

    public Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _runEventSubscription = _bus.Subscribe(_ => _dispatcher.Post(RefreshFromEvent));
        _ = PollLoopAsync(_pollCancellation.Token);
        return Task.CompletedTask;
    }

    public Task OnDeactivatedAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _runEventSubscription?.Dispose();
        _runEventSubscription = null;
        _pollCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async void RefreshFromEvent()
    {
        try
        {
            await RefreshAsync(_pollCancellation?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 页面切换或轮询取消。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "处理运行事件时刷新进度失败。");
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var runId = await ResolveRunIdAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(runId))
        {
            PageError = ProcessingCopy.RunUnavailable;
            return;
        }

        var snapshot = await _operations.RunAsync(
            (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                .GetProgressAsync(runId, token),
            cancellationToken).ConfigureAwait(true);

        await ApplyAsync(snapshot, cancellationToken).ConfigureAwait(true);
        PageError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanStopRun))]
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var runId = _runId ?? _runSession.RunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>()
                    .StopAsync(new RunStopRequest(runId), token),
                cancellationToken).ConfigureAwait(true);

            if (!result.Accepted)
            {
                _notifications.Show(ProcessingCopy.StopRejected, NotificationKind.Warning);
                return;
            }

            StopPending = true;
            AppendLog(ProcessingCopy.StopAccepted);
        }
        catch (OperationCanceledException)
        {
            // 页面切换导致的取消不算失败。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "发送停止指令失败。");
            _notifications.Show(ProcessingCopy.StopFailed, NotificationKind.Error);
        }
    }

    private bool CanStopRun() => IsRunning && CanStop && !StopPending;

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "刷新进度失败。");
                PageError = ProcessingCopy.LoadFailed;
            }

            try
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<string?> ResolveRunIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_runId))
        {
            return _runId;
        }

        var candidate = _runSession.RunId;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var context = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>().GetContextAsync(token),
                cancellationToken).ConfigureAwait(true);
            candidate = context.RunId;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        _runId = candidate;
        _runSession.SetRunId(candidate);
        RunIdLabel = candidate;
        return candidate;
    }

    private async Task ApplyAsync(RunProgressSnapshot snapshot, CancellationToken cancellationToken)
    {
        ProgressValue = snapshot.IsRunning ? snapshot.Progress : 100;
        IsRunning = snapshot.IsRunning;
        CanStop = snapshot.CanStop;
        StopPending = snapshot.StopRequested;
        EmailCount = snapshot.Stats.Emails;
        InvoiceCount = snapshot.Stats.Invoices;
        ErrorCount = snapshot.Stats.Errors;
        StatusText = MapStatus(snapshot);
        QuotaMessage = snapshot.QuotaExhausted ? ProcessingCopy.QuotaWarning : string.Empty;
        MailboxFailureMessage = snapshot.MailboxFetchFailures.Count == 0
            ? string.Empty
            : $"{snapshot.MailboxFetchFailures.Count} 封邮件读取失败,已跳过。";
        LastErrorMessage = string.IsNullOrWhiteSpace(snapshot.LastError)
            ? string.Empty
            : ProcessingCopy.MapFailureCode(snapshot.LastError);

        AppendDeltaLogs(snapshot);

        if (snapshot.StopRequested && !_stopLogged)
        {
            _stopLogged = true;
            AppendLog(ProcessingCopy.StopAccepted);
        }

        if (!snapshot.IsRunning && snapshot.RunState is RunState.Completed or RunState.Failed or RunState.Cancelled)
        {
            if (_lastState != snapshot.RunState)
            {
                AppendLog(snapshot.RunState switch
                {
                    RunState.Completed => ProcessingCopy.CompletedLog,
                    RunState.Cancelled => ProcessingCopy.CancelledLog,
                    _ => ProcessingCopy.FailedLog,
                });
            }

            if (!_navigated)
            {
                _navigated = true;
                if (_options.TerminalNavigationDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.TerminalNavigationDelay, cancellationToken).ConfigureAwait(true);
                }

                await _navigator.NavigateAsync("analysis", cancellationToken).ConfigureAwait(true);
            }
        }

        _lastState = snapshot.RunState;
    }

    private void AppendDeltaLogs(RunProgressSnapshot snapshot)
    {
        if (Logs.Count == 0)
        {
            AppendLog(ProcessingCopy.ReadyLog);
        }

        if (snapshot.Stats.Emails > _lastEmails)
        {
            AppendLog($"已获取 {snapshot.Stats.Emails} 封邮件。");
        }

        if (snapshot.Stats.Invoices > _lastInvoices)
        {
            AppendLog($"已识别 {snapshot.Stats.Invoices} 张票据。");
        }

        if (snapshot.Stats.Errors > _lastErrors)
        {
            AppendLog($"新增 {snapshot.Stats.Errors} 条待处理记录。");
        }

        _lastEmails = snapshot.Stats.Emails;
        _lastInvoices = snapshot.Stats.Invoices;
        _lastErrors = snapshot.Stats.Errors;
    }

    private void AppendLog(string message)
    {
        Logs.Insert(0, new ActivityLogEntry(DateTimeOffset.Now, message));
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }

    private static string MapStatus(RunProgressSnapshot snapshot)
    {
        if (snapshot.StopRequested)
        {
            return ProcessingCopy.StopRequested;
        }

        return snapshot.RunState switch
        {
            RunState.Completed => ProcessingCopy.Completed,
            RunState.Failed => ProcessingCopy.Failed,
            RunState.Cancelled => ProcessingCopy.Cancelled,
            RunState.Stopping => ProcessingCopy.StopRequested,
            RunState.Running => ProcessingCopy.MapStage(snapshot.StatusText),
            _ => ProcessingCopy.Waiting,
        };
    }
}
```

- [ ] **Step 5: 运行测试**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~ProcessingViewModelTests|FullyQualifiedName~SettingsViewModelTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add -A src/InvoiceFlowAI.App/ViewModels tests/InvoiceFlowAI.App.Tests
git commit -m "feat(processing): add the native processing view model

- poll run progress on the UI thread and expose counts, status and stop state
- translate stage codes, failure codes and quota messages into fixed product copy
- synthesize the activity log from snapshot deltas instead of raw backend logs
- navigate to the analysis page once the run reaches a terminal state

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 9: 阶段 2 — 处理中心视图与 run.* RPC 退役

**Files:**
- Create: `src/InvoiceFlowAI.App/Views/ProcessingPageView.axaml`
- Create: `src/InvoiceFlowAI.App/Views/ProcessingPageView.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/App.axaml`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs:19-25,56-60`
- Modify: `src/InvoiceFlowAI.App/Rpc/RunAndReportRpcHandlers.cs:9-36`(删除 4 个 handler 类)
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/RunAndReportRpcHandlersTests.cs:14-47`
- Delete: `tests/InvoiceFlowAI.App.Tests/EndToEnd/NativeWebViewRunIntegrationTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Views/ProcessingPageViewTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`

**Interfaces:**
- Consumes: `ProcessingViewModel`(Task 8)。
- Produces: `ProcessingPageView` 通过 `DataTemplate` 匹配 `ProcessingViewModel`。
- 说明:`NativeWebViewRunIntegrationTests` 验证的是"SPA 通过 WebView 启动并跑完一次任务",处理页原生化后该通路不再存在,删除;真正的运行回归由 Task 13 的人工清单覆盖。
- 说明:`RunAndReportRpcHandlers.cs` 只删前 4 个类(`RunContextRpcHandler`、`RunStartRpcHandler`、`RunStatusRpcHandler`、`RunStopRpcHandler`),保留 `RunResultsGetRpcHandler` 与 `ReportExportRpcHandler` 给分析页过渡。

- [ ] **Step 1: 写失败的视图冒烟测试**

创建 `tests/InvoiceFlowAI.App.Tests/Views/ProcessingPageViewTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.Views;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class ProcessingPageViewTests
{
    [Fact]
    public void Processing_page_view_builds_progress_and_log_list()
    {
        var result = AtomUiHeadlessHost.Run(() =>
        {
            var view = new ProcessingPageView();
            view.Measure(new Avalonia.Size(1180, 760));
            return new ViewSnapshot(
                view.GetVisualDescendants().OfType<AtomUI.Desktop.Controls.ProgressBar>().Count(),
                view.GetVisualDescendants().OfType<ListBox>().Count());
        });

        result.ProgressBars.Should().BeGreaterThanOrEqualTo(1);
        result.Lists.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed record ViewSnapshot(int ProgressBars, int Lists);
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~ProcessingPageViewTests"
```

Expected: 编译失败,`ProcessingPageView` 不存在。

- [ ] **Step 3: 写处理中心视图**

创建 `src/InvoiceFlowAI.App/Views/ProcessingPageView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:atom="https://atomui.net"
             xmlns:vm="using:InvoiceFlowAI.App.ViewModels"
             x:Class="InvoiceFlowAI.App.Views.ProcessingPageView"
             x:DataType="vm:ProcessingViewModel">
  <Grid RowDefinitions="Auto,*,Auto" Margin="28,20,28,24">
    <Grid Grid.Row="0" ColumnDefinitions="*,Auto" Margin="0,0,0,16">
      <StackPanel Grid.Column="0" Spacing="6">
        <TextBlock Text="{Binding Eyebrow}" FontSize="12" Opacity="0.55" />
        <TextBlock Text="{Binding Title}" FontSize="26" FontWeight="SemiBold" />
      </StackPanel>
      <atom:Button Grid.Column="1"
                   ButtonType="Primary"
                   IsDanger="True"
                   Content="停止任务"
                   VerticalAlignment="Center"
                   Command="{Binding StopCommand}" />
    </Grid>

    <ScrollViewer Grid.Row="1">
      <StackPanel Spacing="16">
        <atom:Alert Type="Error"
                    IsShowIcon="True"
                    IsClosable="False"
                    Message="{Binding PageError}"
                    IsVisible="{Binding HasPageError}" />
        <atom:Alert Type="Error"
                    IsShowIcon="True"
                    IsClosable="False"
                    Message="{Binding LastErrorMessage}"
                    IsVisible="{Binding HasLastError}" />
        <atom:Alert Type="Warning"
                    IsShowIcon="True"
                    IsClosable="False"
                    Message="{Binding QuotaMessage}"
                    IsVisible="{Binding HasQuotaWarning}" />
        <atom:Alert Type="Warning"
                    IsShowIcon="True"
                    IsClosable="False"
                    Message="{Binding MailboxFailureMessage}"
                    IsVisible="{Binding HasMailboxWarning}" />

        <atom:Card StyleVariant="Outlined">
          <StackPanel Spacing="12">
            <Grid ColumnDefinitions="*,Auto">
              <TextBlock Grid.Column="0" Text="{Binding StatusText}" FontWeight="SemiBold" />
              <TextBlock Grid.Column="1" Text="{Binding RunIdLabel}" FontSize="11" Opacity="0.45" />
            </Grid>
            <atom:ProgressBar Minimum="0"
                              Maximum="100"
                              Value="{Binding ProgressValue}"
                              IsProgressInfoVisible="True"
                              Status="Active" />
            <Grid ColumnDefinitions="*,*,*" ColumnSpacing="12">
              <atom:Statistic Grid.Column="0" Header="邮件" Value="{Binding EmailCount}" />
              <atom:Statistic Grid.Column="1" Header="票据" Value="{Binding InvoiceCount}" />
              <atom:Statistic Grid.Column="2" Header="待处理" Value="{Binding ErrorCount}" />
            </Grid>
          </StackPanel>
        </atom:Card>

        <atom:Card Header="处理日志" StyleVariant="Outlined">
          <Grid>
            <TextBlock Text="任务开始后,这里会显示处理进度。"
                       Opacity="0.55"
                       IsVisible="{Binding !HasLogs}" />
            <ListBox ItemsSource="{Binding Logs}"
                     Background="Transparent"
                     BorderThickness="0"
                     MaxHeight="360">
              <ListBox.ItemTemplate>
                <DataTemplate x:DataType="vm:ActivityLogEntry">
                  <Grid ColumnDefinitions="76,*" Margin="0,2">
                    <TextBlock Grid.Column="0"
                               Text="{Binding Timestamp, StringFormat='{}{0:HH:mm:ss}'}"
                               FontSize="11"
                               Opacity="0.45" />
                    <TextBlock Grid.Column="1" Text="{Binding Message}" TextWrapping="Wrap" />
                  </Grid>
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
          </Grid>
        </atom:Card>
      </StackPanel>
    </ScrollViewer>
  </Grid>
</UserControl>
```

创建 `src/InvoiceFlowAI.App/Views/ProcessingPageView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace InvoiceFlowAI.App.Views;

public partial class ProcessingPageView : UserControl
{
    public ProcessingPageView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4: 接 DataTemplate 与 DI,删除 run 启动/进度 RPC**

`App.axaml` 的 `Application.DataTemplates` 追加:

```xml
    <DataTemplate DataType="{x:Type vm:ProcessingViewModel}">
      <views:ProcessingPageView />
    </DataTemplate>
```

`AppServiceProviderFactory.cs`:

- 删除 4 行 handler 注册:`RunContextRpcHandler`、`RunStartRpcHandler`、`RunStatusRpcHandler`、`RunStopRpcHandler`。
- 把处理页的 `WebPageViewModel` 注册替换为:

```csharp
        services.AddSingleton(ProcessingOptions.Production);
        services.AddSingleton<ProcessingViewModel>(provider => new ProcessingViewModel(
            provider.GetRequiredService<IScopedOperationRunner>(),
            provider.GetRequiredService<IRunSession>(),
            provider.GetRequiredService<IAppNavigator>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IAppEventBus>(),
            provider.GetRequiredService<IAvaloniaUiDispatcher>(),
            provider.GetRequiredService<ProcessingOptions>(),
            provider.GetRequiredService<ILogger<ProcessingViewModel>>()));
        services.AddSingleton<IPageViewModel>(provider => provider.GetRequiredService<ProcessingViewModel>());
```

`AppRpcComposition.cs`:从 `V1Methods` 删除 `run.context.get`、`run.start`、`run.progress.get`、`run.stop`,并删除对应 4 行 `dispatcher.RegisterHandler(...)`。

`RunAndReportRpcHandlers.cs`:删除 `RunContextRpcHandler`、`RunStartRpcHandler`、`RunStatusRpcHandler`、`RunStopRpcHandler` 四个类,文件保留 `RunResultsGetRpcHandler` 与 `ReportExportRpcHandler`。

`tests/InvoiceFlowAI.App.Tests/Rpc/RunAndReportRpcHandlersTests.cs`:删除 `Run_start_forwards_typed_request_and_serializes_accepted_result` 与 `Run_stop_maps_desktop_run_exception_to_stable_rpc_error` 两个测试方法;如果 `FakeDesktopRunService.StartResult` / `StopException` 不再被引用,一并删除这两个属性。

- [ ] **Step 5: 更新 RPC 组合测试**

`AppRpcCompositionTests.cs` 与 `AppServiceProviderFactoryTests.cs` 同步删除这 4 个方法名与 handler 断言,并补充:

```csharp
        provider.GetServices<IPageViewModel>().Should().Contain(page => page is ProcessingViewModel);
```

- [ ] **Step 6: 运行测试与构建**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj -c Debug
```

Expected: 全绿(依赖真实邮箱/WebView2 的端到端测试已被删除,不在此列)。

- [ ] **Step 7: 手动验收**

启动应用 → 设置页填好配置 → 开始提取 → 原生处理页显示状态、进度、三项统计与自动生成的中文日志;停止按钮发送安全停止后变为禁用并显示"已收到安全停止指令,正在收尾...";任务结束后自动跳到仍为嵌入模式的分析页,分析页数据正常。

- [ ] **Step 8: 提交**

```bash
git add -A src/InvoiceFlowAI.App tests/InvoiceFlowAI.App.Tests
git commit -m "feat(processing): render the processing center natively and retire run.* RPC

- ProcessingPageView with live progress, statistics, alerts and a virtualized log list
- run.context.get, run.start, run.progress.get and run.stop handlers are deleted
- remove the WebView run end-to-end test that exercised the retired path

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 10: 阶段 3 — 分析页 ViewModel(汇总、复核清单、导出)

**Files:**
- Create: `src/InvoiceFlowAI.App/ViewModels/AnalysisViewModel.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/AnalysisOptions.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/AnalysisCopy.cs`
- Create: `src/InvoiceFlowAI.App/ViewModels/AnalysisGroupItem.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/ViewModels/AnalysisViewModelTests.cs`

**Interfaces:**
- Consumes: `IRunResultsQueryService`(Task 2)、`IReportApplicationService`、`IDesktopActionService`、`IRunSession`、`IScopedOperationRunner`、`IAppNavigator`、`INotificationService`。
- Produces: `AnalysisOptions(TimeSpan PollInterval)`,静态 `Production`(3s)与 `Fast`(10ms)。
- Produces: `AnalysisGroupItem(string Key, string Label, int Count)`。
- Produces: `AnalysisViewModel : ObservableObject, IPageViewModel`,成员:
  - 汇总:`SuccessCount`、`RetentionCount`、`ManualCheckCount`、`ProcessingErrorCount`、`TotalCount`、`SummaryText`、`RelativeStatusText`。
  - 横幅:`HasQuotaWarning` / `QuotaMessage`、`HasProcessingErrorWarning` / `ProcessingErrorWarning`、`HasManualReview` / `ManualReviewTitle`。
  - 目录与导出:`OutputPath`、`HasOutputPath`、`ManualCheckPath`、`LastExportFileName`、`HasLastExport`、`IsExporting` / `ExportButtonText`、`PageError` / `HasPageError`。
  - 列表:`GroupedErrors`(`ObservableCollection<AnalysisGroupItem>`)、`HasGroupedErrors`。
  - 命令:`ExportCommand`、`OpenOutputFolderCommand`、`OpenManualReviewFolderCommand`、`StartNewBatchCommand`;方法:`RefreshAsync(CancellationToken)`。
- **关键约束**:`QuotaMessage`、`RunGroupedErrorResult.Label`、`RunErrorInvoiceResult.Reason` 都可能含后端英文或实现细节,一律不直接绑定;分组标签按 key 映射为固定中文,未知 key 统一显示"待处理记录"。

- [ ] **Step 1: 写失败的测试**

创建 `tests/InvoiceFlowAI.App.Tests/ViewModels/AnalysisViewModelTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.App.Tests.TestSupport;
using InvoiceFlowAI.App.ViewModels;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvoiceFlowAI.App.Tests.ViewModels;

public sealed class AnalysisViewModelTests
{
    [Fact]
    public async Task Refresh_derives_counts_from_summary_breakdown_and_groups()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            summary: new Dictionary<string, int> { ["success_count"] = 5, ["manual_check_count"] = 2 },
            groups:
            [
                new RunGroupedErrorResult("manual_review", "Manual review", 2, []),
                new RunGroupedErrorResult("retained_record", "Retained", 1, []),
            ]);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.SuccessCount.Should().Be(5);
        viewModel.ManualCheckCount.Should().Be(2);
        viewModel.RetentionCount.Should().Be(1);
        viewModel.ProcessingErrorCount.Should().Be(0);
        viewModel.TotalCount.Should().Be(8);
        viewModel.HasManualReview.Should().BeTrue();
        viewModel.GroupedErrors.Should().OnlyContain(group => group.Label != "Manual review" && group.Label != "Retained");
    }

    [Fact]
    public async Task Refresh_falls_back_to_breakdown_when_summary_is_missing()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            breakdown: new Dictionary<string, int>
            {
                ["manual_review"] = 3,
                ["retained_record"] = 4,
                ["processing_error"] = 1,
            });
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.ManualCheckCount.Should().Be(3);
        viewModel.RetentionCount.Should().Be(4);
        viewModel.ProcessingErrorCount.Should().Be(1);
        viewModel.HasProcessingErrorWarning.Should().BeTrue();
        viewModel.ProcessingErrorWarning.Should().Be("检测到 1 条真实异常，请通过“导出结果明细”继续排查。");
    }

    [Fact]
    public async Task Refresh_never_renders_the_server_quota_message()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(quota: true, quotaMessage: "Provider quota exhausted.");
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.HasQuotaWarning.Should().BeTrue();
        viewModel.QuotaMessage.Should().Be("大模型额度已用尽，请补充额度后重新运行。");
    }

    [Fact]
    public async Task Refresh_builds_status_copy_for_mixed_results()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            summary: new Dictionary<string, int>
            {
                ["success_count"] = 6,
                ["manual_check_count"] = 2,
                ["retention_count"] = 1,
                ["processing_error_count"] = 0,
            });
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.SummaryText.Should().Be(
            "共整理 9 条结果记录。已整理 6 条成功记录；请优先查看 2 条待人工复核记录，另有 1 条暂存记录可按需导出查看。");
    }

    [Fact]
    public async Task Export_writes_the_report_and_opens_it_through_the_desktop_service()
    {
        var harness = new AnalysisHarness();
        harness.Session.SetRunId("run-9");
        harness.Results.Snapshot = Snapshot(summary: new Dictionary<string, int> { ["success_count"] = 1 });
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.ExportCommand.ExecuteAsync(null);

        harness.Reports.Exported.Should().ContainSingle();
        harness.Reports.Exported[0].RunId.Should().Be("run-9");
        harness.Actions.OpenedFiles.Should().ContainSingle();
        harness.Actions.OpenedFiles[0].ReportPath.Should().Be(@"D:\out\report.xlsx");
        harness.Actions.OpenedFiles[0].ContentHash.Should().Be("hash-1");
        viewModel.LastExportFileName.Should().Be("report.xlsx");
        viewModel.HasLastExport.Should().BeTrue();
    }

    [Fact]
    public async Task Export_failure_shows_product_copy_and_no_open_call()
    {
        var harness = new AnalysisHarness();
        harness.Session.SetRunId("run-9");
        harness.Reports.ExportException = new InvalidOperationException("disk full at C:\\secret\\path");
        var viewModel = harness.CreateViewModel();
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        await viewModel.ExportCommand.ExecuteAsync(null);

        viewModel.HasPageError.Should().BeTrue();
        viewModel.PageError.Should().Be("导出失败，请稍后重试。");
        viewModel.PageError.Should().NotContain("secret");
        harness.Actions.OpenedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_new_batch_clears_the_run_session_and_goes_home()
    {
        var harness = new AnalysisHarness();
        harness.Session.SetRunId("run-9");
        var viewModel = harness.CreateViewModel();

        await viewModel.StartNewBatchCommand.ExecuteAsync(null);

        harness.Session.RunId.Should().BeNull();
        harness.Navigator.Requested.Should().Equal("settings");
    }

    [Fact]
    public async Task Unknown_group_keys_fall_back_to_a_neutral_label()
    {
        var harness = new AnalysisHarness();
        harness.Results.Snapshot = Snapshot(
            groups: [new RunGroupedErrorResult("mystery_code", "Raw English label", 2, [])]);
        var viewModel = harness.CreateViewModel();

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        viewModel.GroupedErrors.Should().ContainSingle();
        viewModel.GroupedErrors[0].Label.Should().Be("待处理记录");
        viewModel.GroupedErrors[0].Count.Should().Be(2);
    }

    private static RunResultsSnapshot Snapshot(
        IReadOnlyDictionary<string, int>? summary = null,
        IReadOnlyDictionary<string, int>? breakdown = null,
        IReadOnlyList<RunGroupedErrorResult>? groups = null,
        bool quota = false,
        string? quotaMessage = null)
        => new(
            new Dictionary<string, int>(),
            [],
            [],
            groups ?? [],
            @"D:\out\manual-review",
            @"D:\out",
            summary ?? new Dictionary<string, int>(),
            null,
            null,
            null,
            breakdown ?? new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            quota,
            quotaMessage,
            null);

    private sealed class AnalysisHarness
    {
        public FakeResultsQueryService Results { get; } = new();
        public FakeReportApplicationService Reports { get; } = new();
        public FakeDesktopActionService Actions { get; } = new();
        public FakeRunSession Session { get; } = new();
        public RecordingNavigator Navigator { get; } = new();
        public NullNotificationService Notifications { get; } = new();

        public AnalysisViewModel CreateViewModel()
        {
            var operations = TestScopedOperationRunner.Create(services =>
            {
                services.AddSingleton<IRunResultsQueryService>(Results);
                services.AddSingleton<IReportApplicationService>(Reports);
                services.AddSingleton<IDesktopActionService>(Actions);
                services.AddSingleton<IDesktopRunService>(new FakeDesktopRunService(
                    new RunContextSnapshot(false, false, false, 0, "run-1", null, null, null, null, null)));
            });

            return new AnalysisViewModel(
                operations,
                Session,
                Navigator,
                Notifications,
                AnalysisOptions.Fast,
                NullLogger<AnalysisViewModel>.Instance);
        }
    }

    private sealed class FakeResultsQueryService : IRunResultsQueryService
    {
        public RunResultsSnapshot Snapshot { get; set; } = new(
            new Dictionary<string, int>(),
            [],
            [],
            [],
            null,
            null,
            new Dictionary<string, int>(),
            null,
            null,
            null,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            false,
            null,
            null);

        public Task<RunResultsSnapshot> GetAsync(string? runId, CancellationToken cancellationToken)
            => Task.FromResult(Snapshot);
    }

    private sealed class FakeReportApplicationService : IReportApplicationService
    {
        public List<ReportExportRequest> Exported { get; } = [];
        public Exception? ExportException { get; set; }

        public Task<ReportExportRpcResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken)
        {
            Exported.Add(request);
            if (ExportException is not null)
            {
                return Task.FromException<ReportExportRpcResult>(ExportException);
            }

            return Task.FromResult(new ReportExportRpcResult(
                request.RunId, @"D:\out\report.xlsx", "hash-1", 3, 1, "v1", false));
        }

        public Task<ReportOpenToken> OpenAsync(ReportOpenRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReportOpenResolution> ResolveTokenAsync(string tokenId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeDesktopActionService : IDesktopActionService
    {
        public List<RunFileOpenRequest> OpenedFiles { get; } = [];
        public List<string?> OpenedFolders { get; } = [];
        public List<string?> OpenedReviewFolders { get; } = [];

        public Task<DesktopActionResult> OpenRunFolderAsync(string? runId, CancellationToken cancellationToken)
        {
            OpenedFolders.Add(runId);
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> OpenManualReviewFolderAsync(string? runId, CancellationToken cancellationToken)
        {
            OpenedReviewFolders.Add(runId);
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }

        public Task<DesktopActionResult> OpenFileAsync(RunFileOpenRequest request, CancellationToken cancellationToken)
        {
            OpenedFiles.Add(request);
            return Task.FromResult(new DesktopActionResult(true, null, null));
        }
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~AnalysisViewModelTests"
```

Expected: 编译失败,`AnalysisViewModel` 等类型不存在。

- [ ] **Step 3: 写文案与选项**

创建 `src/InvoiceFlowAI.App/ViewModels/AnalysisOptions.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

public sealed record AnalysisOptions(TimeSpan PollInterval)
{
    public static AnalysisOptions Production { get; } = new(TimeSpan.FromSeconds(3));

    public static AnalysisOptions Fast { get; } = new(TimeSpan.FromMilliseconds(10));
}
```

创建 `src/InvoiceFlowAI.App/ViewModels/AnalysisGroupItem.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

public sealed record AnalysisGroupItem(string Key, string Label, int Count);
```

创建 `src/InvoiceFlowAI.App/ViewModels/AnalysisCopy.cs`:

```csharp
namespace InvoiceFlowAI.App.ViewModels;

/// <summary>
/// 分析页的固定产品文案与结果摘要算法。摘要算法与现有页面保持一致,
/// 后端分组标签、失败原因与英文配额消息不直接进入 UI。
/// </summary>
public static class AnalysisCopy
{
    public const string Title = "结果分析";
    public const string Eyebrow = "Step 3";
    public const string ResultTitle = "本次处理完成";
    public const string StatusSummaryKicker = "运行结果";
    public const string SuccessLabel = "处理成功";
    public const string RetentionLabel = "暂存记录";
    public const string ManualLabel = "待人工复核";
    public const string SuccessHelper = "归档文件已写入输出目录。";
    public const string SuccessHelperEmpty = "当前还没有成功记录。";
    public const string RetentionHelper = "系统已保全但未纳入成功归档，可按需导出查看。";
    public const string RetentionHelperEmpty = "当前没有暂存记录。";
    public const string ManualHelper = "这是当前需要优先处理的内容。";
    public const string ManualHelperEmpty = "当前没有待人工复核项。";
    public const string QuotaWarning = "大模型额度已用尽，请补充额度后重新运行。";
    public const string ManualReviewHint = "请优先前往待人工复核文件夹处理这些记录。";
    public const string ManualReviewButton = "打开待人工复核";
    public const string OutputEyebrow = "输出目录";
    public const string OutputTitle = "归档结果与暂存记录已写入输出目录";
    public const string OutputTitleEmpty = "当前还没有可打开的输出目录";
    public const string OutputHint = "成功归档文件、暂存记录和导出结果都可以从这里继续查看。";
    public const string OutputHintEmpty = "待本轮生成结果后，可从这里直接打开输出目录。";
    public const string OutputButton = "打开输出目录";
    public const string ExportButton = "导出结果明细";
    public const string ExportingButton = "导出中...";
    public const string StartNewBatchButton = "开始新批次";
    public const string LastExportPrefix = "最近导出:";
    public const string RunUnavailable = "结果记录不可用，请先完成一次处理。";
    public const string LoadFailed = "结果加载失败，请稍后重试。";
    public const string ExportFailed = "导出失败，请稍后重试。";
    public const string ExportOpenedFailed = "报表已导出，但打开文件失败。";
    public const string OpenFailed = "打开失败，请稍后重试。";
    public const string UnknownGroupLabel = "待处理记录";

    public static string MapGroupLabel(string key) => key switch
    {
        "retained_record" => RetentionLabel,
        "manual_review" => ManualLabel,
        "processing_error" => "真实异常",
        _ => UnknownGroupLabel,
    };

    public static string ProcessingErrorWarning(int count)
        => $"检测到 {count} 条真实异常，请通过“导出结果明细”继续排查。";

    public static string ManualReviewTitle(int count) => $"检测到 {count} 条待人工复核记录";

    public static string BuildStatusText(int successCount, int manualCheckCount, int retentionCount, int processingErrorCount)
    {
        var pendingCount = manualCheckCount + retentionCount + processingErrorCount;
        if (successCount > 0 && pendingCount == 0)
        {
            return "主要结果已经整理完成，本轮没有需要额外关注的记录。";
        }

        if (successCount > 0 && pendingCount > 0)
        {
            if (manualCheckCount > 0 && retentionCount > 0 && processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；请先处理 {manualCheckCount} 条待人工复核记录，另有 {retentionCount} 条暂存记录与 {processingErrorCount} 条真实异常需要查看。";
            }

            if (manualCheckCount > 0 && retentionCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；请优先查看 {manualCheckCount} 条待人工复核记录，另有 {retentionCount} 条暂存记录可按需导出查看。";
            }

            if (manualCheckCount > 0 && processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；当前需要关注 {manualCheckCount} 条待人工复核记录和 {processingErrorCount} 条真实异常。";
            }

            if (manualCheckCount > 0)
            {
                return $"已整理 {successCount} 条成功记录；当前需要你关注的是 {manualCheckCount} 条待人工复核记录。";
            }

            if (retentionCount > 0 && processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录，另有 {retentionCount} 条暂存记录和 {processingErrorCount} 条真实异常需要查看。";
            }

            if (retentionCount > 0)
            {
                return $"已整理 {successCount} 条成功记录，另有 {retentionCount} 条暂存记录可按需导出查看。";
            }

            if (processingErrorCount > 0)
            {
                return $"已整理 {successCount} 条成功记录，但仍有 {processingErrorCount} 条真实异常需要排查。";
            }
        }

        if (successCount == 0 && pendingCount > 0)
        {
            if (manualCheckCount > 0)
            {
                return $"本轮暂无可直接归档的记录；请先查看 {manualCheckCount} 条待人工复核记录，其余结果可按需导出查看。";
            }

            if (retentionCount > 0 && processingErrorCount > 0)
            {
                return $"本轮暂无可直接归档的记录，当前有 {retentionCount} 条暂存记录和 {processingErrorCount} 条真实异常。";
            }

            if (retentionCount > 0)
            {
                return "本轮暂无可直接归档的记录，当前结果可通过导出明细进一步查看。";
            }

            if (processingErrorCount > 0)
            {
                return $"本轮暂无可直接归档的记录，当前有 {processingErrorCount} 条真实异常需要排查。";
            }
        }

        return "本轮尚未生成可展示的结果记录。";
    }
}
```

- [ ] **Step 4: 实现 AnalysisViewModel**

创建 `src/InvoiceFlowAI.App/ViewModels/AnalysisViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InvoiceFlowAI.Application.Reports;
using InvoiceFlowAI.Application.Runs;
using InvoiceFlowAI.App.Desktop;
using InvoiceFlowAI.App.Navigation;
using InvoiceFlowAI.App.Services;
using InvoiceFlowAI.Contracts.Reports;
using InvoiceFlowAI.Contracts.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceFlowAI.App.ViewModels;

public sealed partial class AnalysisViewModel : ObservableObject, IPageViewModel
{
    private readonly IScopedOperationRunner _operations;
    private readonly IRunSession _runSession;
    private readonly IAppNavigator _navigator;
    private readonly INotificationService _notifications;
    private readonly AnalysisOptions _options;
    private readonly ILogger<AnalysisViewModel> _logger;

    private CancellationTokenSource? _pollCancellation;
    private string? _runId;
    private bool _started;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _retentionCount;

    [ObservableProperty]
    private int _manualCheckCount;

    [ObservableProperty]
    private int _processingErrorCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _summaryText = AnalysisCopy.BuildStatusText(0, 0, 0, 0);

    [ObservableProperty]
    private string _quotaMessage = string.Empty;

    [ObservableProperty]
    private string _processingErrorWarning = string.Empty;

    [ObservableProperty]
    private string _manualReviewTitle = string.Empty;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _manualCheckPath = string.Empty;

    [ObservableProperty]
    private string _lastExportFileName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _isExporting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _pageError = string.Empty;

    public AnalysisViewModel(
        IScopedOperationRunner operations,
        IRunSession runSession,
        IAppNavigator navigator,
        INotificationService notifications,
        AnalysisOptions options,
        ILogger<AnalysisViewModel> logger)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _runSession = runSession ?? throw new ArgumentNullException(nameof(runSession));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Key => "analysis";

    public string Title => AnalysisCopy.Title;

    public string Eyebrow => AnalysisCopy.Eyebrow;

    public ObservableCollection<AnalysisGroupItem> GroupedErrors { get; } = [];

    public bool HasPageError => !string.IsNullOrWhiteSpace(PageError);

    public bool HasQuotaWarning => !string.IsNullOrWhiteSpace(QuotaMessage);

    public bool HasProcessingErrorWarning => !string.IsNullOrWhiteSpace(ProcessingErrorWarning);

    public bool HasManualReview => ManualCheckCount > 0;

    public bool HasOutputPath => !string.IsNullOrWhiteSpace(OutputPath);

    public bool HasLastExport => !string.IsNullOrWhiteSpace(LastExportFileName);

    public bool HasGroupedErrors => GroupedErrors.Count > 0;

    public string ExportButtonText => IsExporting ? AnalysisCopy.ExportingButton : AnalysisCopy.ExportButton;

    public string OutputTitle => HasOutputPath ? AnalysisCopy.OutputTitle : AnalysisCopy.OutputTitleEmpty;

    public string OutputHint => HasOutputPath ? AnalysisCopy.OutputHint : AnalysisCopy.OutputHintEmpty;

    public string SuccessHelper => SuccessCount > 0 ? AnalysisCopy.SuccessHelper : AnalysisCopy.SuccessHelperEmpty;

    public string RetentionHelper => RetentionCount > 0 ? AnalysisCopy.RetentionHelper : AnalysisCopy.RetentionHelperEmpty;

    public string ManualHelper => ManualCheckCount > 0 ? AnalysisCopy.ManualHelper : AnalysisCopy.ManualHelperEmpty;

    partial void OnPageErrorChanged(string value) => OnPropertyChanged(nameof(HasPageError));

    partial void OnQuotaMessageChanged(string value) => OnPropertyChanged(nameof(HasQuotaWarning));

    partial void OnProcessingErrorWarningChanged(string value) => OnPropertyChanged(nameof(HasProcessingErrorWarning));

    partial void OnOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutputPath));
        OnPropertyChanged(nameof(OutputTitle));
        OnPropertyChanged(nameof(OutputHint));
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastExportFileNameChanged(string value) => OnPropertyChanged(nameof(HasLastExport));

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(ExportButtonText));

    partial void OnSuccessCountChanged(int value) => OnPropertyChanged(nameof(SuccessHelper));

    partial void OnRetentionCountChanged(int value) => OnPropertyChanged(nameof(RetentionHelper));

    partial void OnManualCheckCountChanged(int value)
    {
        OnPropertyChanged(nameof(ManualHelper));
        OnPropertyChanged(nameof(HasManualReview));
        OpenManualReviewFolderCommand.NotifyCanExecuteChanged();
    }

    public Task OnActivatedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _ = PollLoopAsync(_pollCancellation.Token);
        return Task.CompletedTask;
    }

    public Task OnDeactivatedAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _pollCancellation?.Cancel();
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var runId = await ResolveRunIdAsync(cancellationToken).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(runId))
        {
            PageError = AnalysisCopy.RunUnavailable;
            return;
        }

        try
        {
            var snapshot = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IRunResultsQueryService>()
                    .GetAsync(runId, token),
                cancellationToken).ConfigureAwait(true);

            Apply(snapshot);
            PageError = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "加载结果失败。");
            PageError = AnalysisCopy.LoadFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        var runId = _runId ?? _runSession.RunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        IsExporting = true;
        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IReportApplicationService>()
                    .ExportAsync(new ReportExportRequest(runId), token),
                cancellationToken).ConfigureAwait(true);

            LastExportFileName = Path.GetFileName(result.ReportPath);

            var opened = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopActionService>()
                    .OpenFileAsync(
                        new RunFileOpenRequest(runId, ReportPath: result.ReportPath, ContentHash: result.ContentHash),
                        token),
                cancellationToken).ConfigureAwait(true);

            if (!opened.Succeeded)
            {
                _notifications.Show(AnalysisCopy.ExportOpenedFailed, NotificationKind.Warning);
            }

            PageError = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // 页面切换导致的取消不算失败。
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "导出结果失败。");
            PageError = AnalysisCopy.ExportFailed;
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private async Task OpenOutputFolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopActionService>()
                    .OpenRunFolderAsync(_runId, token),
                cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Warning);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开输出目录失败。");
            _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenManualReviewFolder))]
    private async Task OpenManualReviewFolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopActionService>()
                    .OpenManualReviewFolderAsync(_runId, token),
                cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Warning);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开待人工复核目录失败。");
            _notifications.Show(AnalysisCopy.OpenFailed, NotificationKind.Error);
        }
    }

    [RelayCommand]
    private Task StartNewBatchAsync(CancellationToken cancellationToken)
    {
        _runSession.SetRunId(null);
        _runId = null;
        return _navigator.NavigateAsync("settings", cancellationToken);
    }

    private bool CanExport() => !IsExporting && !string.IsNullOrWhiteSpace(_runId ?? _runSession.RunId);

    private bool CanOpenOutputFolder() => HasOutputPath;

    private bool CanOpenManualReviewFolder() => ManualCheckCount > 0;

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "刷新结果失败。");
            }

            try
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<string?> ResolveRunIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_runId))
        {
            return _runId;
        }

        var candidate = _runSession.RunId;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var context = await _operations.RunAsync(
                (provider, token) => provider.GetRequiredService<IDesktopRunService>().GetContextAsync(token),
                cancellationToken).ConfigureAwait(true);
            candidate = context.RunId;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        _runId = candidate;
        _runSession.SetRunId(candidate);
        ExportCommand.NotifyCanExecuteChanged();
        return candidate;
    }

    private void Apply(RunResultsSnapshot snapshot)
    {
        var success = Count(snapshot.Summary, "success_count", snapshot.SuccessInvoices.Count);

        var manualGroupCount = snapshot.GroupedErrorInvoices
            .Where(group => string.Equals(group.Key, "manual_review", StringComparison.Ordinal))
            .Sum(group => group.Count);
        var manual = Count(snapshot.Summary, "manual_check_count",
            Count(snapshot.ResultBreakdown, "manual_review", manualGroupCount));
        var retention = Count(snapshot.Summary, "retention_count",
            Count(snapshot.ResultBreakdown, "retained_record", 0));
        var processing = Count(snapshot.Summary, "processing_error_count",
            Count(snapshot.ResultBreakdown, "processing_error", 0));

        var totalErrors = snapshot.GroupedErrorInvoices.Sum(group => group.Count);
        var pending = manual + retention + processing;
        if (pending == 0)
        {
            pending = Count(snapshot.Summary, "error_count", totalErrors);
        }

        SuccessCount = success;
        ManualCheckCount = manual;
        RetentionCount = retention;
        ProcessingErrorCount = processing;
        TotalCount = success + pending;
        SummaryText = $"共整理 {TotalCount} 条结果记录。{AnalysisCopy.BuildStatusText(success, manual, retention, processing)}";
        QuotaMessage = snapshot.QuotaExhausted ? AnalysisCopy.QuotaWarning : string.Empty;
        ProcessingErrorWarning = processing > 0 ? AnalysisCopy.ProcessingErrorWarning(processing) : string.Empty;
        ManualReviewTitle = AnalysisCopy.ManualReviewTitle(manual);
        OutputPath = snapshot.OutputPath ?? ParentFolder(snapshot.ManualCheckPath) ?? string.Empty;
        ManualCheckPath = snapshot.ManualCheckPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(snapshot.LastExportPath))
        {
            LastExportFileName = Path.GetFileName(snapshot.LastExportPath);
        }

        GroupedErrors.Clear();
        foreach (var group in snapshot.GroupedErrorInvoices.Where(group => group.Count > 0))
        {
            GroupedErrors.Add(new AnalysisGroupItem(
                group.Key,
                AnalysisCopy.MapGroupLabel(group.Key),
                group.Count));
        }

        OnPropertyChanged(nameof(HasGroupedErrors));
    }

    private static int Count(IReadOnlyDictionary<string, int> values, string key, int fallback)
        => values.TryGetValue(key, out var value) && value > 0 ? value : fallback;

    private static string? ParentFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
```

- [ ] **Step 5: 运行测试**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~AnalysisViewModelTests"
```

Expected: PASS(8 个用例)。

- [ ] **Step 6: 提交**

```bash
git add src/InvoiceFlowAI.App/ViewModels tests/InvoiceFlowAI.App.Tests/ViewModels
git commit -m "feat(analysis): add the native analysis view model

- derive success, retention, manual-review and processing-error counts from the snapshot
- port the result status summary into product copy
- export the report, open it through the desktop action service, and start a new batch

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 11: 阶段 3 — 分析页视图(AtomUI)与 report.* RPC 退役

**Files:**
- Create: `src/InvoiceFlowAI.App/Views/AnalysisPageView.axaml`
- Create: `src/InvoiceFlowAI.App/Views/AnalysisPageView.axaml.cs`
- Modify: `src/InvoiceFlowAI.App/App.axaml`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.App/Rpc/AppRpcComposition.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/RunAndReportRpcHandlers.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/RunFolderOpenRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/ManualReviewFolderOpenRpcHandler.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/RunFileOpenRpcHandler.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Views/AnalysisPageViewTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/RunAndReportRpcHandlersTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/DesktopActionRpcHandlerTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`

**Interfaces:**
- Consumes: `AnalysisViewModel`(Task 10)、`AtomUiHeadlessHost`(Task 1)。
- Produces: `AnalysisPageView` 通过 `App.axaml` 的 `DataTemplate` 自动匹配 `AnalysisViewModel`。
- 说明:`AnalysisViewModel` 依赖的 `IDesktopActionService`、`IRunResultsQueryService`、`IReportApplicationService` 都是 Scoped,VM 内通过 `IScopedOperationRunner` 在每次操作的作用域内解析,构造函数不持有它们,因此 VM 本身可安全注册为 Singleton。

- [ ] **Step 1: 写失败的视图冒烟测试**

创建 `tests/InvoiceFlowAI.App.Tests/Views/AnalysisPageViewTests.cs`:

```csharp
using Avalonia.VisualTree;
using AtomUI.Desktop.Controls;
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.Views;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class AnalysisPageViewTests
{
    [Fact]
    public void Analysis_page_view_builds_summary_review_and_output_sections()
    {
        var result = AtomUiHeadlessHost.Run(() =>
        {
            var view = new AnalysisPageView();
            view.Measure(new Avalonia.Size(1180, 760));
            return (
                Cards: view.GetVisualDescendants().OfType<Card>().Count(),
                Buttons: view.GetVisualDescendants().OfType<AtomUI.Desktop.Controls.Button>().Count());
        });

        result.Cards.Should().BeGreaterThanOrEqualTo(6);
        result.Buttons.Should().BeGreaterThanOrEqualTo(3);
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~AnalysisPageViewTests"
```

Expected: 编译失败,`AnalysisPageView` 不存在。

- [ ] **Step 3: 写分析页视图**

创建 `src/InvoiceFlowAI.App/Views/AnalysisPageView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:atom="https://atomui.net"
             xmlns:vm="using:InvoiceFlowAI.App.ViewModels"
             x:Class="InvoiceFlowAI.App.Views.AnalysisPageView"
             x:DataType="vm:AnalysisViewModel">
  <ScrollViewer>
    <StackPanel Margin="28,20,28,28" Spacing="18">
      <Grid ColumnDefinitions="*,Auto">
        <StackPanel Grid.Column="0" Spacing="6">
          <TextBlock Text="{Binding Eyebrow}" FontSize="12" Opacity="0.55" />
          <TextBlock Text="{Binding Title}" FontSize="26" FontWeight="SemiBold" />
        </StackPanel>
        <StackPanel Grid.Column="1"
                    Orientation="Horizontal"
                    Spacing="10"
                    VerticalAlignment="Center">
          <atom:Button ButtonType="Primary"
                       Content="{Binding ExportButtonText}"
                       IsLoading="{Binding IsExporting}"
                       Command="{Binding ExportCommand}" />
          <atom:Button ButtonType="Link"
                       Content="开始新批次"
                       Command="{Binding StartNewBatchCommand}" />
        </StackPanel>
      </Grid>

      <atom:Alert Type="Error"
                  IsShowIcon="True"
                  IsClosable="False"
                  Message="{Binding PageError}"
                  IsVisible="{Binding HasPageError}" />

      <atom:Card StyleVariant="Outlined">
        <StackPanel Spacing="6">
          <TextBlock Text="运行结果" FontSize="12" Opacity="0.55" />
          <TextBlock Text="{Binding SummaryText}" FontSize="15" TextWrapping="Wrap" />
        </StackPanel>
      </atom:Card>

      <atom:Alert Type="Warning"
                  IsShowIcon="True"
                  IsClosable="False"
                  Message="{Binding QuotaMessage}"
                  IsVisible="{Binding HasQuotaWarning}" />

      <atom:Alert Type="Warning"
                  IsShowIcon="True"
                  IsClosable="False"
                  Message="{Binding ProcessingErrorWarning}"
                  IsVisible="{Binding HasProcessingErrorWarning}" />

      <Grid ColumnDefinitions="*,*,*" ColumnSpacing="16">
        <atom:Card Grid.Column="0" StyleVariant="Outlined">
          <StackPanel Spacing="6">
            <TextBlock Text="处理成功" FontSize="12" Opacity="0.55" />
            <TextBlock Text="{Binding SuccessCount}" FontSize="28" FontWeight="SemiBold" />
            <TextBlock Text="{Binding SuccessHelper}" FontSize="12" Opacity="0.55" TextWrapping="Wrap" />
          </StackPanel>
        </atom:Card>

        <atom:Card Grid.Column="1" StyleVariant="Outlined">
          <StackPanel Spacing="6">
            <TextBlock Text="暂存记录" FontSize="12" Opacity="0.55" />
            <TextBlock Text="{Binding RetentionCount}" FontSize="28" FontWeight="SemiBold" />
            <TextBlock Text="{Binding RetentionHelper}" FontSize="12" Opacity="0.55" TextWrapping="Wrap" />
          </StackPanel>
        </atom:Card>

        <atom:Card Grid.Column="2" StyleVariant="Outlined">
          <StackPanel Spacing="6">
            <TextBlock Text="待人工复核" FontSize="12" Opacity="0.55" />
            <TextBlock Text="{Binding ManualCheckCount}" FontSize="28" FontWeight="SemiBold" />
            <TextBlock Text="{Binding ManualHelper}" FontSize="12" Opacity="0.55" TextWrapping="Wrap" />
          </StackPanel>
        </atom:Card>
      </Grid>

      <atom:Card StyleVariant="Outlined" IsVisible="{Binding HasManualReview}">
        <Grid ColumnDefinitions="*,Auto">
          <StackPanel Grid.Column="0" Spacing="6">
            <TextBlock Text="{Binding ManualReviewTitle}" FontWeight="SemiBold" TextWrapping="Wrap" />
            <TextBlock Text="请优先前往待人工复核文件夹处理这些记录。"
                       FontSize="12" Opacity="0.55" TextWrapping="Wrap" />
          </StackPanel>
          <atom:Button Grid.Column="1"
                       ButtonType="Default"
                       VerticalAlignment="Center"
                       Content="打开待人工复核"
                       Command="{Binding OpenManualReviewFolderCommand}" />
        </Grid>
      </atom:Card>

      <atom:Card StyleVariant="Outlined">
        <StackPanel Spacing="10">
          <TextBlock Text="{Binding OutputTitle}" FontWeight="SemiBold" TextWrapping="Wrap" />
          <TextBlock Text="{Binding OutputHint}" FontSize="12" Opacity="0.55" TextWrapping="Wrap" />
          <atom:Button ButtonType="Default"
                       HorizontalAlignment="Left"
                       Content="打开输出目录"
                       Command="{Binding OpenOutputFolderCommand}" />
        </StackPanel>
      </atom:Card>

      <atom:Card Header="结果分布" StyleVariant="Outlined" IsVisible="{Binding HasGroupedErrors}">
        <ItemsControl ItemsSource="{Binding GroupedErrors}">
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="vm:AnalysisGroupItem">
              <Grid ColumnDefinitions="*,Auto" Margin="0,6,0,0">
                <TextBlock Grid.Column="0" Text="{Binding Label}" />
                <TextBlock Grid.Column="1" Text="{Binding Count}" FontWeight="SemiBold" />
              </Grid>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </atom:Card>
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

创建 `src/InvoiceFlowAI.App/Views/AnalysisPageView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace InvoiceFlowAI.App.Views;

public partial class AnalysisPageView : UserControl
{
    public AnalysisPageView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4: 接 DataTemplate 与 DI**

`App.axaml` 的 `Application.DataTemplates` 追加:

```xml
    <DataTemplate DataType="{x:Type vm:AnalysisViewModel}">
      <views:AnalysisPageView />
    </DataTemplate>
```

`AppServiceProviderFactory.cs`:

- 删除 5 行 handler 注册:`RunResultsGetRpcHandler`、`ReportExportRpcHandler`、`RunFolderOpenRpcHandler`、`ManualReviewFolderOpenRpcHandler`、`RunFileOpenRpcHandler`。
- 把分析页的 `WebPageViewModel` 注册替换为:

```csharp
        services.AddSingleton(AnalysisOptions.Production);
        services.AddSingleton<AnalysisViewModel>(provider => new AnalysisViewModel(
            provider.GetRequiredService<IScopedOperationRunner>(),
            provider.GetRequiredService<IRunSession>(),
            provider.GetRequiredService<IAppNavigator>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<AnalysisOptions>(),
            provider.GetRequiredService<ILogger<AnalysisViewModel>>()));
        services.AddSingleton<IPageViewModel>(provider => provider.GetRequiredService<AnalysisViewModel>());
```

补 `using InvoiceFlowAI.App.ViewModels;`(已存在则跳过)。

- [ ] **Step 5: 退役 report.* 与文件夹/文件打开 RPC**

`AppRpcComposition.cs`:从 `V1Methods` 删除 `run.results.get`、`run.report.export`、`run.folder.open`、`run.manual-review.open`、`run.file.open`,并删除对应 5 行 `dispatcher.RegisterHandler(...)`。

删除以下文件(此时已无任何 handler 类):

- `src/InvoiceFlowAI.App/Rpc/RunAndReportRpcHandlers.cs`
- `src/InvoiceFlowAI.App/Rpc/RunFolderOpenRpcHandler.cs`
- `src/InvoiceFlowAI.App/Rpc/ManualReviewFolderOpenRpcHandler.cs`
- `src/InvoiceFlowAI.App/Rpc/RunFileOpenRpcHandler.cs`

删除测试:

- `tests/InvoiceFlowAI.App.Tests/Rpc/RunAndReportRpcHandlersTests.cs`
- `tests/InvoiceFlowAI.App.Tests/Rpc/DesktopActionRpcHandlerTests.cs`

- [ ] **Step 6: 更新 RPC 组合测试**

`AppRpcCompositionTests.cs`:期望方法列表与 `hello` 的 `registeredMethods` 断言都改为只允许 hello:

```csharp
        dispatcher.RegisteredMethods.Should().OnlyContain(method => method == RpcDispatcher.HelloMethod);
```

```csharp
        hello.Result!.Value.GetProperty("registeredMethods").EnumerateArray()
            .Select(x => x.GetString()).Should().OnlyContain(method => method == RpcDispatcher.HelloMethod);
```

`AppServiceProviderFactoryTests.cs`:删除 `ReportExportRpcHandler`、`RunResultsGetRpcHandler` 的解析断言,并补充:

```csharp
        provider.GetRequiredService<AnalysisViewModel>().Should().BeOfType<AnalysisViewModel>();
        provider.GetServices<IPageViewModel>().Should().Contain(page => page is AnalysisViewModel);
```

- [ ] **Step 7: 运行测试与构建**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj
dotnet build src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj -c Debug
```

Expected: 全绿。若仍有测试引用已删除的 handler 类型,说明漏删,按编译错误逐项清理。

- [ ] **Step 8: 手动验收**

启动应用完成一轮任务 → 分析页显示三张汇总卡、状态摘要、待人工复核卡片与输出目录卡片;点击"导出结果明细"生成报表并自动打开;点击"打开输出目录"打开资源管理器;点击"开始新批次"回到设置页且运行会话被清空。窗口内不再出现任何 WebView 页面。

- [ ] **Step 9: 提交**

```bash
git add -A src/InvoiceFlowAI.App tests/InvoiceFlowAI.App.Tests
git commit -m "feat(analysis): render the analysis page natively and retire report.* RPC

- AnalysisPageView with result summary, grouped distribution, review and output actions
- run.results.get, run.report.export, run.folder.open, run.manual-review.open and run.file.open are deleted
- every remaining WebView page now has a native replacement

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 12: 移除 WebView、RPC 层、SPA 与过渡桥

**Files:**
- Modify: `src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`
- Modify: `src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`
- Modify: `src/InvoiceFlowAI.App/MainWindow.axaml.cs`
- Move: `src/InvoiceFlowAI.App/Rpc/ScopedDesktopRunExecutorLeaseFactory.cs` → `src/InvoiceFlowAI.App/Desktop/ScopedDesktopRunExecutorLeaseFactory.cs`
- Delete: `src/InvoiceFlowAI.App/Rpc/`(其余全部文件)
- Delete: `src/InvoiceFlowAI.App/WebView/`
- Delete: `src/InvoiceFlowAI.App/Events/RunEventPublisherFanOut.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Events/RunEventPublisherFanOutTests.cs`
- Delete: `src/InvoiceFlowAI.App/Navigation/WebPageViewModel.cs`
- Delete: `src/InvoiceFlowAI.App/Views/EmbedWebViewHost.cs`
- Delete: `templates/`
- Delete: `build/compile-webview-page.mjs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/RpcDispatcherTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRpcBridgeTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRunEventPublisherTests.cs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/rpc_client.test.mjs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs`
- Delete: `tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs`
- Delete: `tests/InvoiceFlowAI.App.Tests/WebView/WebViewAssetVerifierTests.cs`
- Move: `tests/InvoiceFlowAI.App.Tests/Rpc/ScopedDesktopRunExecutorLeaseFactoryTests.cs` → `tests/InvoiceFlowAI.App.Tests/Desktop/ScopedDesktopRunExecutorLeaseFactoryTests.cs`
- Modify: `tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`
- Test: `tests/InvoiceFlowAI.App.Tests/Views/MainWindowSmokeTests.cs`

**Interfaces:**
- Consumes: 三个原生页面(任务 7/9/11)、`NativeRunEventPublisher`(Task 3)。
- Produces: `IRunEventPublisher` 唯一实现为 `NativeRunEventPublisher`;`MainWindow` 构造函数收缩为 `(ShellViewModel, AvaloniaMainWindowAccessor, IRunShutdownService, ILogger<MainWindow>)`。
- 说明:`Directory.Packages.props` 的 `Avalonia.Controls.WebView` **PackageVersion 行保留**(`build/release-manifest.ps1` 仍读取它生成发布元数据),只删除 App 项目的 PackageReference。

- [ ] **Step 1: 写最终壳冒烟测试**

创建 `tests/InvoiceFlowAI.App.Tests/Views/MainWindowSmokeTests.cs`:

```csharp
using FluentAssertions;
using InvoiceFlowAI.App.Tests.Headless;
using InvoiceFlowAI.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvoiceFlowAI.App.Tests.Views;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void Main_window_builds_with_three_native_pages_and_no_webview()
    {
        var appDataDirectory = Path.Combine(Path.GetTempPath(), $"invoiceflow-window-{Guid.NewGuid():N}");
        try
        {
            var pageKeys = AtomUiHeadlessHost.Run(() =>
            {
                using var provider = AppServiceProviderFactory.Create(appDataDirectory);
                var window = provider.GetRequiredService<MainWindow>();
                var shell = window.DataContext.Should().BeOfType<ShellViewModel>().Subject;
                return shell.Pages.Select(page => page.Key).ToArray();
            });

            pageKeys.Should().Equal("settings", "processing", "analysis");
        }
        finally
        {
            if (Directory.Exists(appDataDirectory))
            {
                Directory.Delete(appDataDirectory, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 2: 运行测试,确认失败**

```bash
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj --filter "FullyQualifiedName~MainWindowSmokeTests"
```

Expected: 编译失败或断言失败(`EmbedWebViewHost`/`WebPageViewModel` 仍在,页面顺序依赖过渡注册)。

- [ ] **Step 3: 移除 App 项目的 WebView 依赖与资产拷贝**

`src/InvoiceFlowAI.App/InvoiceFlowAI.App.csproj`:

- 删除 `<PackageReference Include="Avalonia.Controls.WebView" />`。
- 删除整个 `ItemGroup`,其中包含 8 条 `<Content Include="..\..\templates\...">` 资产拷贝。

`Directory.Packages.props`:保留 `Avalonia.Controls.WebView` 的 `PackageVersion` 行,在其上方加注释:

```xml
    <!-- Kept for build/release-manifest.ps1 metadata; the app no longer references the package. -->
```

- [ ] **Step 4: 删除 WebView、RPC 与 SPA**

先移出仍需保留的租约工厂(必须在 `git rm -r src/InvoiceFlowAI.App/Rpc` 之前执行):

```bash
mkdir -p src/InvoiceFlowAI.App/Desktop tests/InvoiceFlowAI.App.Tests/Desktop
git mv src/InvoiceFlowAI.App/Rpc/ScopedDesktopRunExecutorLeaseFactory.cs src/InvoiceFlowAI.App/Desktop/ScopedDesktopRunExecutorLeaseFactory.cs
git mv tests/InvoiceFlowAI.App.Tests/Rpc/ScopedDesktopRunExecutorLeaseFactoryTests.cs tests/InvoiceFlowAI.App.Tests/Desktop/ScopedDesktopRunExecutorLeaseFactoryTests.cs
```

把该源文件的命名空间改为 `InvoiceFlowAI.App.Desktop`;测试文件命名空间改为 `InvoiceFlowAI.App.Tests.Desktop`,并把 `using InvoiceFlowAI.App.Rpc;` 改为 `using InvoiceFlowAI.App.Desktop;`。

再删除其余文件:

```bash
git rm -r src/InvoiceFlowAI.App/Rpc src/InvoiceFlowAI.App/WebView templates
git rm src/InvoiceFlowAI.App/Events/RunEventPublisherFanOut.cs
git rm tests/InvoiceFlowAI.App.Tests/Events/RunEventPublisherFanOutTests.cs
git rm src/InvoiceFlowAI.App/Navigation/WebPageViewModel.cs
git rm src/InvoiceFlowAI.App/Views/EmbedWebViewHost.cs
git rm build/compile-webview-page.mjs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/AppRpcCompositionTests.cs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/RpcDispatcherTests.cs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRpcBridgeTests.cs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/WebViewRunEventPublisherTests.cs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/rpc_client.test.mjs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/run_page_rpc.test.mjs
git rm tests/InvoiceFlowAI.App.Tests/Rpc/settings_page_rpc.test.mjs
git rm tests/InvoiceFlowAI.App.Tests/WebView/WebViewAssetVerifierTests.cs
```

- [ ] **Step 5: 解除 MainWindow 的嵌入桥**

`src/InvoiceFlowAI.App/MainWindow.axaml.cs`:

- 删除字段 `private readonly EmbedWebViewHost _embedHost;`。
- 构造函数签名改为:

```csharp
    public MainWindow(
        ShellViewModel shell,
        AvaloniaMainWindowAccessor windowAccessor,
        IRunShutdownService shutdown,
        ILogger<MainWindow> logger)
```

- 删除 `_embedHost = new EmbedWebViewHost(...) { IsVisible = false };` 与 `ContentHost.Children.Add(_embedHost);`。
- `UpdateContent` 方法改为:

```csharp
    private void UpdateContent()
    {
        try
        {
            var page = _shell.CurrentPage;
            NativeContent.Content = page;
            SyncSelection(page);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "页面切换失败。");
        }
    }
```

- `OnClosed` 中删除 `_embedHost.Dispose();`。

- [ ] **Step 6: 事件发布器直连原生总线**

`src/InvoiceFlowAI.App/AppServiceProviderFactory.cs`:

- 删除 `services.AddSingleton<WebViewRunEventPublisher>();`。
- 删除 Task 5 添加的 `services.AddSingleton<IRpcDispatcher>(provider => AppRpcComposition.CreateDispatcher(...));`。
- 删除 `services.AddSingleton<RunEventPublisherFanOut>(...);` 与 `services.AddSingleton<IRunEventPublisher>(provider => provider.GetRequiredService<RunEventPublisherFanOut>());`(若写在 Task 3 的注册块中,按实际代码删除)。
- 增加单一注册:

```csharp
        services.AddSingleton<IRunEventPublisher>(provider => provider.GetRequiredService<NativeRunEventPublisher>());
```

- 删除 `using InvoiceFlowAI.App.Rpc;`(如已无引用)。

- [ ] **Step 7: 更新既有测试**

`tests/InvoiceFlowAI.App.Tests/Rpc/AppServiceProviderFactoryTests.cs`:

- `IRunEventPublisher` 断言改为:

```csharp
                provider.GetRequiredService<IRunEventPublisher>()
                    .Should().BeSameAs(provider.GetRequiredService<NativeRunEventPublisher>());
```

- 补 `using InvoiceFlowAI.App.Events;` 与 `using InvoiceFlowAI.App.Desktop;`(租约工厂已移入 `Desktop` 命名空间)。
- 删除 `using InvoiceFlowAI.App.Rpc;` 与所有已删除 handler 的 `GetRequiredService<...RpcHandler>()` 断言(Task 7/9/11 已逐步删除,此处只清理残留)。

- [ ] **Step 8: 全量构建与测试**

```bash
dotnet build InvoiceFlowAI.sln -c Debug
dotnet test tests/InvoiceFlowAI.App.Tests/InvoiceFlowAI.App.Tests.csproj
```

Expected: 0 errors;App.Tests 全绿。若 `dotnet build` 报 `AtomUI` 或 WebView 资产缺失,按错误信息清理 csproj 残留项。

- [ ] **Step 9: 手动验收**

启动应用:侧边栏三个页面全部原生渲染;处理任务期间进度、日志、停止与完成跳转正常;导出与打开目录正常;关闭窗口无 WebView 进程残留。

- [ ] **Step 10: 提交**

```bash
git add -A
git commit -m "refactor(app): remove the WebView host, RPC bridge and SPA bundle

- delete Avalonia.Controls.WebView reference, Rpc/, WebView/, templates/ and the JS tests
- MainWindow renders native pages only
- NativeRunEventPublisher becomes the single IRunEventPublisher

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 13: 合规收尾(CI LGPL 护栏、通知文件、README 事实校正、人工清单)

**Files:**
- Create: `build/verify-lgpl-packaging.ps1`
- Modify: `.github/workflows/windows.yml`
- Modify: `licenses/THIRD-PARTY-NOTICES.txt`
- Modify: `README.md` / `README.en.md`(仅在存在失实陈述时)
- Create: `docs/qa/avalonia-native-ui-manual-checklist.md`

**Interfaces:**
- Consumes: Task 1 的 AtomUI 依赖与 `licenses/LGPL-3.0.txt`、Task 12 的最终发布产物布局。
- Produces: CI 在每次 publish 后断言 LGPL 打包不变量;发布前人工核对清单。

- [ ] **Step 1: 写 LGPL 打包校验脚本**

创建 `build/verify-lgpl-packaging.ps1`:

```powershell
[CmdletBinding()]
param(
    [string]$PublishRoot = 'publish/win-x64'
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish = if ([System.IO.Path]::IsPathRooted($PublishRoot)) {
    [System.IO.Path]::GetFullPath($PublishRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $root $PublishRoot))
}

if (-not (Test-Path -LiteralPath $publish -PathType Container)) {
    throw "Publish root '$publish' does not exist."
}

$atomDlls = @(Get-ChildItem -LiteralPath $publish -Filter 'AtomUI*.dll' -File -Recurse)
if ($atomDlls.Count -lt 2) {
    throw "Expected AtomUI assemblies as separate DLLs, found $($atomDlls.Count)."
}

$allFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
if ($allFiles.Count -lt 20) {
    throw "Publish output has only $($allFiles.Count) files; refusing a single-file-style bundle."
}

$projectFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.csproj' -File -Recurse)
$singleFileSettings = $projectFiles | Select-String -Pattern 'PublishSingleFile|PublishTrimmed'
if ($singleFileSettings) {
    throw "Single-file or trimmed publishing is not allowed: $($singleFileSettings -join '; ')"
}

$notices = Join-Path $publish 'licenses/THIRD-PARTY-NOTICES.txt'
$license = Join-Path $publish 'licenses/LGPL-3.0.txt'
if (-not (Test-Path -LiteralPath $notices -PathType Leaf)) {
    throw 'THIRD-PARTY-NOTICES.txt is missing from the publish output.'
}
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
    throw 'LGPL-3.0.txt is missing from the publish output.'
}
if (-not (Select-String -LiteralPath $notices -Pattern 'AtomUI' -Quiet)) {
    throw 'THIRD-PARTY-NOTICES.txt does not mention AtomUI.'
}
if (-not (Select-String -LiteralPath $license -Pattern 'GNU LESSER GENERAL PUBLIC LICENSE' -Quiet)) {
    throw 'LGPL-3.0.txt does not contain the LGPL text.'
}

Write-Host "LGPL packaging checks passed: $($atomDlls.Count) AtomUI assemblies, $($allFiles.Count) files."
```

- [ ] **Step 2: 在 CI 中执行护栏**

`.github/workflows/windows.yml` 在 `Publish self-contained win-x64 app, worker, model, and manifests` 步骤之后插入:

```yaml
      - name: Verify LGPL packaging invariants
        shell: pwsh
        run: pwsh -File build/verify-lgpl-packaging.ps1 -PublishRoot publish/win-x64
```

- [ ] **Step 3: 通知文件最终化**

`licenses/THIRD-PARTY-NOTICES.txt`:

- 删除 `Avalonia.Controls.WebView 12.1.0` 整段(包已不再随应用分发)。
- 确认 AtomUI 段落存在且写明 LGPL-3.0 与 `licenses\LGPL-3.0.txt`。
- 确认 Avalonia 版本为 12.1.4。

- [ ] **Step 4: README 事实核对(保守改动)**

先检索:

```bash
rg -n "WebView|webview|templates/|RPC|Avalonia" README.md README.en.md
```

- 若检索无结果(当前仓库即如此),**不要改动 README**。
- 若存在"基于 WebView/网页界面"之类失实描述,只做最小事实替换(如改为"Avalonia 原生界面"),不重排结构、不改语气、不动图表与 FAQ。

- [ ] **Step 5: 写发布前人工核对清单**

创建 `docs/qa/avalonia-native-ui-manual-checklist.md`:

```markdown
# Avalonia 原生 UI 发布前人工核对清单

适用:替换 WebView 后的首次及后续发布。逐项勾选,任一失败即阻断发布。

## 1. 启动与壳

- [ ] 应用启动进入原生窗口,无白屏、无 WebView 进程。
- [ ] 侧边栏三个入口(启动配置 / 处理中心 / 结果分析)均可切换,选中态与内容一致。
- [ ] 暗色主题下文字、边框、按钮对比度正常,中文无乱码。
- [ ] 最小化 / 最大化 / 关闭按钮可用;运行中关闭会弹出确认,确认后安全停止并退出。

## 2. 启动配置

- [ ] 邮箱、授权码、输出目录、公司名称可填写并保存,重启后仍在。
- [ ] 授权码与 API Key 默认隐藏,可切换显示;保存后不出现明文回显。
- [ ] 测试邮箱授权码与测试 API Key 分别给出明确成功/失败提示。
- [ ] 目录选择器可打开并写回所选路径;取消不报错。
- [ ] 受控复跑时输出目录与日期范围锁定并显示提示。
- [ ] 点击"开始提取"进入处理中心。

## 3. 处理中心

- [ ] 状态文案为中文产品语言,不出现 `extract-documents` 等内部阶段码。
- [ ] 进度条、邮件/票据/错误统计随任务推进更新。
- [ ] 日志列表自动滚动且最多保留 200 条,不含后端原始英文消息。
- [ ] 点击停止后按钮禁用并显示"已收到安全停止指令,正在收尾..."。
- [ ] 配额耗尽与邮箱读取失败以横幅提示,文案为固定中文。
- [ ] 完成后自动进入结果分析页,且只跳转一次。

## 4. 结果分析

- [ ] 三张汇总卡(处理成功 / 暂存记录 / 待人工复核)数字正确。
- [ ] 状态摘要为中文,不出现后端分组英文标签。
- [ ] "打开待人工复核"仅在有待复核记录时可用,点击打开正确目录。
- [ ] "打开输出目录"打开本轮输出根目录。
- [ ] "导出结果明细"生成 Excel 并自动打开;失败时显示"导出失败,请稍后重试。"。
- [ ] "开始新批次"回到启动配置且不残留上一轮运行状态。

## 5. 文案泄漏审计

- [ ] 全量搜索界面可见文本,不含:`保留原有`、`只重做`、`仅重构视觉`、`设计稿`、`实现`、`备注`、`TODO`、`mockup`、`phase`、`Apple`。
- [ ] 页面标题、副标题、空状态、对话框副标题均为产品语言,无实现说明。

## 6. 发布产物

- [ ] `licenses/THIRD-PARTY-NOTICES.txt` 与 `licenses/LGPL-3.0.txt` 存在于安装目录。
- [ ] 安装目录内 `AtomUI*.dll` 为独立文件,未被合并进主程序。
- [ ] `build/verify-lgpl-packaging.ps1` 在发布目录上通过。
```

- [ ] **Step 6: 本地运行护栏脚本**

先用发布脚本执行一次完整发布(Task 12 后 WebView 资产已移除;`licenses/` 下的文本由该脚本复制):

```bash
pwsh -File build/windows/publish_dotnet_app.ps1 -Configuration Release -PublishRoot publish/win-x64-check
pwsh -File build/verify-lgpl-packaging.ps1 -PublishRoot publish/win-x64-check
```

Expected: 输出 `LGPL packaging checks passed: ...`。随后删除 `publish/win-x64-check`。

- [ ] **Step 7: 提交**

```bash
git add build/verify-lgpl-packaging.ps1 .github/workflows/windows.yml licenses/THIRD-PARTY-NOTICES.txt docs/qa/avalonia-native-ui-manual-checklist.md
git commit -m "chore(release): guard LGPL packaging and document the native UI checklist

- assert AtomUI ships as separate assemblies with notices and LGPL text
- record the manual acceptance checklist for the native pages

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```
