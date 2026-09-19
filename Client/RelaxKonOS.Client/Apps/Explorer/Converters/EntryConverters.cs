// SizeSuffix 逻辑移植自 Jaya FileSystemObjectModel.SizeSuffix（BSD-3）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.Globalization;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Converters;

/// <summary>Matches a row entry with the view model's active inline-rename entry.</summary>
public sealed class EntryIsEditingConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var matches = values.Count >= 2
            && values[0] is FileSystemEntryDto entry
            && values[1] is FileSystemEntryDto editing
            && ExplorerPath.Equal(entry.Path, editing.Path);
        return string.Equals(parameter as string, "inverse", StringComparison.Ordinal) ? !matches : matches;
    }
}

/// <summary>Dims entries that are currently pending a cut operation in the shared clipboard.</summary>
public sealed class CutEntryOpacityConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var normal = string.Equals(parameter as string, "secondary", StringComparison.Ordinal) ? 0.8d : 1d;
        return values.Count >= 2 && values[0] is FileSystemEntryDto entry
            && values[1] is IReadOnlyList<string> paths
            && paths.Any(path => ExplorerPath.Equal(path, entry.Path))
                ? normal * 0.5d
                : normal;
    }
}

/// <summary>条目类型 → 中文类型名（用于"类型"列）。</summary>
public sealed class EntryTypeToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FileSystemEntryType t ? t switch
        {
            FileSystemEntryType.Drive => LocalizedText.Get("explorer.entry_type.drive"),
            FileSystemEntryType.Directory => LocalizedText.Get("explorer.entry_type.directory"),
            FileSystemEntryType.File => LocalizedText.Get("explorer.entry_type.file"),
            _ => string.Empty
        } : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>File extensions make the displayed type agree with extension-based type sorting.</summary>
public sealed class EntryDescriptionConverter : IValueConverter
{
    private static readonly EntryTypeToStringConverter TypeConverter = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileSystemEntryDto entry) return string.Empty;
        var extension = ExplorerPath.Extension(entry.Name).TrimStart('.');
        if (entry.Type == FileSystemEntryType.File && extension.Length > 0)
            return LocalizedText.Format("explorer.entry_type.extension", extension.ToUpperInvariant());
        return TypeConverter.Convert(entry.Type, targetType, parameter, culture);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats the Explorer's loading indicator through localized resources.</summary>
public sealed class ExplorerLoadingStatusConverter : IValueConverter
{
    public static readonly ExplorerLoadingStatusConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? LocalizedText.Get("explorer.status.loading") : LocalizedText.Get("explorer.status.ready");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>大小 → 友好字符串（字节/KB/MB/...）。目录/驱动器无大小返回空。移植自 Jaya SizeSuffix。</summary>
public sealed class EntrySizeToStringConverter : IValueConverter
{
    private static readonly string[] Suffixes = { "bytes", "KB", "MB", "GB", "TB", "PB" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size || size <= 0) return string.Empty;
        var mag = (int)Math.Log(size, 1024);
        if (mag >= Suffixes.Length) mag = Suffixes.Length - 1;
        var adjusted = (decimal)size / (1L << (mag * 10));
        if (Math.Round(adjusted, 2) >= 1000 && mag < Suffixes.Length - 1)
        {
            mag++;
            adjusted /= 1024;
        }
        return $"{adjusted:n2} {Suffixes[mag]}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
