using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.EventAlerts;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.EventAlerts;

/// <summary>Built-in, read-first Event & Alert Center. The server remains the source of truth.</summary>
public sealed class EventAlertCenterApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.event-alerts"), "Event & Alert Center", "0.1.0", "⚠",
        "Browse safe operational events and current alerts on the RelaxKonOS Server", [AppPermissions.ServerEventAlertsRead],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteEventAlertClient)) as IRemoteEventAlertClient;
        if (session?.State != AuthSessionState.Authenticated || client is null)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.event-alerts.display_name", "Event & Alert Center"),
                new TextBlock { Text = LocalizedText.Get("event_alerts.sign_in", "Sign in to view operational alerts."), Margin = new Thickness(24) },
                new Rect(200, 160, 480, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var root = new DockPanel { Margin = new Thickness(18) };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) };
        var list = new ListBox();
        var refresh = new Button { Content = LocalizedText.Get("common.refresh", "Refresh"), HorizontalAlignment = HorizontalAlignment.Right };
        var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.Children.Add(refresh);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        root.Children.Add(list);
        async Task LoadAsync()
        {
            refresh.IsEnabled = false;
            try
            {
                var snapshot = await client.SummaryAsync();
                var alerts = await client.ListAlertsAsync();
                summary.Text = $"Open: {snapshot.OpenCount}   Acknowledged: {snapshot.AcknowledgedCount}   Critical: {snapshot.UnacknowledgedCriticalCount}";
                list.ItemsSource = alerts.Items.Select(Format).ToArray();
                status.Text = alerts.Items.Count == 0 ? LocalizedText.Get("event_alerts.empty", "No alerts match the current view.") : string.Empty;
            }
            catch
            {
                status.Text = LocalizedText.Get("event_alerts.load_failed", "Alert data is currently unavailable. Try again shortly.");
            }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await LoadAsync();
        context.ShowWindow(LocalizedText.Get("application.relaxkonos.event-alerts.display_name", "Event & Alert Center"), root, new Rect(100, 75, 950, 640), Manifest.IconGlyph);
        _ = LoadAsync();
    }

    private static string Format(OperationalAlertDto alert) => $"[{alert.Severity}] {alert.Type} — {alert.ProblemCode} — {alert.Status} ({alert.OccurrenceCount})";
}
