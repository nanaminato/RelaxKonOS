using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;

internal static class SettingsInteractionChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var original = settings.Language;
        var session = DispatchProxy.Create<IAuthSession, LanguageSessionStub>();
        using var vm = new TimeLanguagePageViewModel(settings, localization, null,
            new HostTimeEditorViewModel(null!, session, localization));
        var view = new TimeLanguagePageView { DataContext = vm };
        var scroll = new ScrollViewer { Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var window = new Window { Content = scroll, Width = 640, Height = 480 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var id in new[] { "workspace.timeFormat", "workspace.dateFormat", "workspace.language", "workspace.region", "host.time.zone" })
            Check(SettingsSearchTarget.Find(view, id) is not null, $"No real control target for {id}.");
        Check(SettingsSearchTarget.Find(view, "remote.arbitrary-control") is null, "Remote metadata selected an unregistered control.");
        var region = SettingsSearchTarget.Find(view, "workspace.region")!;
        var highlight = SettingsSearchTarget.HighlightAndFocus(region);
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(window.FocusManager?.GetFocusedElement(), region), "Region search did not focus its input.");
        Check(scroll.Offset.Y > 0, "Region search did not scroll to the lower section.");
        Check(highlight.Target.Classes.Contains("settings-search-target"), "Search target has no highlight.");
        highlight.Dispose();
        var zone = SettingsSearchTarget.Find(view, "host.time.zone")!;
        Check(!zone.IsEnabled, "Unauthenticated host time input should be disabled.");
        highlight = SettingsSearchTarget.HighlightAndFocus(zone, "Host time zone");
        Dispatcher.UIThread.RunJobs();
        Check(!zone.IsEnabled && window.FocusManager?.GetFocusedElement() is Control { IsEffectivelyEnabled: true },
            "Search altered host permissions or stranded keyboard focus on a disabled control.");
        var section = highlight.Target;
        Check(AutomationProperties.GetName(section) == "Host time zone", "Read-only section has no accessible search title.");
        highlight.Dispose();
        Check(!section.Classes.Contains("settings-search-target") && !section.Focusable, "Reveal cleanup changed the normal tab order.");
        var hidden = new TextBox { IsVisible = false };
        SettingsSearchTarget.SetId(hidden, "hidden");
        ((StackPanel)view.Content!).Children.Add(hidden);
        Check(SettingsSearchTarget.Find(view, "hidden") is null, "Hidden setting was reported as located.");
        foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = culture;
            Dispatcher.UIThread.RunJobs();
            Check(!localization.Get("settings.search.location_unavailable", "missing").Equals("missing"), "Missing translated location status.");
            Check(ReferenceEquals(SettingsSearchTarget.Find(view, "workspace.region"), region), "Language change invalidated the setting target.");
        }
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "settings");
        Directory.CreateDirectory(output);
        foreach (var size in new[] { new PixelSize(640, 480), new PixelSize(1024, 768), new PixelSize(1440, 900) })
        {
            window.Width = size.Width; window.Height = size.Height;
            scroll.Offset = default;
            Dispatcher.UIThread.RunJobs();
            using var screenshot = new RenderTargetBitmap(size);
            screenshot.Render(scroll);
            screenshot.Save(Path.Combine(output, $"time-language-{size.Width}x{size.Height}.png"), PngBitmapEncoderOptions.Default);
        }
        // A 320x240 logical viewport at 2x density exercises the 640x480 magnified case.
        window.Width = 320; window.Height = 240;
        scroll.Offset = default;
        Dispatcher.UIThread.RunJobs();
        Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "Magnified time page requires horizontal scrolling.");
        foreach (var id in new[] { "workspace.timeFormat", "workspace.dateFormat", "workspace.language", "workspace.region" })
        {
            var control = SettingsSearchTarget.Find(view, id)!;
            var origin = control.TranslatePoint(default, scroll)!.Value;
            Check(origin.X >= 0 && origin.X + control.Bounds.Width <= scroll.Bounds.Width + 1,
                $"Magnified setting {id} clips horizontally.");
        }
        using (var screenshot = new RenderTargetBitmap(new PixelSize(640, 480), new Vector(192, 192)))
        {
            screenshot.Render(scroll);
            screenshot.Save(Path.Combine(output, "time-language-200-percent.png"), PngBitmapEncoderOptions.Default);
        }
        window.Close();
        settings.Language = original;
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine("PASS: Settings real targets, scrolling, keyboard focus, disabled/hidden controls and three languages. Headless page screenshots saved.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
