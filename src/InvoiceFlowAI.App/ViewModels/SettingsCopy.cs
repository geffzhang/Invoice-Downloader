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
