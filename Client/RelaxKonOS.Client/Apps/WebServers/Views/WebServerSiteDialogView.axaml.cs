using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

internal partial class WebServerSiteDialogView : UserControl
{
    private readonly ModalDialog<bool> _dialog;

    public WebServerSiteDialogView(WebServerManagerViewModel viewModel, ModalDialog<bool> dialog)
    {
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.BeginSiteEditing();
        var initial = CaptureDraft(viewModel);
        var bindings = viewModel.SiteBindings.Select(binding => (binding.Domain, binding.Port)).ToArray();
        var routes = viewModel.SiteRoutes.Select(route => (route.Path, route.Upstream, route.DisableBuffering)).ToArray();
        RelaxKonOS.Client.Services.Dialogs.DraftDialogGuard.Attach(dialog,
            () => viewModel.IsSavingSite || viewModel.IsLoading || viewModel.IsOperationRunning,
            () => !initial.SequenceEqual(CaptureDraft(viewModel))
                || !bindings.SequenceEqual(viewModel.SiteBindings.Select(binding => (binding.Domain, binding.Port)))
                || !routes.SequenceEqual(viewModel.SiteRoutes.Select(route => (route.Path, route.Upstream, route.DisableBuffering))));
    }

    private static object?[] CaptureDraft(WebServerManagerViewModel vm) =>
    [
        vm.SiteName, vm.SiteBindingsBatch, vm.SiteRootPath, vm.SiteGrantNginxReadAccess, vm.SiteSpaFallback,
        vm.SiteHttpsEnabled, vm.SiteRedirectHttpToHttps, vm.SiteIpv6Enabled, vm.SelectedSiteCertificate?.Id,
        vm.SelectedSiteCertificateSource?.Value, vm.SiteCertificatePath, vm.SitePrivateKeyPath
    ];

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();

    private void RemoveBinding_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: WebServerSiteBindingEditor binding }
            && DataContext is WebServerManagerViewModel viewModel)
            viewModel.RemoveSiteBindingCommand.Execute(binding);
    }

    private void RemoveRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: WebServerProxyRouteEditor route }
            && DataContext is WebServerManagerViewModel viewModel)
            viewModel.RemoveSiteRouteCommand.Execute(route);
    }
}
