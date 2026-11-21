using System.Globalization;
using Avalonia.Data.Converters;

namespace RelaxKonOS.App.TaskManager.Converters;

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
