using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.UI.Themes.SystemStyle;
using RelaxKonOS.WindowManager;
using Point = Avalonia.Point;
using ShapePath = Avalonia.Controls.Shapes.Path;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;
using WindowRect = RelaxKonOS.Core.Primitives.Rect;

/// <summary>
/// The host half of the fused title bar: an application hands over four role blocks, the chrome
/// recipe decides which cell each one occupies, and the caption cluster stays host-owned.
///
/// This is the "window chrome × system style" cell of the system-style regression matrix. It had
/// never been rendered before: a fused header only ever existed under the default recipe, so no one
/// had seen one on a recipe whose caption buttons lead. Every assertion here is about placement or
/// palette, never about a specific style's numbers, because the point is that an application that
/// knows nothing about recipes still ends up correctly laid out on all of them.
/// </summary>
internal static class FusedWindowHeaderChecks
{
    public static void Run(ShellSettings settings)
    {
        var original = settings.ToPreferences();
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = new Grid { Children = { canvas } } };
        try
        {
            host.Show(); Pump();
            var windows = new WindowManagerService();
            windows.Attach(canvas);
            windows.SetHostBounds(new WindowRect(0, 0, 1100, 800));

            var leading = new Border { Width = 90, Height = 26, Background = Brushes.Silver };
            var center = new Border { Height = 26, Background = Brushes.LightSteelBlue };
            var trailing = new Border { Width = 70, Height = 26, Background = Brushes.Khaki };
            var managed = windows.Create(new WindowCreateOptions(new AppId("preview.fused"), "Fused header",
                new Border { Background = Brushes.White }, new WindowRect(0, 0, 1080, 780)));
            var view = managed.View;
            view.HeaderLeading = leading;
            view.HeaderCenter = center;
            view.HeaderTrailing = trailing;
            Pump();

            Check(view.HasTitleBarContent, "Role slots did not put the host title bar into its fused form.");
            var contentHost = Descendant<ContentPresenter>(view, "PART_ContentHost");
            var bar = Descendant<Border>(view, "PART_TitleBar");
            var barTop = Descendant<ContentPresenter>(view, "PART_HeaderLeading").TranslatePoint(default, view)!.Value.Y;
            Check(barTop >= 0 && contentHost.TranslatePoint(default, view)!.Value.Y >= bar.Bounds.Height - 1,
                "The fused bar is not a single layer above the application content.");

            var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "fused-header");
            Directory.CreateDirectory(output);

            var expectations = new[]
            {
                (StyleId: SystemStyleIds.WindowsLike, Recipe: WindowChromeRecipes.CaptionButtonsRight, ClusterLeads: false),
                (StyleId: SystemStyleIds.MacOsLike, Recipe: WindowChromeRecipes.TrafficLightsLeft, ClusterLeads: true),
                (StyleId: SystemStyleIds.UbuntuLike, Recipe: WindowChromeRecipes.HeaderbarRight, ClusterLeads: false),
            };

