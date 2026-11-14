using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassroomKit.Agent.Desktop;

/// <summary>PageKey == ConverterParameter 时显示，其余塌陷。</summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
