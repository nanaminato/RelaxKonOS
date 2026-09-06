using Avalonia.Controls;
using Avalonia.Interactivity;
using Client.Apps.PortForwarding.ViewModels;
using Client.Localization;
using RemoteOS.WindowManager;

namespace Client.Apps.PortForwarding.Views;

/// <summary>Modal editor used for both creating and changing a loopback forward.</summary>
public partial class PortForwardingEditorDialogView : UserControl
{
    private ModalDialog<bool>? _dialog;

    /// <summary>供 Avalonia 运行时 XAML 加载器和设计器创建控件。</summary>
    public PortForwardingEditorDialogView()
    {
        InitializeComponent();
    }

    public PortForwardingEditorDialogView(PortForwardingViewModel viewModel, ModalDialog<bool> dialog, bool isEditing)
        : this()
    {
        DataContext = viewModel;
        _dialog = dialog;
        SaveButton.Content = LocalizedText.Get(isEditing ? "port_forwarding.update" : "port_forwarding.start");
        SaveButton.Command = isEditing ? viewModel.UpdateSelectedCommand : viewModel.StartCommand;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog?.Cancel();
}
