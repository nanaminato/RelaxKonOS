using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RelaxKonOS.Client.Apps.Explorer.Controls;

/// <summary>Observed throughput history, painted with the current theme's success color.</summary>
public sealed class TransferSpeedGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<TransferSpeedGraph, IReadOnlyList<double>?>(nameof(Values));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<TransferSpeedGraph, IBrush?>(nameof(Foreground));
    static TransferSpeedGraph() => AffectsRender<TransferSpeedGraph>(ValuesProperty, ForegroundProperty);
    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size).Deflate(0.5);
        if (rect.Width <= 0 || rect.Height <= 0 || Foreground is not { } brush) return;
        using (context.PushOpacity(0.08)) context.DrawRectangle(brush, null, rect);
        using (context.PushOpacity(0.16))
        {
            var pen = new Pen(brush, 1);
            for (var i = 1; i < 5; i++) context.DrawLine(pen, new(rect.Left, rect.Top + rect.Height * i / 5), new(rect.Right, rect.Top + rect.Height * i / 5));
        }
        var values = Values;
        if (values is null || values.Count < 2) return;
        var maximum = Math.Max(1, values.Max() * 1.2);
        var points = values.Select((value, index) => new Point(rect.Left + rect.Width * index / (values.Count - 1),
            rect.Bottom - Math.Clamp(value / maximum, 0, 1) * rect.Height)).ToArray();
        var shape = new StreamGeometry();
        using (var drawing = shape.Open())
        {
            drawing.BeginFigure(new(rect.Left, rect.Bottom), true);
            foreach (var point in points) drawing.LineTo(point);
            drawing.LineTo(new(rect.Right, rect.Bottom)); drawing.EndFigure(true);
        }
        using (context.PushOpacity(0.28)) context.DrawGeometry(brush, null, shape);
        var line = new Pen(brush, 1.5);
        for (var i = 1; i < points.Length; i++) context.DrawLine(line, points[i - 1], points[i]);
    }
}
