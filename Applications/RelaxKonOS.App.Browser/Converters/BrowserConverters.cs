using System.Globalization;
using Avalonia.Data.Converters;

namespace RelaxKonOS.App.Browser.Converters;

public sealed class BrowserSiteConverter : IValueConverter
{
    public static readonly BrowserSiteConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Bool → 星标字符：true="★"（已加书签），false="☆"（未加）。</summary>
public sealed class BookmarkStarConverter : IValueConverter
{
    public static readonly BookmarkStarConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "★" : "☆";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
