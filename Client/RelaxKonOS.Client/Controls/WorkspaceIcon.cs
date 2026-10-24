using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace RelaxKonOS.Client.Controls;

/// <summary>Theme-coloured, consistently stroked workspace symbols on a 24-unit grid.</summary>
public sealed class WorkspaceIcon : TemplatedControl
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<WorkspaceIcon, string>(nameof(Kind), "File");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private static readonly Dictionary<string, Geometry> Symbols = new()
    {
        ["Folder"] = Geometry.Parse("M3 6 H10 L12 8 H21 V20 H3 Z"),
        ["File"] = Geometry.Parse("M6 3 H14 L19 8 V21 H6 Z M14 3 V8 H19 M9 12 H16 M9 16 H16"),
        ["Branch"] = Geometry.Parse("M7 6 V18 M7 10 C16 10 17 9 17 6 M5 4 A2 2 0 1 0 9 4 A2 2 0 1 0 5 4 M5 20 A2 2 0 1 0 9 20 A2 2 0 1 0 5 20 M15 4 A2 2 0 1 0 19 4 A2 2 0 1 0 15 4"),
        ["Commit"] = Geometry.Parse("M3 12 H8 M16 12 H21 M8 12 A4 4 0 1 0 16 12 A4 4 0 1 0 8 12"),
        ["Search"] = Geometry.Parse("M4 10 A6 6 0 1 0 16 10 A6 6 0 1 0 4 10 M15 15 L21 21"),
        ["Plus"] = Geometry.Parse("M12 5 V19 M5 12 H19"),
        ["Download"] = Geometry.Parse("M12 3 V16 M7 11 L12 16 L17 11 M4 17 V21 H20 V17"),
        ["Refresh"] = Geometry.Parse("M20 10 A8 8 0 0 0 6 6 L3 9 M3 4 V9 H8 M4 14 A8 8 0 0 0 18 18 L21 15 M16 15 H21 V20"),
        ["Edit"] = Geometry.Parse("M5 16 L16 5 L20 9 L9 20 L4 21 Z M14 7 L18 11"),
        ["Remote"] = Geometry.Parse("M4 6 H20 V17 H4 Z M8 21 H16 M12 17 V21"),
        ["Tag"] = Geometry.Parse("M3 4 H12 L21 13 L13 21 L3 11 Z M7 8 H7.1"),
    };
    static WorkspaceIcon() => AffectsRender<WorkspaceIcon>(KindProperty, ForegroundProperty);
    public WorkspaceIcon() { Width = 16; Height = 16; IsHitTestVisible = false; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var geometry = Symbols.GetValueOrDefault(Kind, Symbols["File"]);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(Foreground, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
