using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.Views;

/// <summary>
/// Application-deployment shell. The layout lives in AXAML and binds straight to the view model; the
/// window-open and confirmation hooks are supplied by the app shell, so this class only attaches the
/// data context.
/// </summary>
internal partial class ApplicationDeploymentsWorkspace : UserControl
{
    public ApplicationDeploymentsWorkspace() => InitializeComponent();

    public static Control Create(ApplicationDeploymentsViewModel viewModel) =>
        new ApplicationDeploymentsWorkspace { DataContext = viewModel };
}
