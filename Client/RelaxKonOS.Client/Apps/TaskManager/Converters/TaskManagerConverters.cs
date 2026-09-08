using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.TaskManager.ViewModels;

namespace RelaxKonOS.Client.Apps.TaskManager.Converters;

/// <summary>字节 → 人类可读（1024 进制，B/KB/MB/GB/TB）。用于内存/磁盘/进程内存。</summary>
public sealed class BytesConverter : IValueConverter
{
    public static readonly BytesConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IConvertible c) return "—";
        var bytes = c.ToInt64(CultureInfo.InvariantCulture);
        return FormatBytes(bytes);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{size:0} {units[u]}" : $"{size:0.##} {units[u]}";
    }
}

/// <summary>字节/秒 → 人类可读速率（如 "1.5 MB/s"）。用于网络速率。</summary>
public sealed class RateConverter : IValueConverter
{
    public static readonly RateConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IConvertible c) return "—";
        var bytes = c.ToInt64(CultureInfo.InvariantCulture);
        return BytesConverter.FormatBytes(bytes) + "/s";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double 占用率 → "45.0%"；null/不可用 → "—"。</summary>
public sealed class PercentTextConverter : IValueConverter
{
    public static readonly PercentTextConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is IConvertible c) return $"{c.ToDouble(CultureInfo.InvariantCulture):0.0}%";
        return "—";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double?（GPU 利用率/温度等可空指标）→ "45.0" 或 "—"。ConverterParameter 可指定格式（默认 0.0）。</summary>
public sealed class NullableDoubleTextConverter : IValueConverter
{
    public static readonly NullableDoubleTextConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is IConvertible c)
        {
            var fmt = parameter as string ?? "0.0";
            return c.ToDouble(CultureInfo.InvariantCulture).ToString(fmt, CultureInfo.InvariantCulture);
        }
        return "—";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a single value through a localized composite-format resource.</summary>
public sealed class LocalizedFormatConverter : IValueConverter
{
    public static readonly LocalizedFormatConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => parameter is string key ? LocalizedText.Format(key, value ?? "—") : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats a byte value and its localized label.</summary>
public sealed class LocalizedBytesConverter : IValueConverter
{
    public static readonly LocalizedBytesConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is IConvertible convertible && parameter is string key
            ? LocalizedText.Format(key, BytesConverter.FormatBytes(convertible.ToInt64(CultureInfo.InvariantCulture)))
            : string.Empty;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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

/// <summary>TaskManagerTab + ConverterParameter → 激活标签页背景（白）/ 非激活（透明）。</summary>
public sealed class TabBgConverter : IValueConverter
{
    private static IBrush Active => ThemeBrushes.Get("SurfaceRaisedBrush");
    private static readonly IBrush Inactive = Brushes.Transparent;
    public static readonly TabBgConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TaskManagerTab tab && parameter is string s
            && Enum.TryParse<TaskManagerTab>(s, ignoreCase: true, out var target))
            return tab == target ? Active : Inactive;
        return Inactive;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 可见性，供可选性能能力区块使用。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>运行时间秒数 → "d天 HH:mm:ss"（不足 1 天则 "HH:mm:ss"）。</summary>
public sealed class UptimeConverter : IValueConverter
{
    public static readonly UptimeConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IConvertible c) return "—";
        var secs = c.ToInt64(CultureInfo.InvariantCulture);
        if (secs < 0) return "—";
        var ts = TimeSpan.FromSeconds(secs);
        return ts.TotalDays >= 1
            ? LocalizedText.Format("task_manager.uptime_days", (int)ts.TotalDays, ts.Hours, ts.Minutes, ts.Seconds)
            : $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
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
