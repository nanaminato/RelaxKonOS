using Avalonia.Controls;
using RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Views;

/// <summary>
/// The deployment wizard dialog. It owns the dialog handle, so closing is the view's concern: the
/// view model only raises <see cref="DeploymentWizardViewModel.CloseRequested"/> once it has stopped
/// observing the operation.
/// </summary>
internal partial class DeploymentWizardView : UserControl
{
    // Leave space for the vertical scrollbar and a small visual gutter. A ScrollViewer otherwise
    // measures its vertical content at infinite width, which turns star columns into Auto columns.
    private const double WizardRightInset = 28;

    public DeploymentWizardView(DeploymentWizardViewModel viewModel, ModalDialog<bool> dialog)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Follow new output unless the operator is focusing the pane to inspect/copy a line.
        LiveLogBox.TextChanged += (_, _) =>
        {
            if (!LiveLogBox.IsFocused) LiveLogBox.CaretIndex = LiveLogBox.Text?.Length ?? 0;
        };
        viewModel.CloseRequested = () => dialog.Close(true);
        WizardScrollViewer.SizeChanged += (_, _) => UpdateWizardContentWidth();
        AttachedToVisualTree += (_, _) => UpdateWizardContentWidth();
        DetachedFromVisualTree += (_, _) => viewModel.StopObserving();
    }

    private void UpdateWizardContentWidth()
    {
        var width = WizardScrollViewer.Bounds.Width - WizardRightInset;
        WizardContent.Width = width > 0 ? width : double.NaN;
    }
}
