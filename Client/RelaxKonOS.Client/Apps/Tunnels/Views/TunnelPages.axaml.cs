using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Apps.Tunnels;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Client.Apps.Tunnels.Views;

public partial class TunnelOverviewView : UserControl { public TunnelOverviewView() => InitializeComponent(); }
public partial class TunnelDefinitionsView : UserControl { public TunnelDefinitionsView() => InitializeComponent(); }
public partial class TunnelServersView : UserControl
{
    public TunnelServersView() => InitializeComponent();
    private void OpenLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TunnelServerProfileDto profile } && DataContext is TunnelManagerViewModel viewModel)
            viewModel.OpenLogsCommand.Execute(profile);
    }
}
public partial class TunnelRuntimeView : UserControl { public TunnelRuntimeView() => InitializeComponent(); }
public partial class TunnelManagedFrpsView : UserControl { public TunnelManagedFrpsView() => InitializeComponent(); }
public partial class TunnelManagedFrpsConfigurationView : UserControl
{
    public Action? CloseAction { get; set; }
    public TunnelManagedFrpsConfigurationView() => InitializeComponent();
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => CloseAction?.Invoke();
}
public partial class TunnelManagedFrpsDiagnosticsView : UserControl
{
    public Action? CloseAction { get; set; }
    public TunnelManagedFrpsDiagnosticsView() => InitializeComponent();
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => CloseAction?.Invoke();
}
public partial class TunnelProfileEditorView : UserControl { public TunnelProfileEditorView() => InitializeComponent(); }
public partial class TunnelDefinitionEditorView : UserControl { public TunnelDefinitionEditorView() => InitializeComponent(); }
public partial class TunnelLogWindowView : UserControl { public TunnelLogWindowView() => InitializeComponent(); }
