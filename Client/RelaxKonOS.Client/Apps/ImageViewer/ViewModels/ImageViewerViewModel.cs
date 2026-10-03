using Avalonia.Media;
using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.ImageViewer.ViewModels;

/// <summary>Loads remote bitmap and vector images for display.</summary>
public sealed partial class ImageViewerViewModel : LocalizedObservableObject, IDisposable
{
    private readonly IExplorerClient? _files;
    private readonly bool _isSshSession;
    private CancellationTokenSource? _loadCts;
    private double _viewportWidth;
    private double _viewportHeight;

    public ImageViewerViewModel(IExplorerClient? files, bool isSshSession = false)
    {
        _files = files;
        _isSshSession = isSshSession;
    }

    [ObservableProperty] private IImage? _imageSource;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.Ref("image_viewer.status.open_hint");
    [ObservableProperty] private int _pixelWidth;
    [ObservableProperty] private int _pixelHeight;
    [ObservableProperty] private int _zoomPercent = 100;
    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
    [ObservableProperty] private bool _isLoading;

    public string DocumentName => string.IsNullOrWhiteSpace(CurrentPath) ? LocalizedText.Get("image_viewer.title") : Path.GetFileName(CurrentPath);
    public string DimensionsText => PixelWidth > 0 ? LocalizedText.Format("image_viewer.dimensions", PixelWidth, PixelHeight) : string.Empty;
    public string ImageFormatText => string.IsNullOrWhiteSpace(CurrentPath)
        ? string.Empty
        : Path.GetExtension(CurrentPath).TrimStart('.').ToUpperInvariant();
    public string SessionText => _isSshSession
        ? T("image_viewer.session.ssh", "SSH desktop")
        : T("image_viewer.session.server", "RelaxKonOS Server");
    public bool HasImage => ImageSource is not null;

    partial void OnCurrentPathChanged(string? value)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(ImageFormatText));
    }
    partial void OnPixelWidthChanged(int value) => OnPropertyChanged(nameof(DimensionsText));
    partial void OnPixelHeightChanged(int value) => OnPropertyChanged(nameof(DimensionsText));
    partial void OnZoomPercentChanged(int value) => UpdateDisplaySize();
    partial void OnImageSourceChanged(IImage? value) => OnPropertyChanged(nameof(HasImage));

    [RelayCommand]
    private void ZoomIn() => ZoomPercent = Math.Min(400, ZoomPercent + 25);

    [RelayCommand]
    private void ZoomOut() => ZoomPercent = Math.Max(25, ZoomPercent - 25);

    [RelayCommand]
    private void ResetZoom() => ZoomPercent = 100;

    [RelayCommand]
    private void FitToView()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0 || _viewportWidth <= 0 || _viewportHeight <= 0) return;

        // Keep breathing room around the image so it reads as an object on the canvas,
        // rather than as content pressed directly against the scroll viewer edges.
        var scale = Math.Min((_viewportWidth - 72) / PixelWidth, (_viewportHeight - 72) / PixelHeight);
        ZoomPercent = Math.Clamp((int)Math.Floor(scale * 100), 10, 400);
    }

    public void SetViewportSize(double width, double height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
    }

    public async Task OpenPathAsync(string path)
    {
        if (_files is null)
        {
            StatusText = LocalizedText.Ref("image_viewer.status.connect_before_open");
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsLoading = true;
        StatusText = LocalizedText.Ref("image_viewer.status.loading");

        try
        {
            var bytes = await _files.ReadFileAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                StatusText = LocalizedText.Ref("image_viewer.status.file_missing");
                return;
            }

            var image = ImageViewerDecoder.Load(path, bytes);
            if (ct.IsCancellationRequested)
            {
                (image as IDisposable)?.Dispose();
                return;
            }

            var previous = ImageSource;
            ImageSource = image;
            (previous as IDisposable)?.Dispose();
            CurrentPath = path;
            PixelWidth = image is Bitmap bitmap ? bitmap.PixelSize.Width : checked((int)Math.Ceiling(image.Size.Width));
            PixelHeight = image is Bitmap raster ? raster.PixelSize.Height : checked((int)Math.Ceiling(image.Size.Height));
            ZoomPercent = 100;
            FitToView();
            StatusText = LocalizedText.Ref("image_viewer.status.opened", Path.GetFileName(path), DimensionsText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A more recent file-open request superseded this one.
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Ref("image_viewer.status.open_failed", exception.Message);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        (ImageSource as IDisposable)?.Dispose();
    }

    private void UpdateDisplaySize()
    {
        DisplayWidth = PixelWidth * ZoomPercent / 100d;
        DisplayHeight = PixelHeight * ZoomPercent / 100d;
    }
}