            foreach (var expectation in expectations)
            {
                settings.SystemStyleId = expectation.StyleId; Pump();
                BuiltInSystemStyles.TryGet(expectation.StyleId, out var profile);
                Check(profile.SupportedRecipes.WindowChrome == expectation.Recipe,
                    $"{expectation.StyleId} no longer selects {expectation.Recipe}.");
                var tokens = profile.ResolveTokens(dark: false);

                var cluster = Descendant<Grid>(view, "PART_WindowControls");
                var close = Descendant<Button>(view, "PART_Close");
                var minimize = Descendant<Button>(view, "PART_Minimize");
                var maximize = Descendant<Button>(view, "PART_Maximize");

                Check((string)view.FindResource("SystemStyle.WindowChrome")! == expectation.Recipe,
                    $"{expectation.StyleId} declares {expectation.Recipe} but the window adopted another recipe.");
                Check(Grid.GetColumn(cluster) == (expectation.ClusterLeads ? 0 : 4),
                    $"{expectation.Recipe}: the caption cluster stayed on the wrong side of the bar.");

                // The three role slots are placed by the host, identically on every recipe: leading,
                // flexible centre, trailing - and the cluster never shares a cell with any of them.
                Check(Grid.GetColumn(Descendant<ContentPresenter>(view, "PART_HeaderLeading")) == 1
                      && Grid.GetColumn(Descendant<ContentPresenter>(view, "PART_HeaderCenter")) == 2
                      && Grid.GetColumn(Descendant<ContentPresenter>(view, "PART_HeaderTrailing")) == 3,
                    $"{expectation.Recipe}: an application role slot was moved out of its cell.");
                Check(!Overlaps(leading, cluster, view) && !Overlaps(center, cluster, view) && !Overlaps(trailing, cluster, view),
                    $"{expectation.Recipe}: an application block collides with the host caption cluster.");
                Check(RightEdge(leading, view) <= LeftEdge(center, view) + 1 && RightEdge(center, view) <= LeftEdge(trailing, view) + 1,
                    $"{expectation.Recipe}: the application blocks are not laid out leading-to-trailing.");

                // Order within the cluster, read off the screen rather than off the markup.
                if (expectation.ClusterLeads)
                {
                    Check(LeftEdge(close, view) < LeftEdge(minimize, view) && LeftEdge(minimize, view) < LeftEdge(maximize, view),
                        $"{expectation.Recipe}: the caption cluster is not ordered close / minimize / zoom.");
                    Check(LeftEdge(leading, view) >= RightEdge(cluster, view) - 1,
                        $"{expectation.Recipe}: the leading block starts before the leading cluster ends.");
                }
                else
                {
                    Check(LeftEdge(minimize, view) < LeftEdge(maximize, view) && LeftEdge(maximize, view) < LeftEdge(close, view),
                        $"{expectation.Recipe}: the caption cluster is not ordered minimize / maximize / close.");
                }

                // A fused bar grows out of the style's own *window caption*, never out of one shared
                // number written into the window manager - and never out of the host's desktop bar,
                // which is a different surface with a different height.
                var expectedHeight = Math.Max(48, tokens["WindowCaptionHeight"] + 14);
                Check(Math.Abs(bar.Bounds.Height - expectedHeight) < 1,
                    $"{expectation.Recipe}: the fused bar is {bar.Bounds.Height} tall, expected {expectedHeight}.");

                var captionHeight = tokens["WindowCaptionHeight"];
                // The marks are the host's own geometry rather than characters, so nothing about them can
                // move with a font: each button paints one of the style's marks, in the style's own box,
                // with the platform's 1px hairline.
                var glyphBox = tokens["WindowCaptionGlyphSize"];
                var styleMarks = WindowCaptionMarks.All.Select(entry => view.FindResource(entry.Key)).ToArray();
                foreach (var button in new[] { minimize, maximize, close })
                {
                    Check(!button.GetVisualDescendants().OfType<TextBlock>().Any(),
                        $"{expectation.Recipe}: {button.Name} still draws its mark from text.");
                    var painted = button.GetVisualDescendants().OfType<ShapePath>().ToArray();
                    Check(painted.Length > 0, $"{expectation.Recipe}: {button.Name} paints no mark.");
                    Check(painted.All(mark => styleMarks.Any(resource => ReferenceEquals(resource, mark.Data))),
                        $"{expectation.Recipe}: {button.Name} does not paint one of the style's marks.");
                    Check(painted.All(mark => Math.Abs(mark.StrokeThickness - 1) < 0.01),
                        $"{expectation.Recipe}: {button.Name} strokes its mark at {painted[0].StrokeThickness}px, expected the platform's hairline.");
                    Check(painted.All(mark => Math.Abs(mark.Width - glyphBox) < 0.01 && Math.Abs(mark.Height - glyphBox) < 0.01),
                        $"{expectation.Recipe}: {button.Name}'s mark is not drawn in the style's {glyphBox}px box.");
                }

                // One button, two actions: a maximized caption offers "restore", a normal one "maximize",
                // and exactly one of the two marks is showing.
                Check(maximize.GetVisualDescendants().OfType<ShapePath>().Count(mark => mark.IsVisible) == 1,
                    $"{expectation.Recipe}: the maximize button does not offer exactly one of its two marks.");

                if (expectation.Recipe == WindowChromeRecipes.TrafficLightsLeft)
                {
                    var diameter = tokens.TryGetValue("WindowTrafficLightSize", out var declared) ? declared : 12;
                    // The corner radius is the shared "fully rounded" caption radius, which is always
                    // at least half of any admitted traffic-light diameter - so the circle is exact.
                    Check(new[] { close, minimize, maximize }.All(button =>
                              Math.Abs(button.Bounds.Width - diameter) < 0.5 && Math.Abs(button.Bounds.Height - diameter) < 0.5
                              && button.CornerRadius.TopLeft >= diameter / 2 - 0.5),
                        $"{expectation.Recipe}: the caption buttons are not {diameter}px circles "
                        + $"({close.Bounds.Width}x{close.Bounds.Height}, r={close.CornerRadius.TopLeft}).");
                    Check(ReferenceEquals(close.Background, view.FindResource("DangerBrush"))
                          && ReferenceEquals(minimize.Background, view.FindResource("WarningBrush"))
                          && ReferenceEquals(maximize.Background, view.FindResource("SuccessBrush")),
                        $"{expectation.Recipe}: the traffic lights did not take their colours from the palette roles.");
                    // macOS centres the three lights on the window's toolbar line, so a fused bar grows
                    // symmetrically around them rather than pushing them to one edge.
                    Check(Math.Abs(CentreY(cluster, view) - CentreY(bar, view)) < 1,
                        $"{expectation.Recipe}: the traffic lights are not centred on the bar "
                        + $"({CentreY(cluster, view)} vs {CentreY(bar, view)}).");
                    Check(Alpha(close.Foreground) == 0 && Alpha(maximize.Foreground) == 0,
                        $"{expectation.Recipe}: the glyphs are painted before the cluster is engaged.");
                    ((IPseudoClasses)cluster.Classes).Set(":pointerover", true); Pump();
                    Check(Alpha(close.Foreground) > 0,
                        $"{expectation.Recipe}: hovering the cluster does not reveal the glyphs.");
                    ((IPseudoClasses)cluster.Classes).Set(":pointerover", false); Pump();
                }
                else
                {
                    // A caption button is measured by the platform's caption, not by the bar it happens
                    // to sit in: growing the bar around a tab strip must not inflate a 32px button, and
                    // the whitespace left around the glyph is whatever remains once the two agree.
                    var controlWidth = tokens["WindowControlWidth"];
                    Check(Math.Abs(cluster.Bounds.Height - captionHeight) < 1,
                        $"{expectation.Recipe}: the caption cluster is {cluster.Bounds.Height} tall, expected {captionHeight}.");
                    Check(new[] { minimize, maximize, close }.All(button =>
                              Math.Abs(button.Bounds.Width - controlWidth) < 0.5 && Math.Abs(button.Bounds.Height - captionHeight) < 0.5),
                        $"{expectation.Recipe}: the caption buttons are not {controlWidth}x{captionHeight} "
                        + $"({close.Bounds.Width}x{close.Bounds.Height}).");

                    // Windows 11 pins its three buttons to the top of the window and lets the tab row
                    // grow below them; a headerbar keeps its buttons on its own centre line instead.
                    if (expectation.Recipe == WindowChromeRecipes.CaptionButtonsRight)
                    {
                        Check(Math.Abs(TopEdge(cluster, view) - TopEdge(bar, view)) < 1,
                            $"{expectation.Recipe}: the trailing caption cluster did not take the top of the bar.");

                        // What the buttons *render*, not just what they measure. The marks are geometry,
                        // so the platform's own numbers apply exactly: the ink is the style's mark box,
                        // the whitespace around it is whatever the button has left over, and the mark sits
                        // on the button's centre line because it was placed there rather than by a font.
                        var background = ((ISolidColorBrush)view.FindResource("SurfaceBrush")!).Color;
                        var whitespace = (controlWidth - glyphBox) / 2;
                        foreach (var (button, expected, slack) in new[]
                                 {
                                     // A hairline and an outline are grid-aligned, so they land on whole
                                     // pixels: any half-pixel offset would smear them into two grey rows.
                                     // Only the diagonal cross is antialiased by nature.
                                     (minimize, new PixelSize((int)glyphBox, 1), 0),
                                     (maximize, new PixelSize((int)glyphBox, (int)glyphBox), 0),
                                     (close, new PixelSize((int)glyphBox, (int)glyphBox), 1),
                                 })
                        {
                            var ink = InkBox(host, button, background);
                            Check(ink is not null, $"{expectation.Recipe}: {button.Name} drew no mark.");
                            var centreX = ink!.Value.X + ink.Value.Width / 2;
                            var centreY = ink.Value.Y + ink.Value.Height / 2;
                            Check(Math.Abs(centreX - (LeftEdge(button, host) + button.Bounds.Width / 2)) <= 1
                                  && Math.Abs(centreY - (TopEdge(button, host) + button.Bounds.Height / 2)) <= 1,
                                $"{expectation.Recipe}: {button.Name}'s mark is off the button's centre line "
                                + $"({centreX - (LeftEdge(button, host) + button.Bounds.Width / 2):+0.#}, "
                                + $"{centreY - (TopEdge(button, host) + button.Bounds.Height / 2):+0.#}).");
                            Check(Math.Abs(ink.Value.Width - expected.Width) <= slack
                                  && Math.Abs(ink.Value.Height - expected.Height) <= slack,
                                $"{expectation.Recipe}: {button.Name}'s mark measures {ink.Value.Width}x{ink.Value.Height}, "
                                + $"expected the style's {expected.Width}x{expected.Height} in a {glyphBox}px box.");
                            Check(Math.Abs(ink.Value.X - (LeftEdge(button, host) + whitespace)) <= 1,
                                $"{expectation.Recipe}: {button.Name} leaves {ink.Value.X - LeftEdge(button, host)}px of "
                                + $"whitespace, expected the platform's {whitespace}.");
                        }
                    }
                    else
                        Check(Math.Abs(CentreY(cluster, view) - CentreY(bar, view)) < 1,
                            $"{expectation.Recipe}: the headerbar buttons are not centred on their bar.");
                }

                // A headerbar is the application's own toolbar: the fused bar is raised like the
                // sidebar beside it, not left as one shared surface for every recipe.
                var expectedSurface = expectation.Recipe == WindowChromeRecipes.HeaderbarRight ? "SurfaceRaisedBrush" : "SurfaceBrush";
                Check(ReferenceEquals(bar.Background, view.FindResource(expectedSurface)),
                    $"{expectation.Recipe}: the fused bar did not take the {expectedSurface} its recipe asks for.");

                Capture(host, new PixelSize(1100, 800), Path.Combine(output, $"fused-header-{expectation.Recipe}.png"));

                // The maximize button offers two actions, so what it announces and what it draws both
                // have to follow the window state. It comes after the capture so the fixture's own
                // screenshot stays the normal-state one.
                windows.ToggleMaximize(managed); Pump();
                Check(AutomationProperties.GetName(maximize) == WindowCaptionLabels.Current.Restore,
                    $"{expectation.Recipe}: a maximized caption announces '{AutomationProperties.GetName(maximize)}', expected restore.");
                Check(maximize.GetVisualDescendants().OfType<ShapePath>().Single(mark => mark.IsVisible).Data is { } shown
                      && ReferenceEquals(shown, view.FindResource(SystemStyleDerivedKeys.WindowCaptionRestoreGlyph)),
                    $"{expectation.Recipe}: a maximized caption still draws the maximize mark.");
                windows.ToggleMaximize(managed); Pump();
                Check(AutomationProperties.GetName(maximize) == WindowCaptionLabels.Current.Maximize,
                    $"{expectation.Recipe}: a restored caption announces '{AutomationProperties.GetName(maximize)}', expected maximize.");
            }

