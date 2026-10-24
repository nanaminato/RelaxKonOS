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

    public ProxyServerRuntimeView() => InitializeComponent();

    public ProxyServerRuntimeView(ProxyServerRuntimeViewModel viewModel) : this() => DataContext = viewModel;
}
