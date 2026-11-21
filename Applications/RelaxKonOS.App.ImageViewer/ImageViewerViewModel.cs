using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.UI.Imaging;

namespace RelaxKonOS.App.ImageViewer;

/// <summary>Loads remote bitmap and vector images for display.</summary>
public sealed partial class ImageViewerViewModel : ObservableObject, IDisposable
{
    private readonly ISystemLanguage _language;
    private readonly Func<string, CancellationToken, Task<byte[]?>>? _readFile;
    private readonly bool _isSshSession;
    private readonly string? _title;
    private CancellationTokenSource? _loadCts;
    private string _statusKey = "image_viewer.status.open_hint";
    private string? _error;
    private bool _disposed;
    private double _viewportWidth;
    private double _viewportHeight;

    public ImageViewerViewModel(ISystemLanguage language, Func<string, CancellationToken, Task<byte[]?>>? readFile,
        bool isSshSession = false, string? title = null)
    {
        _language = language;
        _readFile = readFile;
        _isSshSession = isSshSession;
        _title = title;
        _language.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty] private IImage? _imageSource;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private int _pixelWidth;
    [ObservableProperty] private int _pixelHeight;
    [ObservableProperty] private int _zoomPercent = 100;
    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
    [ObservableProperty] private bool _isLoading;

    public string ViewerTitle => _title ?? Text("image_viewer.title");
    public string DocumentName => string.IsNullOrWhiteSpace(CurrentPath) ? ViewerTitle : Path.GetFileName(CurrentPath);
    public string DimensionsText => PixelWidth > 0 ? Format("image_viewer.dimensions", PixelWidth, PixelHeight) : string.Empty;
    public string ImageFormatText => string.IsNullOrWhiteSpace(CurrentPath)
        ? string.Empty
        : Path.GetExtension(CurrentPath).TrimStart('.').ToUpperInvariant();
    public string SessionText => _isSshSession ? Text("image_viewer.session.ssh") : Text("image_viewer.session.server");
    public string ZoomInText => Text("image_viewer.zoom_in");
    public string ZoomOutText => Text("image_viewer.zoom_out");
    public string MoreText => Text("image_viewer.more");
    public string FitText => Text("image_viewer.fit_to_view");
    public string ResetZoomText => Text("image_viewer.reset_zoom");
    public string ShortcutsText => Text("image_viewer.shortcuts");
    public string StatusText => _statusKey switch
    {
        "image_viewer.status.opened" => Format(_statusKey, DocumentName, DimensionsText),
        "image_viewer.status.open_failed" => Format(_statusKey, _error),
        _ => Text(_statusKey),
    };
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

    public async Task OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested) return;
        if (_readFile is null)
        {
            SetStatus("image_viewer.status.connect_before_open");
            return;
        }

        _loadCts?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loadCts = load;
        var ct = load.Token;
        IsLoading = true;
        SetStatus("image_viewer.status.loading");

        try
        {
            var bytes = await _readFile(path, ct);
            ct.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                SetStatus("image_viewer.status.file_missing");
                return;
            }

            var image = ImageDecoder.Load(path, bytes);
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
            UpdateDisplaySize();
            SetStatus("image_viewer.status.opened");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer request or window close cancelled this load.
        }
        catch (UnauthorizedAccessException)
        {
            if (!ct.IsCancellationRequested) SetStatus("image_viewer.status.permission_denied");
        }
        catch (Exception exception)
        {
            if (!ct.IsCancellationRequested) SetStatus("image_viewer.status.open_failed", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, load))
            {
                _loadCts = null;
                IsLoading = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _language.LanguageChanged -= OnLanguageChanged;
        _loadCts?.Cancel();
        var image = ImageSource;
        ImageSource = null;
        (image as IDisposable)?.Dispose();
    }

    private string Text(string key) => ImageViewerText.Get(_language.CurrentLanguage, key);

    private string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    private void SetStatus(string key, string? error = null)
    {
        _statusKey = key;
        _error = error;
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshText();
        else Dispatcher.UIThread.Post(RefreshText);
    }

    private void RefreshText()
    {
        if (_disposed) return;
        foreach (var property in new[] { nameof(ViewerTitle), nameof(DocumentName), nameof(DimensionsText), nameof(SessionText),
                     nameof(ZoomInText), nameof(ZoomOutText), nameof(MoreText), nameof(FitText), nameof(ResetZoomText),
                     nameof(ShortcutsText), nameof(StatusText) })
            OnPropertyChanged(property);
    }

    private void UpdateDisplaySize()
    {
        DisplayWidth = PixelWidth * (ZoomPercent / 100d);
        DisplayHeight = PixelHeight * (ZoomPercent / 100d);
    }
}
