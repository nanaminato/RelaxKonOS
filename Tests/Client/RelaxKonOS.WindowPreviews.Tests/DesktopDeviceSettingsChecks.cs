using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Apps.Settings.Views;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Theming;

internal static class DesktopDeviceSettingsChecks
{
    public static void Run(ShellSettings settings, AppearanceService appearance)
    {
        var directory = Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "device-settings");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        var blocker = path + ".blocked";
        var preferences = new DesktopDevicePreferences(path);
        var reducedMotion = appearance.ReducedMotion;
        var contrast = appearance.HighContrast;
        Application.Current!.Resources.TryGetResource("DesktopInterfaceTransform", null, out var originalTransform);
        using var runtime = new DesktopAccessibilityService(Application.Current!, preferences, appearance);
        try
        {
            using var accessibility = new AccessibilityPageViewModel(settings, preferences);
            using var daily = new DailySettingsPageViewModel(settings, preferences);
            accessibility.InterfaceScale = 150;
            accessibility.ReducedMotion = true;
            accessibility.HighContrast = true;
            Check(!preferences.SaveFailed && appearance.ReducedMotion && appearance.HighContrast, "Device choices apply immediately");
            Check(Application.Current.Resources.TryGetResource("DesktopInterfaceTransform", null, out var transform)
                && transform is ScaleTransform { ScaleX: 1.5, ScaleY: 1.5 }, "Desktop scale is applied");
            Check(Application.Current.Resources.TryGetResource("TextPrimaryColor", null, out var text) && text is Color color
                && color == Colors.White, "Contrast uses the live resource graph");
            daily.RestoreTerminals = false;
            daily.KeepConnectionBarVisible = true;
            preferences.Update(p => p with { PinnedSettings = ["home", "personalization/colors", "personalization/colors", "invalid"] });
            var loaded = new DesktopDevicePreferences(path);
            Check(loaded.Value.InterfaceScale == 150 && loaded.Value.ReducedMotion && loaded.Value.HighContrast
                && !loaded.Value.RestoreTerminals && loaded.Value.KeepConnectionBarVisible
                && loaded.Value.PinnedSettings.SequenceEqual(new[] { "personalization/colors" }), "Persisted choices and route normalization");

            var session = DispatchProxy.Create<IAuthSession, SettingsWindowSession>();
            var auth = (SettingsWindowSession)session;
            auth.State = AuthSessionState.Authenticated;
            using var notifications = new DesktopNotificationService(preferences, session, new SilentDiagnostics());
            notifications.Notify("Test", "Visible"); Dispatcher.UIThread.RunJobs();
            Check(notifications.HasNotification, "Notifications enabled");
            daily.DoNotDisturb = true; Dispatcher.UIThread.RunJobs();
            Check(!notifications.HasNotification, "Do not disturb removes existing banner");
            notifications.Notify("Test", "Suppressed"); Dispatcher.UIThread.RunJobs();
            Check(!notifications.HasNotification, "Do not disturb suppresses future banners");
            daily.DoNotDisturb = false;
            notifications.Notify("Test", "Old workspace"); auth.SwitchWorkspace(); Dispatcher.UIThread.RunJobs();
            Check(!notifications.HasNotification, "Workspace changes invalidate queued notifications");
            daily.NotificationsEnabled = false;
            notifications.Notify("Test", "Disabled"); Dispatcher.UIThread.RunJobs();
            Check(!notifications.HasNotification, "Notification master switch");

            var window = new Window { Width = 640, Height = 480, Content = new AccessibilityPageView { DataContext = accessibility } };
            window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                Check(SettingsSearchTarget.Find(window, "client.interfaceScale") is not null
                    && SettingsSearchTarget.Find(window, "client.reducedMotion") is not null
                    && SettingsSearchTarget.Find(window, "client.highContrast") is not null, "Accessibility controls and search targets");
                window.Content = new DailySettingsPageView { DataContext = daily }; Dispatcher.UIThread.RunJobs();
                Check(SettingsSearchTarget.Find(window, "client.notifications") is not null
                    && SettingsSearchTarget.Find(window, "client.restoreTerminals") is not null, "Daily settings controls and search targets");
            }
            finally { window.Close(); }
            accessibility.ResetAccessibilityCommand.Execute(null);
            Check(preferences.Value.InterfaceScale == 100 && !appearance.HighContrast && !appearance.ReducedMotion
                && !preferences.Value.RestoreTerminals, "Accessibility reset leaves startup settings alone");
            daily.ResetDailySettingsCommand.Execute(null);
            Check(preferences.Value.RestoreTerminals && preferences.Value.NotificationsEnabled && !preferences.Value.DoNotDisturb
                && !preferences.Value.KeepConnectionBarVisible && preferences.Value.PinnedSettings.Length == 1, "Daily reset leaves pins alone");

            File.WriteAllText(blocker, "not a directory");
            var failed = new DesktopDevicePreferences(Path.Combine(blocker, "prefs.json"));
            failed.Update(p => p with { InterfaceScale = 125 });
            Check(failed.SaveFailed && failed.Value.InterfaceScale == 125, "Failed persistence preserves live choices and exposes retry");
            File.WriteAllText(path, "{ invalid");
            Check(new DesktopDevicePreferences(path).Value.InterfaceScale == 100, "Malformed local preference file uses defaults");
            Console.WriteLine("PASS: Device preference persistence, accessibility resources, grouped reset, notification suppression and search targets.");
        }
        finally
        {
            appearance.SetAccessibility(reducedMotion, contrast);
            Application.Current.Resources["DesktopInterfaceTransform"] = originalTransform ?? new ScaleTransform(1, 1);
            File.Delete(path); File.Delete(blocker);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class SilentDiagnostics : IAppActivationDiagnostics { public void Record(string message) { } }
}
