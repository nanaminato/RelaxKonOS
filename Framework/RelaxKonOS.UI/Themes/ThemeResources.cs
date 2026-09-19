using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RelaxKonOS.UI.Themes;

/// <summary>Semantic resource lookup and binding for controls constructed in C#.</summary>
public static class ThemeResources
{
    /// <summary>
    /// Binds a property to a theme resource for the lifetime of the control.
    /// </summary>
    /// <remarks>
    /// This is the C# equivalent of <c>{DynamicResource ...}</c> and is the only supported way for
    /// code-constructed UI to consume a token: the value is re-resolved whenever the palette or the
    /// system style changes, so a control built before a style switch is still correct afterwards.
    /// Prefer this over <see cref="Brush"/> for anything that can outlive a single layout pass.
    /// </remarks>
    public static void Bind(Control target, AvaloniaProperty property, string resourceKey)
        => target.Bind(property, target.GetResourceObservable(resourceKey));

    /// <summary>Binds a control's background, border and corner radius in one call.</summary>
    public static void BindSurface(
        Control target,
        string backgroundKey,
        string? borderKey = null,
        string? borderThicknessKey = null,
        string? cornerRadiusKey = null)
    {
        Bind(target, Border.BackgroundProperty, backgroundKey);
        if (borderKey is not null) Bind(target, Border.BorderBrushProperty, borderKey);
        if (borderThicknessKey is not null) Bind(target, Border.BorderThicknessProperty, borderThicknessKey);
        if (cornerRadiusKey is not null) Bind(target, Border.CornerRadiusProperty, cornerRadiusKey);
    }

    /// <summary>
    /// Snapshot of a brush resource at call time.
    /// </summary>
    /// <remarks>
    /// This does <b>not</b> track theme changes. It exists only for the rare case where a brush has
    /// to be handed to an API that cannot accept a binding; anything that stays on screen must use
    /// <see cref="Bind"/> instead.
    /// </remarks>
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        // Theme token brushes are contributed by the application's Styles collection, rather
        // than directly to Application.Resources. TryFindResource walks that complete resource
        // chain, so C#-constructed controls receive the same foreground as XAML controls instead
        // of the transparent fallback.
        return app?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush : Brushes.Transparent;
    }

    /// <summary>Snapshot of a colour resource at call time. Does not track theme changes.</summary>
    public static Color Color(string key)
    {
        var app = Application.Current;
        return app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is Color color
            ? color : Colors.Transparent;
    }
}
