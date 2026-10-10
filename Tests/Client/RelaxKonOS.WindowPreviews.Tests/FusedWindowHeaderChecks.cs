using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.WindowManager;
using Point = Avalonia.Point;
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

                // A fused bar takes its geometry from the style it belongs to, never from one shared
                // number written into the window manager.
                var expectedHeight = Math.Max(48, tokens["WindowTitleBarHeight"] + 14);
                Check(Math.Abs(bar.Bounds.Height - expectedHeight) < 1,
                    $"{expectation.Recipe}: the fused bar is {bar.Bounds.Height} tall, expected {expectedHeight}.");

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
                    Check(Alpha(close.Foreground) == 0 && Alpha(maximize.Foreground) == 0,
                        $"{expectation.Recipe}: the glyphs are painted before the cluster is engaged.");
                    ((IPseudoClasses)cluster.Classes).Set(":pointerover", true); Pump();
                    Check(Alpha(close.Foreground) > 0,
                        $"{expectation.Recipe}: hovering the cluster does not reveal the glyphs.");
                    ((IPseudoClasses)cluster.Classes).Set(":pointerover", false); Pump();
                }
                else
                {
                    Check(Math.Abs(close.Bounds.Width - tokens["WindowControlWidth"]) < 0.5,
                        $"{expectation.Recipe}: the caption buttons do not use the style's control width.");
                }

                // A headerbar is the application's own toolbar: the fused bar is raised like the
                // sidebar beside it, not left as one shared surface for every recipe.
                var expectedSurface = expectation.Recipe == WindowChromeRecipes.HeaderbarRight ? "SurfaceRaisedBrush" : "SurfaceBrush";
                Check(ReferenceEquals(bar.Background, view.FindResource(expectedSurface)),
                    $"{expectation.Recipe}: the fused bar did not take the {expectedSurface} its recipe asks for.");

                Capture(host, new PixelSize(1100, 800), Path.Combine(output, $"fused-header-{expectation.Recipe}.png"));
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
            view.HeaderTabs = null; Pump();
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
                              "tab strip column and the centred block yielding to it).");
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

    /// <summary>Horizontal overlap only: the title bar is a single row, so vertical position carries no information.</summary>
    private static bool Overlaps(Visual first, Visual second, Visual ancestor) =>
        LeftEdge(first, ancestor) < RightEdge(second, ancestor) && LeftEdge(second, ancestor) < RightEdge(first, ancestor);

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
