using Avalonia.Media;
using RelaxKonOS.UI.Themes;

namespace RelaxKonOS.Client.Services.Theming;

/// <summary>Bridge for controls constructed in C#. Returned brushes track the dynamic colour token.</summary>
public static class ThemeBrushes
{
    public static IBrush Get(string key)
    {
        return ThemeResources.Brush(key);
    }
}
