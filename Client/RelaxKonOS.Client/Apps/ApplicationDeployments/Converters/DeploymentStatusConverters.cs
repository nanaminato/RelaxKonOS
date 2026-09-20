using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Converters;

/// <summary>
/// An observed state to one brush of its colour family. <c>ConverterParameter</c> selects the role:
/// the literal <c>foreground</c> yields the text colour, anything else the border and dot colour.
///
/// The brushes come from <see cref="ThemeBrushes"/>, so they follow the active theme rather than
/// freezing the colours that were current when the row was created.
/// </summary>
public sealed class DeploymentStateBrushConverter : IValueConverter
{
    public static readonly DeploymentStateBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ApplicationActualState state) return Brushes.Transparent;
        var family = DeploymentStatusVisual.FamilyOf(state);
        var key = parameter as string == "foreground"
            ? DeploymentStatusVisual.ForegroundKey(family)
            : DeploymentStatusVisual.AccentKey(family);
        return ThemeBrushes.Get(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>An observed state to the glyph that repeats its colour in shape.</summary>
public sealed class DeploymentStateGlyphConverter : IValueConverter
{
    public static readonly DeploymentStateGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ApplicationActualState state ? DeploymentStatusVisual.GlyphOf(state) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A boolean to the brush named by <c>ConverterParameter</c> as <c>"whenTrue|whenFalse"</c>. Used
/// where a boolean already states the fact and only the colour follows from it, so a page needs one
/// property rather than one property per colour.
/// </summary>
public sealed class FlagBrushConverter : IValueConverter
{
    public static readonly FlagBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var keys = (parameter as string ?? string.Empty).Split('|');
        if (keys.Length == 0 || string.IsNullOrEmpty(keys[0])) return Brushes.Transparent;
        var index = value is true || keys.Length == 1 ? 0 : 1;
        return ThemeBrushes.Get(keys[index]);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A boolean to the text given as <c>"whenTrue|whenFalse"</c>. A readiness marker is the case this
/// exists for: the same field shows a tick when the probe passes and a neutral mark when it does
/// not, and the word next to it says which.
/// </summary>
public sealed class FlagTextConverter : IValueConverter
{
    public static readonly FlagTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? string.Empty).Split('|');
        if (parts.Length == 0) return string.Empty;
        return value is true || parts.Length == 1 ? parts[0] : parts[1];
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
