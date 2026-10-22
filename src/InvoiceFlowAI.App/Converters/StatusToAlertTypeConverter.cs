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
