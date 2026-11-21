using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.App.WebServers.Views;

/// <summary>Web Server Manager shell. Layout lives in AXAML; this class only switches pages.</summary>
public partial class WebServerManagerWorkspace : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        Header.DataContext = DataContext;
        Header.Attach(window);
    }

    private Button? _selectedButton;
    private readonly Func<Control>? _createSettings;

    private WebServerManagerWorkspace(WebServerManagerViewModel viewModel,
        Func<Control>? createSettings)
    {
        _createSettings = createSettings;
        InitializeComponent();
        RelaxKonOS.WindowManager.CompactWorkspaceNavigation.Install(this, WorkspaceBody,
            (Avalonia.Data.Binding)new LocExtension("webservers.view").ProvideValue(null!));
        DataContext = viewModel;
        ShowPage("instances", InstancesPageButton);
    }

    public static WebServerManagerWorkspace Create(WebServerManagerViewModel viewModel,
        Func<Control>? createSettings = null) => new(viewModel, createSettings);

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button)
            ShowPage(page, button);
    }

    private void ShowPage(string page, Button button)
    {
        if (_selectedButton is not null)
        {
            _selectedButton.Classes.Remove("nav-selected");
        }

        _selectedButton = button;
        button.Classes.Add("nav-selected");
        ContentHost.Content = page switch
        {
            "sites" => new WebServerSitesPageView(),
            "settings" => new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = _createSettings?.Invoke()
            },
            _ => new WebServerInstancesPageView()
        };
    }
}