            // Dragging has to survive the new slots: they exist so an application can put a control in
            // the bar, not so it can take the drag away from the host.
            settings.SystemStyleId = SystemStyleIds.WindowsLike; Pump();
            var dragOrigin = center.TranslatePoint(new Point(center.Bounds.Width / 2, center.Bounds.Height / 2), host)!.Value;
            var before = managed.Info.Bounds;
            host.MouseDown(dragOrigin, MouseButton.Left);
            host.MouseMove(dragOrigin + new Vector(16, 12));
            host.MouseUp(dragOrigin + new Vector(16, 12), MouseButton.Left); Pump();
            Check(managed.Info.Bounds != before, "A role slot made the fused title bar undraggable.");

            // The tab strip is the second consumer shape, and the one that forced a fourth role: a
            // window's tabs are its identity, so they take the whole flexible column and the host
            // stands both its own title text and the centred block down. Nothing here says "put the
            // tabs in column 2" from the application's side - the recipe still owns that, which is
            // what leaves a future recipe free to push the strip onto a row of its own.
            var tabs = new Border { Height = 26, Background = Brushes.MediumPurple };
            view.HeaderCenter = null;
            view.HeaderTabs = tabs;
            Pump();
            Check(view.HasTitleBarContent, "Declaring a tab strip did not put the host title bar into its fused form.");
            Check(Math.Abs(Convert.ToDouble(view.FindResource("WindowTabHeight")) - 28) < 0.01,
                "The host did not publish a WindowTabHeight for the tab strip to size itself from.");
            Check(Descendant<ContentPresenter>(view, "PART_HeaderCenter").IsVisible == false,
                "The centred block was not stood down for the tab strip.");

