using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.WindowManager;
using CoreRect = RelaxKonOS.Core.Primitives.Rect;

internal static class SshFileBrowserLayoutChecks
{
    public static void Run(ShellSettings settings)
    {
        var original = settings.ToPreferences();
        // Disconnected session and local fixtures keep layout checks independent of an SSH host.
        var view = new SshFileBrowserView(new SshDesktopSession(null!));
        Set("_path", "/home/demo/workspace");
        Set("_entries", new[] {
            new SshFileEntry("src", "/home/demo/workspace/src", true, false, 0, DateTime.Now),
            new SshFileEntry("photo.png", "/home/demo/workspace/photo.png", false, false, 12345, DateTime.Now),
            new SshFileEntry("app.cs", "/home/demo/workspace/app.cs", false, false, 120, DateTime.Now),
            new SshFileEntry(".hidden", "/home/demo/workspace/.hidden", false, false, 8, DateTime.Now) });
        Invoke("BuildBreadcrumbs"); Invoke("ApplyView");
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = canvas };
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        try
        {
            host.Show(); Pump(); manager.Attach(canvas); manager.SetHostBounds(new CoreRect(0, 0, 1100, 800));
            var window = manager.Create(new WindowCreateOptions(new AppId("test.ssh-files"), "SSH files", view, new CoreRect(10, 10, 960, 640)));
            view.AttachWindowHeader(window);
            foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike })
            foreach (var dark in new[] { false, true })
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (var width in new[] { 960, 760, 620, 480, 320 })
            {
                settings.SystemStyleId = style;
                settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                settings.Language = language;
                window.View.Width = width;
                window.Info.Bounds = new CoreRect(10, 10, width, 640);
                Pump(); host.UpdateLayout(); Pump();
                var bar = view.FindControl<Border>("CommandToolbar")!;
                var actions = bar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
                foreach (var action in actions)
                {
                    var point = action.TranslatePoint(default, bar)!.Value;
                    Check(point.X >= 0 && point.X + action.Bounds.Width <= bar.Bounds.Width,
                        $"{style}/{language}/{width}: SSH command {action.Name} overflows.");
                    Check(Math.Abs(point.Y - actions[0].TranslatePoint(default, bar)!.Value.Y) < 1, "SSH toolbar wraps.");
                }
                Check(view.FindControl<Button>("MoreActionsButton")!.IsEffectivelyVisible, "SSH overflow entry disappeared.");
                var navigation = view.FindControl<Border>("NavigationToolbar")!;
                Check(view.FindControl<Button>("CompactSearchButton")!.IsVisible == (navigation.Bounds.Width < 600),
                    "SSH search did not adapt to the fused navigation width.");
                Check(view.FindControl<Grid>("FileContentGrid")!.ColumnDefinitions[0].ActualWidth == (view.Bounds.Width < 620 ? 0 : 210),
                    "SSH sidebar did not collapse in a narrow window.");
                Check(navigation.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                    .Where(path => Math.Abs(path.StrokeThickness - 1.2) < 0.01).All(path => path.Stroke is not null),
                    "Fusing SSH navigation lost toolbar icon strokes.");
                if (style == SystemStyleIds.WindowsLike && language == "en-US" && width is 960 or 320)
                {
                    var directory = Path.Combine(AppContext.BaseDirectory, "preview-qa", "ssh-files");
                    Directory.CreateDirectory(directory);
                    using var rendered = new RenderTargetBitmap(new PixelSize(width + 20, 660));
                    rendered.Render(host);
                    rendered.Save(Path.Combine(directory, $"{(dark ? "dark" : "light")}-{width}.png"), new PngBitmapEncoderOptions());
                }
            }
            view.FocusSearchBox(); Pump();
            var compact = view.FindControl<TextBox>("CompactSearchBox")!;
            Check(ReferenceEquals(host.FocusManager?.GetFocusedElement(), compact), "Compact SSH search did not focus its popup.");
            compact.Text = "photo"; Pump();
            Check(view.SearchText == "photo" && view.FindControl<DataGrid>("EntriesGrid")!.ItemsSource.Cast<SshFileEntry>().Single().Name == "photo.png",
                "Compact SSH search does not filter the same entries as inline search.");
            host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Check(view.SearchText == "", "Escape did not clear SSH filtering.");
            var entries = view.FindControl<DataGrid>("EntriesGrid")!;
            entries.SelectedItem = entries.ItemsSource.Cast<SshFileEntry>().First(file => !file.IsDirectory); Pump();
            Check(view.FindControl<MenuItem>("OverflowCopy")!.IsEnabled && view.FindControl<MenuItem>("OverflowRename")!.IsEnabled,
                "Overflow operations did not track the selected SSH file.");
            entries.SelectedItem = null; Pump();
            Check(!view.FindControl<MenuItem>("OverflowCopy")!.IsEnabled && !view.FindControl<MenuItem>("OverflowRename")!.IsEnabled,
                "Overflow operations stayed enabled after clearing selection.");
            var picker = new SshFileBrowserView(new SshDesktopSession(null!), selectPackage: _ => { });
            var pickerWindow = manager.Create(new WindowCreateOptions(new AppId("test.ssh-picker"), "Choose package", picker, new CoreRect(0, 0, 320, 480)));
            picker.AttachWindowHeader(pickerWindow); Pump();
            Check(!pickerWindow.View.HasTitleBarContent && !picker.FindControl<StackPanel>("FileActions")!.IsVisible,
                "Package picker acquired app chrome or file mutation buttons.");
            Console.WriteLine("PASS: SSH file browser toolbar across styles, themes, languages and 5 widths; compact filtering, icon strokes and picker isolation.");
        }
        finally { host.Close(); settings.Apply(original); Pump(); }

        void Set(string name, object value) => typeof(SshFileBrowserView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
        void Invoke(string name) => typeof(SshFileBrowserView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
