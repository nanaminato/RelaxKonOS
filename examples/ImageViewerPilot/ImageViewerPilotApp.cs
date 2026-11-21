using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Examples.ImageViewerPilot;

/// <summary>Package entry point for the Image Viewer migration pilot.</summary>
public sealed class ImageViewerPilotApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("com.relaxkonos.example.image-viewer"),
        "Image Viewer Pilot", "0.1.0-dev", RequestedPermissions: [AppPermissions.ServerFilesRead]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        context.Windows.ShowWindow(Manifest.DisplayName, new TextBlock
        {
            Text = "Open an image from Explorer using Open with → Image Viewer Pilot.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
        });
        return Task.CompletedTask;
    }

    public async Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default)
    {
        var body = new ContentControl { Content = "Opening image…", Padding = new Thickness(20) };
        var window = context.Windows.ShowWindow($"{Path.GetFileName(path)} · {Manifest.DisplayName}", body);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Closed);
        var token = linked.Token;
        try
        {
            var result = await context.ServerFiles.OpenReadAsync(path, token);
            if (result.Content is not { } content)
            {
                body.Content = result.Status == AppCapabilityResult.PermissionDenied
                    ? "Allow server file reads for Image Viewer Pilot in Settings."
                    : $"Could not open the image ({result.Status}).";
                return;
            }
            await using (content)
            {
                const int limit = 32 * 1024 * 1024;
                using var data = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, token)) != 0)
                {
                    if (data.Length + read > limit) throw new InvalidDataException("The image exceeds 32 MiB.");
                    data.Write(buffer, 0, read);
                }
                token.ThrowIfCancellationRequested();
                var image = ImageViewerDecoder.Load(path, data.ToArray());
                if (image is IDisposable disposable) window.Closed.Register(disposable.Dispose);
                body.Content = new Image { Source = image, Stretch = Stretch.Uniform };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { body.Content = exception.Message; }
    }
}
