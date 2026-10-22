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
