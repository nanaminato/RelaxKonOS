using Avalonia;
using Avalonia.Controls;

namespace RelaxKonOS.Client.Views.Shell;

/// <summary>Desktop column ordering, mirrored for macOS without mirroring labels or artwork.</summary>
internal sealed class DesktopIconPanel(bool fromRight) : Panel
{
    private const double CellWidth = 102;
    private const double CellHeight = 104;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(CellWidth, CellHeight));
        var rows = Rows(availableSize.Height);
        var columns = (int)Math.Ceiling((double)Children.Count / rows);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : columns * CellWidth,
            double.IsFinite(availableSize.Height) ? availableSize.Height : Children.Count * CellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = Rows(finalSize.Height);
        for (var index = 0; index < Children.Count; index++)
        {
            var column = index / rows;
            var x = fromRight ? finalSize.Width - (column + 1) * CellWidth : column * CellWidth;
            Children[index].Arrange(new Rect(x, index % rows * CellHeight, CellWidth, CellHeight));
        }
        return finalSize;
    }

    private int Rows(double height) => double.IsFinite(height)
        ? Math.Max(1, (int)(height / CellHeight)) : Math.Max(1, Children.Count);
}
