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
    public DeploymentWizardView(DeploymentWizardViewModel viewModel, ModalDialog<bool> dialog)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested = () => dialog.Close(true);
    }
}
