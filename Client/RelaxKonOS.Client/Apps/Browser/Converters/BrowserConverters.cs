using System.Globalization;
using Avalonia.Data.Converters;
using RelaxKonOS.Client.Localization;
using Avalonia.Media;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Client.Services.Theming;

namespace RelaxKonOS.Client.Apps.Browser.Converters;

/// <summary>Bool → 星标字符：true="★"（已加书签），false="☆"（未加）。</summary>
public sealed class BookmarkStarConverter : IValueConverter
{
    public static readonly BookmarkStarConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "★" : "☆";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举值（SidebarTab）+ ConverterParameter（"Bookmarks"/"History"）→ bool 可见性。
/// 用于侧边栏 ListBox/底部按钮的 IsVisible 绑定，按当前激活标签页切换。</summary>
public sealed class SidebarTabVisibilityConverter : IValueConverter
{
    public static readonly SidebarTabVisibilityConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SidebarTab tab && parameter is string s
            && Enum.TryParse<SidebarTab>(s, ignoreCase: true, out var target))
            return tab == target;
        return false;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>枚举值（SidebarTab）+ ConverterParameter → 激活标签页背景色（白色）vs 非激活（透明）。
/// 用于标签切换按钮的 Background 绑定。</summary>
public sealed class SidebarTabBgConverter : IValueConverter
{
    private static IBrush Active => ThemeBrushes.Get("SurfaceRaisedBrush");
    private static readonly IBrush Inactive = Brushes.Transparent;

    public static readonly SidebarTabBgConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SidebarTab tab && parameter is string s
            && Enum.TryParse<SidebarTab>(s, ignoreCase: true, out var target))
            return tab == target ? Active : Inactive;
        return Inactive;
    }
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
