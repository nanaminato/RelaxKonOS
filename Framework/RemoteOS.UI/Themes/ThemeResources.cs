using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemoteOS.UI.Themes;

/// <summary>Semantic resource lookup for controls constructed in C#.</summary>
public static class ThemeResources
{
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        // Theme token brushes are contributed by the application's Styles collection, rather
        // than directly to Application.Resources. TryFindResource walks that complete resource
        // chain, so C#-constructed controls (such as Help Center code blocks) receive the same
        // foreground as XAML controls instead of the transparent fallback.
        return app?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush : Brushes.Transparent;
    }

    public static Color Color(string key)
    {
        var app = Application.Current;
        return app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is Color color
            ? color : Colors.Transparent;
    }
}
