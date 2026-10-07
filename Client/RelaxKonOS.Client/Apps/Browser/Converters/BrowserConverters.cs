using System.Globalization;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Browser.Converters;

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

/// <summary>Formats the browser's loading indicator through localized resources.</summary>
public sealed class LoadingStatusConverter : IValueConverter
{
    public static readonly LoadingStatusConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? LocalizedText.Get("browser.status.loading") : LocalizedText.Get("browser.status.ready");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
