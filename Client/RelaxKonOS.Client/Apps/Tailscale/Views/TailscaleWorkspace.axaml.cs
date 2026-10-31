using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Controls;

namespace RelaxKonOS.Client.Apps.Tailscale.Views;

/// <summary>Tailscale workspace shell. Page switching and lifecycle live here, not in AXAML.</summary>
public partial class TailscaleWorkspace : UserControl
{
    private readonly TailscaleLoginViewModel _login;
    private Button? _selected;
    private RelaxKonOS.WindowManager.ManagedWindow? _window;
    public TailscaleWorkspace(TailscaleViewModel model, TailscaleLoginViewModel login)
    {
        _login = login;
        InitializeComponent();
        DataContext = model;
        CompactWorkspaceNavigation.Install(this, WorkspaceBody);
        Show("devices", Devices);
        AttachedToVisualTree += async (_, _) =>
        {
            login.Activate();
            await model.ActivateAsync();
            login.SetVisible(_selected?.Tag is "login");
        };
        DetachedFromVisualTree += (_, _) => { login.Deactivate(); model.Deactivate(); };
    }

    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        _window = window;
        Header.DataContext = DataContext;
        Header.Attach(window, HeaderActions);
    }

    private void NavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page } button) Show(page, button);
    }
    private void OpenDevices(object? sender, RoutedEventArgs e) => Show("devices", Devices);
    private void ExitManager(object? sender, RoutedEventArgs e) => _window?.CloseCommand.Execute(null);
    private void Show(string page, Button button)
    {
        _selected?.Classes.Remove("nav-selected");
        _selected = button;
        button.Classes.Add("nav-selected");
        Page.Content = page switch
        {
            "login" => new TailscaleLoginPage { DataContext = _login },
            "control" => new TailscaleControlPage(),
            "operations" => new TailscaleOperationsPage(),
            _ => new TailscaleDevicesPage()
        };
        (DataContext as TailscaleViewModel)?.SetVisiblePage(page);
        _login.SetVisible(page == "login");
    }
}
