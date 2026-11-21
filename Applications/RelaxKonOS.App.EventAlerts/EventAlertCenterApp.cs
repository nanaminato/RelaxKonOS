using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.App.EventAlerts;

public sealed class EventAlertCenterApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.event-alerts"),
        "Event & Alert Center", "1.0.0", "⚠", "View operational alerts on the connected server",
        [AppPermissions.ServerEventAlertsRead], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = new DockPanel { Margin = new Thickness(18) };
        var refresh = new Button { HorizontalAlignment = HorizontalAlignment.Right };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold,
            FontSize = 18, Margin = new Thickness(0, 0, 0, 12) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var list = new ListBox();
        DockPanel.SetDock(refresh, Dock.Top);
        DockPanel.SetDock(summary, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(refresh);
        root.Children.Add(summary);
        root.Children.Add(status);
        root.Children.Add(list);

        var language = context.SystemLanguage;
        var strings = ReadStrings(language.CurrentLanguage);
        var window = context.Windows.ShowWindow(strings["title"], root, new Rect(100, 75, 950, 640), Manifest.IconGlyph);
        var closed = window.Closed;
        ServerAlertsResult? current = null;

        void UpdateText()
        {
            refresh.Content = current?.Status == AppCapabilityResult.PermissionDenied ? strings["grant"] : strings["refresh"];
            var snapshot = current?.Snapshot;
            summary.Text = snapshot is null ? "" : string.Format(strings["summary"],
                snapshot.OpenCount, snapshot.AcknowledgedCount, snapshot.CriticalCount);
            status.Text = current?.Status switch
            {
                AppCapabilityResult.PermissionDenied => strings["denied"],
                AppCapabilityResult.Unavailable => strings["unavailable"],
                AppCapabilityResult.Succeeded when snapshot?.Alerts.Count == 0 => strings["empty"],
                _ => "",
            };
        }

        async Task LoadAsync(bool requestPermission)
        {
            refresh.IsEnabled = false;
            try
            {
                if (requestPermission && current?.Status == AppCapabilityResult.PermissionDenied)
                    await context.Permissions.RequestAsync(AppPermissions.ServerEventAlertsRead, closed);
                current = await context.ServerAlerts.ReadAsync(closed);
                if (closed.IsCancellationRequested) return;
                list.ItemsSource = current.Snapshot?.Alerts.Select(alert =>
                    $"[{alert.Severity}] {alert.Type} — {alert.ProblemCode} — {alert.Status} ({alert.OccurrenceCount})").ToArray();
                UpdateText();
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
            finally { refresh.IsEnabled = !closed.IsCancellationRequested; }
        }

        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (closed.IsCancellationRequested) return;
                strings = ReadStrings(args.CurrentLanguage);
                window.Window.Title = strings["title"];
                UpdateText();
            });
        }

        language.LanguageChanged += LanguageChanged;
        closed.Register(() => language.LanguageChanged -= LanguageChanged);
        refresh.Click += async (_, _) => await LoadAsync(requestPermission: true);
        UpdateText();
        return LoadAsync(requestPermission: false);
    }

    private static Dictionary<string, string> ReadStrings(string culture)
    {
        var language = culture.Split('-', 2)[0].ToLowerInvariant() switch
        {
            "ja" => "ja-JP",
            "zh" => "zh-CN",
            _ => "en-US",
        };
        using var stream = typeof(EventAlertCenterApp).Assembly.GetManifestResourceStream($"EventAlerts.Localization.{language}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
