using Avalonia.Controls;
using Avalonia.Media.Imaging;
using RelaxKonOS.Core.Input;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.ImageViewer;

public partial class ImageViewerView : UserControl
{
    private static readonly Lazy<Bitmap> Icon = new(() =>
    {
        using var stream = typeof(ImageViewerView).Assembly.GetManifestResourceStream("ImageViewer.Icon.png")!;
        return new Bitmap(stream);
    });

    public void AttachWindow(ManagedWindow window)
    {
        if (DataContext is not ImageViewerViewModel viewModel)
            throw new InvalidOperationException("The image viewer requires an ImageViewerViewModel.");

        Header.DataContext = viewModel;
        Header.Attach(window, HeaderActions);
        window.Title = viewModel.ViewerTitle;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageViewerViewModel.ViewerTitle)) window.Title = viewModel.ViewerTitle;
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Modifiers == RemoteKeyModifiers.Control && e.Key.Value is "Add" or "OemPlus")
                WindowShortcut.TryExecute(e, e.Key, RemoteKeyModifiers.Control, viewModel.ZoomInCommand);
            else if (e.Modifiers == RemoteKeyModifiers.Control && e.Key.Value is "Subtract" or "OemMinus")
                WindowShortcut.TryExecute(e, e.Key, RemoteKeyModifiers.Control, viewModel.ZoomOutCommand);
            else if (e.Key == RemoteKey.Letter('F'))
                WindowShortcut.TryExecute(e, RemoteKey.Letter('F'), RemoteKeyModifiers.None, viewModel.FitToViewCommand);
            else
                WindowShortcut.TryExecute(e, RemoteKey.Digit(0), RemoteKeyModifiers.Control, viewModel.ResetZoomCommand);
        };
    }

    public ImageViewerView()
    {
        InitializeComponent();
        Header.IconSource = Icon.Value;
        SizeChanged += (_, e) => { ZoomLabel.IsVisible = e.NewSize.Width >= 600; ShortcutHint.IsVisible = e.NewSize.Width >= 640; };
        ImageViewport.SizeChanged += (_, _) => ReportViewportSize();
        AttachedToVisualTree += (_, _) => ReportViewportSize();
    }

    private void ReportViewportSize()
    {
        EmptyHint.MaxWidth = Math.Max(80, ImageViewport.Bounds.Width - 72);
        if (DataContext is ImageViewerViewModel viewModel)
            viewModel.SetViewportSize(ImageViewport.Bounds.Width, ImageViewport.Bounds.Height);
    }
}