            foreach (var expectation in expectations)
            {
                settings.SystemStyleId = expectation.StyleId; Pump();

                var tabsHost = Descendant<ContentPresenter>(view, "PART_HeaderTabs");
                var cluster = Descendant<Grid>(view, "PART_WindowControls");
                var title = Descendant<TextBlock>(view, "PART_TitleText");

                Check(Grid.GetColumn(tabsHost) == 2,
                    $"{expectation.Recipe}: the tab strip was moved out of the title bar's flexible column.");
                Check(!title.IsVisible,
                    $"{expectation.Recipe}: the host title text did not yield to the tab strip.");
                Check(!Overlaps(tabs, cluster, view) && !Overlaps(tabs, leading, view) && !Overlaps(tabs, trailing, view),
                    $"{expectation.Recipe}: the tab strip collides with a block the host placed itself.");

                // The strip has to *take* the flexible column, not sit at its start like one more
                // fixed control: it begins after the leading block and runs up to the trailing edge
                // the recipe reserved.
                Check(LeftEdge(tabs, view) >= RightEdge(leading, view) - 1 && RightEdge(tabs, view) > RightEdge(leading, view),
                    $"{expectation.Recipe}: the tab strip did not fill the flexible column.");
                if (expectation.ClusterLeads)
                    Check(RightEdge(cluster, view) <= LeftEdge(tabs, view) + 1,
                        $"{expectation.Recipe}: the leading caption cluster overlaps the tab strip.");
                else
                    Check(RightEdge(tabs, view) <= LeftEdge(cluster, view) + 1,
                        $"{expectation.Recipe}: the tab strip runs under the trailing caption cluster.");

                Capture(host, new PixelSize(1100, 800), Path.Combine(output, $"fused-header-tabs-{expectation.Recipe}.png"));
            }

