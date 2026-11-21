using System.Globalization;
using Avalonia.Data.Converters;
using RelaxKonOS.App.TaskManager.ViewModels;

namespace RelaxKonOS.App.TaskManager.Converters;

/// <summary>TaskManagerTab + ConverterParameter("Performance"/"Processes") → bool 可见性。</summary>
public sealed class TabVisibilityConverter : IValueConverter
{
    public static readonly TabVisibilityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TaskManagerTab tab && parameter is string s
            && Enum.TryParse<TaskManagerTab>(s, ignoreCase: true, out var target))
            return tab == target;
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空字符串 → true（用于结束任务反馈条的可见性）。</summary>
public sealed class StringNonEmptyVisibilityConverter : IValueConverter
{
    public static readonly StringNonEmptyVisibilityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
