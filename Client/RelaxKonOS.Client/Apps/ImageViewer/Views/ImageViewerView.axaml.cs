using Avalonia.Controls;
using RelaxKonOS.Client.Apps.ImageViewer.ViewModels;

namespace RelaxKonOS.Client.Apps.ImageViewer.Views;

public partial class ImageViewerView : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    public ImageViewerView()
    {
        InitializeComponent();
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
