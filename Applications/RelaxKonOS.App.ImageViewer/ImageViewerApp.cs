using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.UI.Imaging;

namespace RelaxKonOS.App.ImageViewer;

/// <summary>Package entry point for Image Viewer.</summary>
public class ImageViewerApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public virtual ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.imageviewer"),
        "Image Viewer", "1.0.0", IconGlyph: "🖼️", RequestedPermissions: [AppPermissions.ServerFilesRead],
        SupportedFileExtensions: ImageDecoder.SupportedExtensions);

    protected virtual string? TitleOverride(string? path) => null;

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default) =>
        OpenViewerAsync(context, null, cancellationToken);

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default) =>
        OpenViewerAsync(context, path, cancellationToken);

    private async Task OpenViewerAsync(IExternalAppContext context, string? path, CancellationToken cancellationToken)
    {
        var model = new ImageViewerViewModel(context.SystemLanguage,
            (file, token) => ReadImageAsync(context.ServerFiles, file, token), isSshSession: context.ServerFiles.IsSshSession, title: TitleOverride(path));
        IExternalAppWindowHandle window;
        try
        {
            var view = new ImageViewerView { DataContext = model };
            window = context.Windows.ShowWindow(model.ViewerTitle, view, bounds: new Rect(180, 100, 880, 640), iconGlyph: Manifest.IconGlyph);
            window.Closed.Register(model.Dispose);
            view.AttachWindow(window.Window);
        }
        catch
        {
            model.Dispose();
            throw;
        }

        if (string.IsNullOrWhiteSpace(path)) return;

        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Closed);
        try
        {
            if (context.Permissions.GetStatus(AppPermissions.ServerFilesRead) == AppPermissionStatus.Undecided)
                await context.Permissions.RequestAsync(AppPermissions.ServerFilesRead, opening.Token);
            await model.OpenPathAsync(path, opening.Token);
        }
        catch (OperationCanceledException) when (opening.IsCancellationRequested) { }
    }

    private static async Task<byte[]?> ReadImageAsync(IServerFiles files, string path, CancellationToken cancellationToken)
    {
        var result = await files.OpenReadAsync(path, cancellationToken);
        if (result.Content is not { } content)
        {
            if (result.Status == AppCapabilityResult.PermissionDenied) throw new UnauthorizedAccessException();
            if (result.Status == AppCapabilityResult.InvalidArgument) return null;
            throw new IOException($"Server files are unavailable ({result.Status}).");
        }

        await using (content)
        {
            const int limit = 32 * 1024 * 1024;
            using var data = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (data.Length + read > limit) throw new InvalidDataException("The image exceeds 32 MiB.");
                data.Write(buffer, 0, read);
            }
            return data.ToArray();
        }
    }
}
