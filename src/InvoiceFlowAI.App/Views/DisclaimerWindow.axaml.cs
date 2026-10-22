using Avalonia.Interactivity;

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
        SoftwareList.ItemsSource = SoftwareItems;
        RiskList.ItemsSource = RiskItems;
    }

    private void OnAcknowledgeClick(object? sender, RoutedEventArgs e) => Close();
}
