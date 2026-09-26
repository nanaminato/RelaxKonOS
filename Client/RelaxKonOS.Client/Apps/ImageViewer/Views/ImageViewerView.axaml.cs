using Avalonia.Controls;
using RelaxKonOS.Client.Apps.ImageViewer.ViewModels;

namespace RelaxKonOS.Client.Apps.ImageViewer.Views;

public partial class ImageViewerView : UserControl
{
    public ImageViewerView()
    {
        InitializeComponent();
        ImageViewport.SizeChanged += (_, _) => ReportViewportSize();
        AttachedToVisualTree += (_, _) => ReportViewportSize();
    }

    private void ReportViewportSize()
    {
        if (DataContext is ImageViewerViewModel viewModel)
            viewModel.SetViewportSize(ImageViewport.Bounds.Width, ImageViewport.Bounds.Height);
    }
}
