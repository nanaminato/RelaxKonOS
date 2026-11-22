using Avalonia.Controls;
using RelaxKonOS.App.Settings.Views;

namespace RelaxKonOS.Client.Apps.Settings.Views;

public sealed class SettingsView : SettingsWorkspaceView
{
    public SettingsView()
    {
        var templates = new SettingsPageTemplates(NavigateTo);
        foreach (var template in templates.DataTemplates)
            DataTemplates.Add(template);
    }
}

public partial class SettingsPageTemplates : UserControl
{
    private readonly Action<string>? _navigate;

    public SettingsPageTemplates() => InitializeComponent();

    public SettingsPageTemplates(Action<string> navigate) : this() => _navigate = navigate;

    private void OnHomeRouteRequested(object? sender, string route) => _navigate?.Invoke(route);
}
