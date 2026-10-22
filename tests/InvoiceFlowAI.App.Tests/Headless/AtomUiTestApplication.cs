using AtomUI;
using InvoiceFlowAI.App.Bootstrap;

namespace InvoiceFlowAI.App.Tests.Headless;

/// <summary>
/// Test-only Application. RegisterServices 必须调用 ApplicationStartup.Attach,
/// 否则 AtomUI 的 AfterApplicationSetup 校验会抛异常。
/// </summary>
public sealed partial class AtomUiTestApplication : Avalonia.Application
{
    public override void RegisterServices()
    {
        base.RegisterServices();
        ApplicationStartup.Attach(this);
    }
}
