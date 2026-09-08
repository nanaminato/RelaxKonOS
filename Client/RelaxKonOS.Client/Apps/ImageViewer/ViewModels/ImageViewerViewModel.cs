using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.ImageViewer.ViewModels;

/// <summary>Loads and displays a remote image using Avalonia's built-in bitmap decoder.</summary>
public sealed partial class ImageViewerViewModel : ObservableObject, IDisposable
{
    private readonly IExplorerClient? _files;
    private CancellationTokenSource? _loadCts;

    public ImageViewerViewModel(IExplorerClient? files) => _files = files;

    [ObservableProperty] private Bitmap? _imageSource;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _statusText = LocalizedText.Get("image_viewer.status.open_hint");
    [ObservableProperty] private int _pixelWidth;
    [ObservableProperty] private int _pixelHeight;
    [ObservableProperty] private int _zoomPercent = 100;
    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;

    public string DocumentName => string.IsNullOrWhiteSpace(CurrentPath) ? LocalizedText.Get("image_viewer.title") : Path.GetFileName(CurrentPath);
    public string DimensionsText => PixelWidth > 0 ? LocalizedText.Format("image_viewer.dimensions", PixelWidth, PixelHeight) : string.Empty;

    partial void OnCurrentPathChanged(string? value) => OnPropertyChanged(nameof(DocumentName));
    partial void OnPixelWidthChanged(int value) => OnPropertyChanged(nameof(DimensionsText));
    partial void OnPixelHeightChanged(int value) => OnPropertyChanged(nameof(DimensionsText));
    partial void OnZoomPercentChanged(int value) => UpdateDisplaySize();

    [RelayCommand]
    private void ZoomIn() => ZoomPercent = Math.Min(400, ZoomPercent + 25);

    [RelayCommand]
    private void ZoomOut() => ZoomPercent = Math.Max(25, ZoomPercent - 25);

    [RelayCommand]
    private void ResetZoom() => ZoomPercent = 100;

    public async Task OpenPathAsync(string path)
    {
        if (_files is null)
        {
            StatusText = LocalizedText.Get("image_viewer.status.connect_before_open");
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        StatusText = LocalizedText.Get("image_viewer.status.loading");

        try
        {
            var bytes = await _files.ReadFileAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                StatusText = LocalizedText.Get("image_viewer.status.file_missing");
                return;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (ct.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            var previous = ImageSource;
            ImageSource = bitmap;
            previous?.Dispose();
            CurrentPath = path;
            PixelWidth = bitmap.PixelSize.Width;
            PixelHeight = bitmap.PixelSize.Height;
            ZoomPercent = 100;
            UpdateDisplaySize();
            StatusText = LocalizedText.Format("image_viewer.status.opened", Path.GetFileName(path), DimensionsText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A more recent file-open request superseded this one.
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("image_viewer.status.open_failed", exception.Message);
        }
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        ImageSource?.Dispose();
    }

    private void UpdateDisplaySize()
    {
        DisplayWidth = PixelWidth * ZoomPercent / 100d;
        DisplayHeight = PixelHeight * ZoomPercent / 100d;
    }
}
