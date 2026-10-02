using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.WindowManager;

/// <summary>A bounded, shared snapshot for taskbar previews and the host's task view.</summary>
public sealed class WindowThumbnail(RemoteWindow view) : ObservableObject, IDisposable
{
    private RenderTargetBitmap? _image;
    private long _lastCapture;
    private bool _disposed;

    public IImage? Image => _image;
    public bool HasImage => _image is not null;

    /// <summary>
    /// Capture only on demand. Hidden/minimized windows keep their last visible frame;
    /// native child surfaces cannot be captured by Avalonia and use the icon/title presentation.
    /// </summary>
    public void Refresh(bool force = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !view.IsVisible || TopLevel.GetTopLevel(view) is null) return;
        var now = Environment.TickCount64;
        if (!force && now - _lastCapture < 300) return;
        _lastCapture = now;
        if (view.GetVisualDescendants().Any(child => child is NativeControlHost))
        {
            Replace(null);
            return;
        }

        var size = view.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        // Render at most 480 x 300 pixels, including chrome, without allocating a full-size frame.
        var scale = Math.Min(1, Math.Min(480 / size.Width, 300 / size.Height));
        RenderTargetBitmap? next = null;
        try
        {
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
            next = new RenderTargetBitmap(pixels, new Vector(96, 96));
            using (var drawing = next.CreateDrawingContext())
                drawing.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.Fill }, null,
                    new Rect(0, 0, pixels.Width, pixels.Height));
            Replace(next);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            next?.Dispose();
            Trace.TraceWarning("Window thumbnail capture unavailable: {0}", exception.Message);
            Replace(null);
        }
    }

    private void Replace(RenderTargetBitmap? next)
    {
        var previous = _image;
        if (ReferenceEquals(previous, next)) return;
        _image = next;
        OnPropertyChanged(nameof(Image));
        if ((previous is null) != (next is null)) OnPropertyChanged(nameof(HasImage));
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Replace(null);
    }
}