            // Clearing the strip has to hand the flexible column back, and a tab strip alone - with
            // no other block - still fuses the bar, because on a tabbed window the tabs *are* the
            // title.
            view.HeaderTabs = null;
            view.HeaderCenter = center; Pump();
            Check(Descendant<ContentPresenter>(view, "PART_HeaderCenter").IsVisible,
                "Clearing the tab strip did not give the flexible column back to the centred block.");

            view.HeaderLeading = null;
            view.HeaderCenter = null;
            view.HeaderTrailing = null;
            view.HeaderTabs = tabs;
            Pump();
            Check(view.HasTitleBarContent,
                "A tab strip on its own no longer fuses the host title bar.");

            Console.WriteLine("PASS: fused title bar role slots under all three window-chrome recipes " +
                              "(placement, cluster order, traffic-light geometry and palette, bar geometry, drag, " +
                              "inline tab strip placement).");
        }
        finally
        {
            settings.Apply(original); Pump();
            host.Close();
        }
    }

    private static T Descendant<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static double LeftEdge(Visual visual, Visual ancestor) =>
        visual.TranslatePoint(default, ancestor)!.Value.X;

    /// <summary>Alpha of a paint, whatever brush implementation the resource or a literal resolved to.</summary>
    private static byte Alpha(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color.A : (byte)255;

    private static double RightEdge(Visual visual, Visual ancestor) =>
        LeftEdge(visual, ancestor) + visual.Bounds.Width;

    private static double TopEdge(Visual visual, Visual ancestor) =>
        visual.TranslatePoint(default, ancestor)!.Value.Y;

    private static double CentreY(Visual visual, Visual ancestor) =>
        TopEdge(visual, ancestor) + visual.Bounds.Height / 2;

    /// <summary>Horizontal overlap only: the title bar is a single row, so vertical position carries no information.</summary>
    private static bool Overlaps(Visual first, Visual second, Visual ancestor) =>
        LeftEdge(first, ancestor) < RightEdge(second, ancestor) && LeftEdge(second, ancestor) < RightEdge(first, ancestor);

    /// <summary>
    /// Bounding box, in host coordinates, of everything painted inside <paramref name="target"/> that
    /// is not the surface behind it. Read off a rendered frame rather than off the visual tree, because
    /// the question - where did the glyph's ink land - has no answer in the visual tree.
    /// </summary>
    private static PixelRect? InkBox(Window host, Visual target, Color background, int tolerance = 32)
    {
        using var frame = host.CaptureRenderedFrame() ?? throw new Exception("The window did not render a frame.");
        using var locked = frame.Lock();
        var origin = target.TranslatePoint(default, host)!.Value;
        // A trailing button reaches the frame's own border, which is not the surface behind it: the
        // probe ignores the outermost two pixels so only the glyph itself can qualify.
        const int inset = 2;
        var left = (int)Math.Round(origin.X) + inset;
        var top = (int)Math.Round(origin.Y) + inset;
        var width = (int)Math.Round(target.Bounds.Width) - inset * 2;
        var height = (int)Math.Round(target.Bounds.Height) - inset * 2;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var y = top; y < top + height; y++)
        for (var x = left; x < left + width; x++)
        {
            var pixel = locked.Address + y * locked.RowBytes + x * 4;
            // Channel order differs per platform; only "differs from the surface" matters here.
            if (Math.Abs(Marshal.ReadByte(pixel) - background.B) <= tolerance
                && Math.Abs(Marshal.ReadByte(pixel + 1) - background.G) <= tolerance
                && Math.Abs(Marshal.ReadByte(pixel + 2) - background.R) <= tolerance)
                continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }

        return minX == int.MaxValue ? null : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static void Capture(Window window, PixelSize size, string path)
    {
        using var screenshot = window.CaptureRenderedFrame() ?? throw new Exception("Fused header did not render a frame.");
        screenshot.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Fused title bar check failed: " + message);
    }
}
