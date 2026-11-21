using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.App.ImageViewer;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ImageViewer;

/// <summary>Lightweight built-in viewer for common remote image files.</summary>
public sealed class ImageViewerApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.imageviewer"),
        DisplayName: "Image Viewer",
        Version: "1.0.0",
        IconGlyph: "🖼️",
        Description: "Lightweight viewer for remote image files",
        RequestedPermissions: [AppPermissions.ServerFilesRead],
        SupportedFileExtensions: ImageViewerDecoder.SupportedExtensions);

    public override void Activate(AppContext context) => OpenViewer(context, null);

    public void OpenFile(AppContext context, string path) => OpenViewer(context, path);

    private void OpenViewer(AppContext context, string? path)
    {
        var sshDesktop = context.Services.GetService(typeof(SshDesktopSession)) as SshDesktopSession;
        IExplorerClient? files = sshDesktop?.IsConnected == true
            ? context.Services.GetService(typeof(SshExplorerClient)) as SshExplorerClient
            : context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var localization = context.Services.GetRequiredService<LocalizationService>();
        var viewModel = new ImageViewerViewModel(localization, files is null ? null : files.ReadFileAsync,
            sshDesktop?.IsConnected == true, localization.Get);
        ManagedWindow window;
        try
        {
            var view = new ImageViewerView { DataContext = viewModel };
            window = context.ShowWindow(viewModel.ViewerTitle, view, bounds: new Rect(180, 100, 880, 640), iconGlyph: Manifest.IconGlyph);
            view.AttachWindow(window);
        }
        catch
        {
            viewModel.Dispose();
            throw;
        }

        EventHandler<ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;

        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }
}
