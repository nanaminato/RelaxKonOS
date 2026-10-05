using Avalonia;
using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

/// <summary>Compacts visible actions into equal-size rows as the viewport changes.</summary>
public sealed class ServerCenterActionPanel : Panel
{
    private const double Gap = 12;
    private const double TwoColumnWidth = 620;

    protected override Size MeasureOverride(Size availableSize)
    {
        var actions = Children.Where(child => child.IsVisible).ToArray();
        if (actions.Length == 0) return default;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : TwoColumnWidth;
        var columns = width >= TwoColumnWidth && actions.Length > 1 ? 2 : 1;
        var itemWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        foreach (var action in actions) action.Measure(new Size(itemWidth, double.PositiveInfinity));
        var height = actions.Max(action => action.DesiredSize.Height);
        var rows = (actions.Length + columns - 1) / columns;
        return new Size(width, rows * height + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var actions = Children.Where(child => child.IsVisible).ToArray();
        if (actions.Length == 0) return finalSize;
        var columns = finalSize.Width >= TwoColumnWidth && actions.Length > 1 ? 2 : 1;
        var width = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var height = actions.Max(action => action.DesiredSize.Height);
        for (var index = 0; index < actions.Length; index++)
            actions[index].Arrange(new Rect(index % columns * (width + Gap),
                index / columns * (height + Gap), width, height));
        return finalSize;
    }
}
