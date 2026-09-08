using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.UI.Themes;

namespace RelaxKonOS.Examples.ServerMonitor.Controls;

/// <summary>Presentation-only line chart bound to an observable metric history.</summary>
public sealed class HistoryChart : Control
{
    public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
        AvaloniaProperty.Register<HistoryChart, IEnumerable<double>?>(nameof(Values));

    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<HistoryChart, Color>(nameof(LineColor), Colors.Transparent);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<HistoryChart, double>(nameof(Maximum), 100);

    private INotifyCollectionChanged? _observableValues;

    static HistoryChart()
    {
        ValuesProperty.Changed.AddClassHandler<HistoryChart>((chart, change) =>
            chart.OnValuesChanged(change.OldValue as IEnumerable<double>, change.NewValue as IEnumerable<double>));
    }

    public IEnumerable<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public HistoryChart()
    {
        Height = 128;
        MinWidth = 260;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = Bounds.Deflate(1);
        var border = new Pen(ThemeResources.Brush("BorderDefaultBrush"));
        context.DrawRectangle(ThemeResources.Brush("SurfaceRaisedBrush"), border, rect);
        if (rect.Width <= 1 || rect.Height <= 1)
            return;

        var grid = new Pen(ThemeResources.Brush("ChartGridLineBrush"));
        for (var row = 1; row < 4; row++)
        {
            var y = rect.Top + rect.Height * row / 4;
            context.DrawLine(grid, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        var values = Values?.ToArray() ?? [];
        if (values.Length < 2)
            return;
        var maximum = Math.Max(Maximum, 1);
        var lineBrush = LineColor == Colors.Transparent ? ThemeResources.Brush("ChartSeries1Brush") : new SolidColorBrush(LineColor);
        var line = new Pen(lineBrush, 2);
        for (var index = 1; index < values.Length; index++)
            context.DrawLine(line, ToPoint(index - 1, values, rect, maximum), ToPoint(index, values, rect, maximum));
    }

    private void OnValuesChanged(IEnumerable<double>? oldValue, IEnumerable<double>? newValue)
    {
        if (_observableValues is not null)
            _observableValues.CollectionChanged -= OnCollectionChanged;
        _observableValues = newValue as INotifyCollectionChanged;
        if (_observableValues is not null)
            _observableValues.CollectionChanged += OnCollectionChanged;
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();

    private static Point ToPoint(int index, IReadOnlyList<double> values, Rect rect, double maximum)
    {
        var x = rect.Left + rect.Width * index / Math.Max(values.Count - 1, 1);
        var ratio = Math.Clamp(values[index] / maximum, 0, 1);
        return new Point(x, rect.Bottom - rect.Height * ratio);
    }
}
