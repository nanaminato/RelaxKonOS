using RelaxKonOS.App.ImageViewer;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.Examples.ImageViewerPilot;

/// <summary>Package entry point for the Image Viewer migration pilot.</summary>
public sealed class ImageViewerPilotApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("com.relaxkonos.example.image-viewer"),
        "Image Viewer Pilot", "0.1.0-dev", RequestedPermissions: [AppPermissions.ServerFilesRead],
        SupportedFileExtensions: ImageViewerDecoder.SupportedExtensions);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default) =>
        OpenViewerAsync(context, null, cancellationToken);

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default) =>
        OpenViewerAsync(context, path, cancellationToken);

    private async Task OpenViewerAsync(IExternalAppContext context, string? path, CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(path) ? Manifest.DisplayName : $"{Path.GetFileName(path)} · {Manifest.DisplayName}";
        var model = new ImageViewerViewModel(context.SystemLanguage,
            (file, token) => ReadImageAsync(context.ServerFiles, file, token), title: title);
        try
        {
            var view = new ImageViewerView { DataContext = model };
            var window = context.Windows.ShowWindow(model.ViewerTitle, view, bounds: new Rect(180, 100, 880, 640));
            window.Closed.Register(model.Dispose);
            view.AttachWindow(window.Window);
        }
        catch
        {
            model.Dispose();
            throw;
        }

        if (!string.IsNullOrWhiteSpace(path)) await model.OpenPathAsync(path, cancellationToken);
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
