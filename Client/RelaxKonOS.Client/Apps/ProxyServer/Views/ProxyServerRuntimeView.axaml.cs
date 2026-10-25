using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.ProxyServer.Views;

/// <summary>Managed runtime page hosted inside the Proxy Server workspace.</summary>
public partial class ProxyServerRuntimeView : UserControl
{

    public Func<string, Control, Task>? ShowEditorAsync { get; set; }

    private async void EditorButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } button || ShowEditorAsync is null) return;
        var parts = target.Split(':');
        await RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.OpenAsync(this, parts[0], button.Content?.ToString() ?? "", ShowEditorAsync);
    }

    public Task OpenEditorAsync() => ShowEditorAsync is { } presenter
        ? RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.OpenAsync(this, "RuntimeEditor", RelaxKonOS.Client.Localization.LocalizedText.Get("proxy_server.runtime.action_title"), presenter)
        : Task.CompletedTask;

    /// <summary>Component Center uses the same controls inline; domain workspaces retain their editor host.</summary>
    public void ShowCenterSection(RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterPage page, bool showDomainActivity)
    {
        EditorButton.IsVisible = false;
        RuntimeEditor.IsVisible = true;
        var tasks = page == RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterPage.Tasks;
        var sources = page == RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterPage.Sources;
        StatePanel.IsVisible = !tasks && !sources;
        EnvironmentSection.IsVisible = !tasks && !sources;
        RecoverySection.IsVisible = tasks;
        SourcesPanel.IsVisible = sources || page == RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterPage.All;
        ReleasesPanel.IsVisible = !tasks && !sources;
        ChangesPanel.IsVisible = !tasks && !sources;
        ActivityPanel.IsVisible = tasks && showDomainActivity;
        ReleaseList.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(
            page == RelaxKonOS.Client.Apps.ComponentCenter.ComponentCenterPage.Updates ? "Updates" : "Releases"));
    }

    public ProxyServerRuntimeView() => InitializeComponent();

    public ProxyServerRuntimeView(ProxyServerRuntimeViewModel viewModel) : this() => DataContext = viewModel;
}
