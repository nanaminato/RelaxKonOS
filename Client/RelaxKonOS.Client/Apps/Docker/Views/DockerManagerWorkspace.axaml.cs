using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Docker.Views;

/// <summary>Docker Manager shell. Layout lives in AXAML; this class only switches pages.</summary>
internal partial class DockerManagerWorkspace : UserControl
{
    private readonly DockerManagerViewModel _viewModel;
    private readonly DockerProxyViewModel _proxyViewModel;
    private readonly DockerImageMirrorsViewModel _imageMirrorsViewModel;
    private readonly Func<Task> _showCreateContainer;
    private readonly Func<Task> _showDeployStack;
    private readonly Func<Task> _showPullImage;
    private readonly Func<Task> _showCreateNetwork;
    private readonly Func<Task> _showCreateVolume;
    private Button? _selectedButton;

    private DockerManagerWorkspace(DockerManagerViewModel viewModel, DockerProxyViewModel proxyViewModel, DockerImageMirrorsViewModel imageMirrorsViewModel, Func<Task> showCreateContainer, Func<Task> showDeployStack, Func<Task> showPullImage, Func<Task> showCreateNetwork, Func<Task> showCreateVolume)
    {
        _viewModel = viewModel;
        _proxyViewModel = proxyViewModel;
        _imageMirrorsViewModel = imageMirrorsViewModel;
        _showCreateContainer = showCreateContainer;
        _showDeployStack = showDeployStack;
        _showPullImage = showPullImage;
        _showCreateNetwork = showCreateNetwork;
        _showCreateVolume = showCreateVolume;
        InitializeComponent();
        DataContext = viewModel;
        ShowPage("overview", OverviewButton);
    }

    public static Control Create(DockerManagerViewModel viewModel, DockerProxyViewModel proxyViewModel, DockerImageMirrorsViewModel imageMirrorsViewModel, Func<Task> showCreateContainer, Func<Task> showDeployStack, Func<Task> showPullImage, Func<Task> showCreateNetwork, Func<Task> showCreateVolume) =>
        new DockerManagerWorkspace(viewModel, proxyViewModel, imageMirrorsViewModel, showCreateContainer, showDeployStack, showPullImage, showCreateNetwork, showCreateVolume);

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } button)
            ShowPage(section, button);
    }

    private void ShowPage(string section, Button button)
    {
        if (_selectedButton is not null)
        {
            _selectedButton.Classes.Remove("nav-selected");
        }

        _selectedButton = button;
        button.Classes.Add("nav-selected");
        // The proxy page reads the host preference on every visit: it is host-global state that
        // another operator or an out-of-band host change can alter while this window is open.
        if (section == "proxy") _ = _proxyViewModel.LoadAsync();
        if (section == "mirrors") _ = _imageMirrorsViewModel.LoadAsync();
        ContentHost.Content = section switch
        {
            "containers" => new DockerContainersView(_showCreateContainer),
            "stacks" => new DockerStacksView(_showDeployStack),
            "images" => new DockerImagesView(_showPullImage),
            "mirrors" => new DockerImageMirrorsView(_imageMirrorsViewModel),
            "networks" => new DockerNetworksView(_showCreateNetwork),
            "volumes" => new DockerVolumesView(_showCreateVolume),
            "proxy" => new DockerProxyView(_proxyViewModel),
            _ => new DockerOverviewView()
        };
    }
}
