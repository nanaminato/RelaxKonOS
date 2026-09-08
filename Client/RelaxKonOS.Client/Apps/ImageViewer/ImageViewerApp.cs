using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.ImageViewer.ViewModels;
using RelaxKonOS.Client.Apps.ImageViewer.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ImageViewer;

/// <summary>Lightweight built-in viewer for common remote image files.</summary>
public sealed class ImageViewerApp : RemoteApplicationBase, IFileOpenApplication
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".dib", ".webp", ".ico"];

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.imageviewer"),
        DisplayName: "Image Viewer",
        Version: "1.0.0",
        IconGlyph: "🖼️",
        Description: "Lightweight viewer for remote image files",
        RequestedPermissions: [AppPermissions.ServerFilesRead],
        SupportedFileExtensions: SupportedExtensions);

    public override void Activate(AppContext context) => OpenViewer(context, null);

    public void OpenFile(AppContext context, string path) => OpenViewer(context, path);

    private void OpenViewer(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var viewModel = new ImageViewerViewModel(files);
        var view = new ImageViewerView { DataContext = viewModel };
        var window = context.ShowWindow("Image Viewer", view,
            bounds: new Rect(180, 100, 880, 640),
            iconGlyph: Manifest.IconGlyph);
        window.KeyDown += (_, e) =>
        {
            if (e.Modifiers == RemoteKeyModifiers.Control && e.Key.Value is "Add" or "OemPlus")
                WindowShortcut.TryExecute(e, e.Key, RemoteKeyModifiers.Control, viewModel.ZoomInCommand);
            else if (e.Modifiers == RemoteKeyModifiers.Control && e.Key.Value is "Subtract" or "OemMinus")
                WindowShortcut.TryExecute(e, e.Key, RemoteKeyModifiers.Control, viewModel.ZoomOutCommand);
            else
                WindowShortcut.TryExecute(e, RemoteKey.Digit(0), RemoteKeyModifiers.Control, viewModel.ResetZoomCommand);
        };

        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
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
