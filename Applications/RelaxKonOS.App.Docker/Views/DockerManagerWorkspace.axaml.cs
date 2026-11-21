using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Docker.Views;

/// <summary>Docker Manager shell. Layout lives in AXAML; this class only switches pages.</summary>
public partial class DockerManagerWorkspace : UserControl
{
    public void AttachWindowHeader(ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    private readonly DockerProxyStatusViewModel _proxyViewModel;
    private readonly DockerImageMirrorsViewModel _imageMirrorsViewModel;
    private readonly Func<Control>? _createSettingsPage;
    private readonly Func<Task> _showCreateContainer;
    private readonly Func<Task> _showDeployStack;
    private readonly Func<Task> _showPullImage;
    private readonly Func<Task> _showCreateNetwork;
    private readonly Func<Task> _showCreateVolume;
    private Button? _selectedButton;

    public DockerManagerWorkspace(DockerManagerViewModel viewModel, DockerProxyStatusViewModel proxyViewModel, DockerImageMirrorsViewModel imageMirrorsViewModel, Func<Task> showCreateContainer, Func<Task> showDeployStack, Func<Task> showPullImage, Func<Task> showCreateNetwork, Func<Task> showCreateVolume, Func<Control>? createSettingsPage = null)
    {
        _proxyViewModel = proxyViewModel;
        _imageMirrorsViewModel = imageMirrorsViewModel;
        _createSettingsPage = createSettingsPage;
        _showCreateContainer = showCreateContainer;
        _showDeployStack = showDeployStack;
        _showPullImage = showPullImage;
        _showCreateNetwork = showCreateNetwork;
        _showCreateVolume = showCreateVolume;
        InitializeComponent();
        SettingsButton.IsVisible = createSettingsPage is not null;
        CompactWorkspaceNavigation.Install(this, WorkspaceBody, (Binding)new LocExtension("common.view").ProvideValue(null!));
        SizeChanged += (_, e) => { HeaderStatusLabel.IsVisible = e.NewSize.Width >= 600; HeaderActions.Width = HeaderStatusLabel.IsVisible ? double.NaN : 32; };
        DataContext = viewModel;
        ShowPage("overview", OverviewButton);
    }

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
        if (section == "proxy") _ = _proxyViewModel.LoadAsync();
        if (section == "mirrors") _ = _imageMirrorsViewModel.LoadAsync();
        Control page = section switch
        {
            "containers" => new DockerContainersView(_showCreateContainer),
            "stacks" => new DockerStacksView(_showDeployStack),
            "images" => new DockerImagesView(_showPullImage),
            "mirrors" => new DockerImageMirrorsView(_imageMirrorsViewModel),
            "proxy" => new DockerProxyView(_proxyViewModel),
            "networks" => new DockerNetworksView(_showCreateNetwork),
            "volumes" => new DockerVolumesView(_showCreateVolume),
            "settings" when _createSettingsPage is not null => _createSettingsPage(),
            _ => new DockerOverviewView()
        };
        // Resource lists fill the available workspace; overview, settings and the multi-table
        // orchestration page keep their own scrollable document presentation.
        ContentHost.Content = section is "containers" or "images" or "networks" or "volumes"
            ? page
            : new ScrollViewer
            {
                Content = page,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
    }
}
