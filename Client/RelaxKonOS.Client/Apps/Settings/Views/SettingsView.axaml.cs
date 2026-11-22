using Avalonia.Controls;
using RelaxKonOS.App.Settings.Views;

namespace RelaxKonOS.Client.Apps.Settings.Views;

public sealed class SettingsView : SettingsWorkspaceView
{
    public SettingsView()
    {
        var templates = new SettingsPageTemplates();
        foreach (var template in templates.DataTemplates)
            DataTemplates.Add(template);
    }
}

public partial class SettingsPageTemplates : UserControl
{
    public SettingsPageTemplates() => InitializeComponent();
}
