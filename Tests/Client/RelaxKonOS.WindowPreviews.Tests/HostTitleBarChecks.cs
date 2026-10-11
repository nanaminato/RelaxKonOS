using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.InteropServices;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Client.Views;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.UI.Themes.SystemStyle;
using RelaxKonOS.WindowManager;
using ShapePath = Avalonia.Controls.Shapes.Path;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

/// <summary>
/// The host's *own* caption - the outer desktop window's minimize / maximize-restore / close cluster,
/// which is not a managed application caption and had never been rendered by a check.
///
/// It is drawn by hand (the host declares WindowDecorations="None"), and it used to draw three
/// characters: U+2212, U+2750 and U+00D7, with a hand-tuned FontSize on the restore mark because the
/// character was optically smaller than the other two. U+2750 is absent from Segoe UI, so which glyph
/// a user saw depended on their installed fonts. These assertions hold the host caption to the same
/// contract as a managed one: the mark is the style's geometry, its box is the style's mark box, its
/// stroke is the platform hairline, and what it announces follows the window's real state rather than
/// the last click.
/// </summary>
internal static class HostTitleBarChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var original = settings.ToPreferences();
        var previousServices = RelaxKonOS.Client.App.Services;
        var windows = new WindowManagerService();
        using var overview = new WindowOverviewController(windows);
        using var coordinator = new SystemUiCoordinator(windows, overview);
        using var services = new ServiceCollection()
            .AddSingleton(localization)
            .AddSingleton(new DesktopDevicePreferences(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "desktop-device.json")))
            .AddSingleton<RelaxKonOS.AppSDK.IAppActivationDiagnostics, UriSchemeRoutingDiagnostics>()
            .AddSingleton(System.Reflection.DispatchProxy.Create<
                RelaxKonOS.Client.Services.Auth.IAuthSession, DisconnectSessionProxy>())
            .AddSingleton<DesktopNotificationService>()
            .AddSingleton(coordinator)
            .BuildServiceProvider();
        typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);

        var window = new MainWindow { Width = 1200, Height = 800 };
        try
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            Pump();

            var minimize = Control<Button>(window, "MinimizeButton");
            var maximize = Control<Button>(window, "MaximizeButton");
            var close = Control<Button>(window, "CloseButton");
            var root = Control<Grid>(window, "Root");
            var host = window;

            // The host opens maximized (the markup says so), so its caption must already offer the
            // restore action before anything has been clicked. A caption that only corrected itself
            // on its own click would advertise the wrong action for the window's whole first life.
            window.WindowState = WindowState.Maximized;
            Pump();
            Check(AutomationProperties.GetName(maximize) == localization.Get("common.restore", "Restore"),
                $"A maximized host caption announces '{AutomationProperties.GetName(maximize)}', expected restore.");
            Check(VisibleMark(maximize) is { } maximizedMark
                  && ReferenceEquals(maximizedMark, window.FindResource(SystemStyleDerivedKeys.WindowCaptionRestoreGlyph)),
                "A maximized host caption does not draw the restore mark.");
            window.WindowState = WindowState.Normal;
            Pump();

            // The three marks, per style, measured off a rendered frame.
            var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "host-titlebar");
            Directory.CreateDirectory(output);

            foreach (var styleId in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike, SystemStyleIds.UbuntuLike })
            {
                settings.SystemStyleId = styleId;
                Pump();

                BuiltInSystemStyles.TryGet(styleId, out var profile);
                var tokens = profile.ResolveTokens(dark: false);
                var glyphBox = tokens["WindowCaptionGlyphSize"];
                var controlWidth = tokens["WindowControlWidth"];
                var titleBarHeight = tokens["WindowTitleBarHeight"];

                // A mark is never text: that is the whole point of the change, and a stray TextBlock
                // would silently reintroduce a font-dependent mark.
                var marks = WindowCaptionMarks.All.Select(entry => window.FindResource(entry.Key)).ToArray();
                foreach (var button in new[] { minimize, maximize, close })
                {
                    Check(!button.GetVisualDescendants().OfType<TextBlock>().Any(),
                        $"{styleId}: the host caption's {button.Name} still draws its mark from text.");
                    var painted = button.GetVisualDescendants().OfType<ShapePath>().ToArray();
                    Check(painted.Length > 0, $"{styleId}: the host caption's {button.Name} paints no mark.");
                    Check(painted.All(mark => marks.Any(resource => ReferenceEquals(resource, mark.Data))),
                        $"{styleId}: the host caption's {button.Name} does not paint one of the style's marks.");
                    Check(painted.All(mark => Math.Abs(mark.StrokeThickness - 1) < 0.01),
                        $"{styleId}: the host caption strokes its mark at {painted[0].StrokeThickness}px, "
                        + "expected the platform's hairline.");
                    Check(painted.All(mark => Math.Abs(mark.Width - glyphBox) < 0.01 && Math.Abs(mark.Height - glyphBox) < 0.01),
                        $"{styleId}: the host caption's {button.Name} is not drawn in the style's {glyphBox}px box.");
                }

                // The host's caption is sized by the style's own window chrome, not by the
                // desktop bar's numbers: it is the outer window, but its buttons are window buttons.
                Check(new[] { minimize, maximize, close }.All(button =>
                          Math.Abs(button.Bounds.Width - controlWidth) < 0.5
                          && Math.Abs(button.Bounds.Height - titleBarHeight) < 0.5),
                    $"{styleId}: the host caption buttons are not {controlWidth}x{titleBarHeight} "
                    + $"({close.Bounds.Width}x{close.Bounds.Height}).");
                Check(maximize.GetVisualDescendants().OfType<ShapePath>().Count(mark => mark.IsVisible) == 1,
                    $"{styleId}: the host's maximize button does not offer exactly one of its two marks.");

                var background = ((ISolidColorBrush)window.FindResource("SurfaceRaisedBrush")!).Color;
                var whitespace = (controlWidth - glyphBox) / 2;
                foreach (var (button, expected, slack) in new[]
                         {
                             // A hairline and an outline are grid-aligned and land on whole pixels,
                             // so any half-pixel offset would smear them into two grey rows; only the
                             // diagonal cross is antialiased by nature. Hence per-mark slack.
                             (minimize, new PixelSize((int)glyphBox, 1), 0),
                             (maximize, new PixelSize((int)glyphBox, (int)glyphBox), 0),
                             (close, new PixelSize((int)glyphBox, (int)glyphBox), 1),
                         })
                {
                    var ink = InkBox(host, button, background);
                    Check(ink is not null, $"{styleId}: the host caption's {button.Name} drew no mark.");
                    var centreX = ink!.Value.X + ink.Value.Width / 2.0;
                    var centreY = ink.Value.Y + ink.Value.Height / 2.0;
                    Check(Math.Abs(centreX - (LeftEdge(button, host) + button.Bounds.Width / 2)) <= 1
                          && Math.Abs(centreY - (TopEdge(button, host) + button.Bounds.Height / 2)) <= 1,
                        $"{styleId}: the host caption's {button.Name} mark is off the button's centre line.");
                    Check(Math.Abs(ink.Value.Width - expected.Width) <= slack
                          && Math.Abs(ink.Value.Height - expected.Height) <= slack,
                        $"{styleId}: the host caption's {button.Name} mark measures "
                        + $"{ink.Value.Width}x{ink.Value.Height}, expected the style's "
                        + $"{expected.Width}x{expected.Height} in a {glyphBox}px box.");
                    // The whitespace around the mark is a consequence, not a number: it is whatever
                    // the style's button has left once the style's mark box sits on its centre line.
                    Check(Math.Abs(ink.Value.X - (LeftEdge(button, host) + whitespace)) <= 1,
                        $"{styleId}: the host caption's {button.Name} leaves "
                        + $"{ink.Value.X - LeftEdge(button, host)}px of whitespace, expected the platform's {whitespace}.");
                }

                // Nothing about the caption may be announced as a symbol, and the two actions a
                // single button offers must each carry their own name.
                Check(AutomationProperties.GetName(minimize) == localization.Get("common.minimize", "Minimize")
                      && AutomationProperties.GetName(close) == localization.Get("common.close", "Close"),
                    $"{styleId}: the host caption announces '{AutomationProperties.GetName(minimize)}' / "
                    + $"'{AutomationProperties.GetName(close)}' instead of its two action names.");
                Check(AutomationProperties.GetName(maximize) == localization.Get("common.maximize", "Maximize"),
                    $"{styleId}: a restored host caption announces '{AutomationProperties.GetName(maximize)}', "
                    + "expected maximize.");

                Capture(host, Path.Combine(output, $"host-titlebar-{styleId}.png"));

                window.WindowState = WindowState.Maximized;
                Pump();
                Check(AutomationProperties.GetName(maximize) == localization.Get("common.restore", "Restore"),
                    $"{styleId}: a maximized host caption announces '{AutomationProperties.GetName(maximize)}', "
                    + "expected restore.");
                Check(VisibleMark(maximize) is { } shown
                      && ReferenceEquals(shown, window.FindResource(SystemStyleDerivedKeys.WindowCaptionRestoreGlyph)),
                    $"{styleId}: a maximized host caption still draws the maximize mark.");

                // Entering and leaving full screen must not desynchronise the caption: the state it
                // keys off is the window's, and full screen is one of the values it can take.
                SetFullScreen(window, true);
                Pump();
                Check(root.Classes.Contains("host-full-screen") && Math.Abs(root.Margin.Top) < 0.01,
                    $"{styleId}: the host still reserves {root.Margin.Top}px of title bar in full screen.");
                SetFullScreen(window, false);
                Pump();
                Check(!root.Classes.Contains("host-full-screen"),
                    $"{styleId}: leaving full screen did not give the title bar its reservation back.");
                Check(Math.Abs(root.Margin.Top - titleBarHeight) < 0.01,
                    $"{styleId}: the desktop reserves {root.Margin.Top}px below the host title bar after a "
                    + $"full-screen round trip, expected the style's {titleBarHeight}.");
                Check(AutomationProperties.GetName(maximize) == localization.Get("common.restore", "Restore")
                      && VisibleMark(maximize) is { } afterFullScreen
                      && ReferenceEquals(afterFullScreen, window.FindResource(SystemStyleDerivedKeys.WindowCaptionRestoreGlyph)),
                    $"{styleId}: a full-screen round trip left the host caption in its maximize form.");

                window.WindowState = WindowState.Normal;
                Pump();
            }

            Console.WriteLine("PASS: host caption geometry (marks, mark box, hairline, whitespace, "
                              + "action names across three system styles), window-state following and the "
                              + "full-screen title-bar reservation.");
        }
        finally
        {
            window.Close();
            settings.Apply(original);
            Pump();
            typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, previousServices);
        }
    }

    private static void SetFullScreen(MainWindow window, bool fullScreen) =>
        typeof(MainWindow).GetMethod("SetFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [fullScreen]);

    private static Geometry? VisibleMark(Button button) =>
        button.GetVisualDescendants().OfType<ShapePath>().SingleOrDefault(mark => mark.IsVisible)?.Data;

    private static T Control<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static double LeftEdge(Visual visual, Visual ancestor) =>
        visual.TranslatePoint(default, ancestor)!.Value.X;

    private static double TopEdge(Visual visual, Visual ancestor) =>
        visual.TranslatePoint(default, ancestor)!.Value.Y;

    /// <summary>
    /// Bounding box, in host coordinates, of everything painted inside <paramref name="target"/> that
    /// is not the surface behind it. Read off a rendered frame rather than off the visual tree, because
    /// the question - where did the mark's ink land - has no answer in the visual tree.
    /// </summary>
    private static PixelRect? InkBox(Window host, Visual target, Color background, int tolerance = 32)
    {
        using var frame = host.CaptureRenderedFrame() ?? throw new Exception("The host window did not render a frame.");
        using var locked = frame.Lock();
        var origin = target.TranslatePoint(default, host)!.Value;
        // The trailing button reaches the frame's own edge, which is not the surface behind it: the
        // probe ignores the outermost two pixels so only the mark itself can qualify.
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

    private static void Capture(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new Exception("The host window did not render a frame.");
        frame.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Host title bar check failed: " + message);
    }
}
