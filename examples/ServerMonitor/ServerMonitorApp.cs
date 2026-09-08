using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Examples.ServerMonitor.Services;
using RelaxKonOS.Examples.ServerMonitor.ViewModels;
using RelaxKonOS.Examples.ServerMonitor.Views;
using RemoteRect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Examples.ServerMonitor;

/// <summary>
/// Composition root for the Server Monitor development application.
/// </summary>
public sealed class ServerMonitorApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.server-monitor"),
        "Server Monitor",
        "0.2.0-dev",
        "📊",
        "A lightweight performance dashboard for a RelaxKonOS server",
        [AppPermissions.ServerMetricsRead]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var settingsStore = new MonitorSettingsStore();
        var viewModel = new ServerMonitorViewModel(context.ServerMonitor, settingsStore.Load(), settingsStore);
        var view = new ServerMonitorMainView { DataContext = viewModel };
        var window = context.Windows.ShowWindow("Server Monitor", view,
            new RemoteRect(120, 80, 1080, 760), Manifest.IconGlyph);
        _ = viewModel.StartAsync(window.Closed);
        return Task.CompletedTask;
    }
}
