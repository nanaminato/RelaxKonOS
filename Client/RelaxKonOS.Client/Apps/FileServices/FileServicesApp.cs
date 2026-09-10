using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Built-in SMB administration entry point. Unsupported protocols deliberately have no card or feature flag.</summary>
public sealed class FileServicesApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.file-services"), "File Services", "1.0.0", "🗄", "Manage the host SMB control plane",
        [AppPermissions.ServerFileServicesRead, AppPermissions.ServerFileServicesManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFileServicesClient)) as IRemoteFileServicesClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated) { context.ShowWindow("File Services", new TextBlock { Text = "Sign in to manage SMB file services.", Margin = new Avalonia.Thickness(20) }, new Rect(180, 160, 440, 160), Manifest.IconGlyph, false, false, false); return; }
        var vm = new FileServicesViewModel(client, context.Permissions);
        var root = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText))); root.Children.Add(status);
        var connection = new TextBlock(); connection.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.ConnectionText))); root.Children.Add(connection);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new Button { Content = "Refresh", Command = vm.RefreshCommand }); actions.Children.Add(new Button { Content = "Install", Command = vm.InstallCommand }); actions.Children.Add(new Button { Content = "Start", Command = vm.StartServiceCommand }); actions.Children.Add(new Button { Content = "Stop", Command = vm.StopCommand }); actions.Children.Add(new Button { Content = "Restart", Command = vm.RestartCommand }); root.Children.Add(actions);
        root.Children.Add(new TextBlock { Text = "Managed SMB shares" });
        var shareActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; shareActions.Children.Add(new Button { Content = "New share", Command = vm.NewShareCommand }); shareActions.Children.Add(new Button { Content = "Edit", Command = vm.EditShareCommand }); shareActions.Children.Add(new Button { Content = "Delete", Command = vm.DeleteShareCommand }); root.Children.Add(shareActions);
        var shares = new DataGrid { ItemsSource = vm.Shares, AutoGenerateColumns = true, IsReadOnly = true, Height = 220, SelectionMode = DataGridSelectionMode.Single }; shares.SelectionChanged += (_, _) => vm.SelectedShare = shares.SelectedItem as RelaxKonOS.Protocol.FileServices.FileShareDto; root.Children.Add(shares);
        root.Children.Add(new TextBlock { Text = "Linux Samba users" }); var userActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; userActions.Children.Add(new Button { Content = "Enable / disable", Command = vm.ToggleUserCommand }); userActions.Children.Add(new Button { Content = "Set Samba password", Command = vm.SetSambaPasswordCommand }); root.Children.Add(userActions);
        var users = new DataGrid { ItemsSource = vm.Users, AutoGenerateColumns = true, IsReadOnly = true, Height = 130, SelectionMode = DataGridSelectionMode.Single }; users.SelectionChanged += (_, _) => vm.SelectedUser = users.SelectedItem as RelaxKonOS.Protocol.FileServices.FileServiceUserDto; root.Children.Add(users);
        var window = context.ShowWindow("File Services", root, new Rect(90, 80, 960, 720), Manifest.IconGlyph);
        vm.RequestHostAdministratorPasswordAsync = () => RequestPasswordAsync(context, window, "Host administrator password");
        vm.RequestSambaPasswordAsync = () => RequestPasswordAsync(context, window, "New Samba password");
        vm.ShowShareEditorAsync = editing => ShowShareEditorAsync(context, window, vm, editing);
        _ = vm.StartAsync();
    }
    private static Task<string?> RequestPasswordAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, string title) => context.ShowDialogAsync<string?>(owner, title, dialog =>
    {
        var box = new TextBox { PasswordChar = '•' }; var ok = new Button { Content = "Confirm" }; ok.Click += (_, _) => dialog.Close(box.Text); var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Cancel();
        return new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12, Children = { box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, ok } } } };
    }, new Size(420, 160));
    private static Task ShowShareEditorAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, FileServicesViewModel vm, bool editing) => context.ShowDialogAsync<bool>(owner, editing ? "Edit managed SMB share" : "New managed SMB share", dialog =>
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 8, DataContext = vm };
        AddField(panel, "Name", nameof(vm.ShareName)); AddField(panel, "Path", nameof(vm.SharePath)); AddField(panel, "Description", nameof(vm.ShareDescription)); AddField(panel, "Permissions (SID/user:Read or :ReadWrite)", nameof(vm.SharePrincipals));
        var readOnly = new CheckBox { Content = "Read only" }; readOnly.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.ShareReadOnly)) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(readOnly);
        var enabled = new CheckBox { Content = "Enabled" }; enabled.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.ShareEnabled)) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(enabled);
        var guest = new CheckBox { Content = "Allow guest read-only access" }; guest.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.ShareGuestAllowed)) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(guest);
        var save = new Button { Content = "Save" }; save.Click += async (_, _) => { if (await vm.SaveShareAsync(editing)) dialog.Close(true); }; var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => dialog.Cancel(); panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } }); return panel;
    }, new Size(580, 500));
    private static void AddField(Panel panel, string label, string property) { panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox(); box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(box); }
}
