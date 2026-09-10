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
        if (session is null || client is null || session.State != AuthSessionState.Authenticated) { context.ShowWindow(T("file_services.title", "File Services"), new TextBlock { Text = T("file_services.login_required", "Sign in to manage SMB file services."), Margin = new Avalonia.Thickness(20) }, new Rect(180, 160, 440, 160), Manifest.IconGlyph, false, false, false); return; }
        var vm = new FileServicesViewModel(client, context.Permissions);
        var root = new DockPanel { Margin = new Avalonia.Thickness(20), DataContext = vm };
        var header = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 16) };
        header.Children.Add(BoundText(nameof(vm.PlatformText), 24));
        header.Children.Add(BoundText(nameof(vm.StatusText)));
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3 };
        progress.Bind(Avalonia.Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(vm.IsBusy)));
        header.Children.Add(progress);
        header.Children.Add(new Button { Content = T("file_services.refresh", "Refresh"), Command = vm.RefreshCommand });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var pages = new TabControl();
        var overview = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(0, 16) };
        overview.Children.Add(BoundText(nameof(vm.PlatformHelp)));
        overview.Children.Add(new TextBlock { Text = T("file_services.version", "Service version") });
        overview.Children.Add(BoundText(nameof(vm.VersionText)));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var install = new Button { Content = T("file_services.install", "Install"), Command = vm.InstallCommand };
        install.Bind(Avalonia.Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(vm.SupportsInstall)));
        actions.Children.Add(install);
        actions.Children.Add(new Button { Content = T("file_services.start", "Start"), Command = vm.StartServiceCommand });
        actions.Children.Add(new Button { Content = T("file_services.stop", "Stop"), Command = vm.StopCommand });
        actions.Children.Add(new Button { Content = T("file_services.restart", "Restart"), Command = vm.RestartCommand });
        overview.Children.Add(actions);
        overview.Children.Add(new TextBlock { Text = T("file_services.connection_help", "Append the share name to a connection address below.") });
        var connection = new SelectableTextBlock(); connection.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.ConnectionText))); overview.Children.Add(connection);
        var overviewPage = new TabItem { Header = T("file_services.overview", "Overview"), Content = new ScrollViewer { Content = overview } };
        var shares = Table(vm.Shares);
        foreach (var field in new[] { "Name", "Path", "Description", "ReadOnly", "Enabled", "GuestAllowed", "Managed", "Drifted" })
            AddColumn(shares, field);
        shares.Columns.Add(new DataGridTextColumn
        {
            Header = T("file_services.column.Permissions", "Permissions"), Width = new DataGridLength(240),
            Binding = new Avalonia.Data.Binding("Permissions")
            {
                Converter = new Avalonia.Data.Converters.FuncValueConverter<System.Collections.Generic.IReadOnlyList<RelaxKonOS.Protocol.FileServices.FileSharePermissionDto>, string>(
                    rules => rules is null ? "—" : string.Join(", ", rules.Select(rule => rule.Principal + ": " + T("file_services.access." + rule.Access, rule.Access.ToString()))))
            }
        });
        shares.SelectionChanged += (_, _) => vm.SelectedShare = shares.SelectedItem as RelaxKonOS.Protocol.FileServices.FileShareDto;
        var shareActions = new WrapPanel();
        shareActions.Children.Add(new Button { Content = T("file_services.new_share", "New share"), Command = vm.NewShareCommand });
        shareActions.Children.Add(new Button { Content = T("file_services.edit", "Edit"), Command = vm.EditShareCommand });
        shareActions.Children.Add(new Button { Content = T("file_services.delete", "Delete"), Command = vm.DeleteShareCommand });
        var sharesPage = new TabItem { Header = T("file_services.shares", "Managed SMB shares"), Content = TablePage(shareActions, shares, "shares_help") };
        var users = Table(vm.Users);
        foreach (var field in new[] { "Username", "Enabled", "Eligible" }) AddColumn(users, field);
        users.SelectionChanged += (_, _) => vm.SelectedUser = users.SelectedItem as RelaxKonOS.Protocol.FileServices.FileServiceUserDto;
        var userActions = new WrapPanel();
        userActions.Children.Add(new Button { Content = T("file_services.user_toggle", "Enable / disable"), Command = vm.ToggleUserCommand });
        userActions.Children.Add(new Button { Content = T("file_services.user_password", "Set Samba password"), Command = vm.SetSambaPasswordCommand });
        var usersPage = new TabItem { Header = T("file_services.users", "Linux Samba users"), Content = TablePage(userActions, users, "users_help") };
        pages.Items.Add(overviewPage); pages.Items.Add(sharesPage);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.SupportsSambaCredentials)) return;
            if (vm.SupportsSambaCredentials && !pages.Items.Contains(usersPage)) pages.Items.Add(usersPage);
            if (!vm.SupportsSambaCredentials && pages.Items.Contains(usersPage)) { pages.SelectedItem = overviewPage; pages.Items.Remove(usersPage); }
        };
        root.Children.Add(pages);
        var window = context.ShowWindow(T("file_services.title", "File Services"), root, new Rect(90, 80, 960, 720), Manifest.IconGlyph);
        vm.RequestHostAdministratorPasswordAsync = () => RequestPasswordAsync(context, window, T("file_services.host_password", "Host administrator password"));
        vm.RequestSambaPasswordAsync = () => RequestPasswordAsync(context, window, T("file_services.samba_password", "New Samba password"));
        vm.ShowShareEditorAsync = editing => ShowShareEditorAsync(context, window, vm, editing);
        vm.ConfirmDeleteAsync = name => context.ShowDialogAsync<bool>(window, T("file_services.delete", "Delete"), dialog =>
        {
            var confirm = new Button { Content = T("file_services.delete", "Delete") }; confirm.Click += (_, _) => dialog.Close(true);
            var cancel = new Button { Content = LocalizedText.Get("common.cancel", "Cancel") }; cancel.Click += (_, _) => dialog.Cancel();
            return new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 16, Children = { new TextBlock { Text = LocalizedText.Format("file_services.delete_confirm", name), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, cancel, confirm } };
        }, new Size(440, 220));
        _ = vm.StartAsync();
    }
    private static TextBlock BoundText(string property, double size = 14)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(property)); return text;
    }
    private static DataGrid Table(System.Collections.IEnumerable items) => new()
    {
        ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
    };
    private static void AddColumn(DataGrid grid, string field)
    {
        var title = T("file_services.column." + field, field);
        if (field is "ReadOnly" or "Enabled" or "GuestAllowed" or "Managed" or "Drifted" or "Eligible")
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = title, Binding = new Avalonia.Data.Binding(field), Width = new DataGridLength(110) });
        else grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Avalonia.Data.Binding(field), Width = new DataGridLength(field == "Path" ? 260 : 160) });
    }
    private static Control TablePage(Control actions, Control table, string help)
    {
        var panel = new DockPanel { Margin = new Avalonia.Thickness(0, 16) };
        var header = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(0, 0, 0, 12), Children = {
            new TextBlock { Text = T("file_services." + help, help), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, actions } };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header); panel.Children.Add(table); return panel;
    }
    private static Task<string?> RequestPasswordAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, string title) => context.ShowDialogAsync<string?>(owner, title, dialog =>
    {
        var box = new TextBox { PasswordChar = '•' }; var ok = new Button { Content = T("file_services.confirm", "Confirm") }; ok.Click += (_, _) => dialog.Close(box.Text); var cancel = new Button { Content = LocalizedText.Get("common.cancel", "Cancel") }; cancel.Click += (_, _) => dialog.Cancel();
        return new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12, Children = { box, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, ok } } } };
    }, new Size(420, 160));
    private static Task ShowShareEditorAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, FileServicesViewModel vm, bool editing) => context.ShowDialogAsync<bool>(owner, editing ? T("file_services.share_edit_title", "Edit managed SMB share") : T("file_services.share_new_title", "New managed SMB share"), dialog =>
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 8, DataContext = vm };
        panel.Children.Add(BoundText(nameof(vm.PlatformHelp)));
        panel.Children.Add(BoundText(nameof(vm.StatusText)));
        AddField(panel, T("file_services.share_name", "Name"), nameof(vm.ShareName)); AddField(panel, T("file_services.share_path", "Path"), nameof(vm.SharePath)); AddField(panel, T("file_services.share_description", "Description"), nameof(vm.ShareDescription)); AddField(panel, T("file_services.share_permissions", "Permissions (principal:Read or :ReadWrite)"), nameof(vm.SharePrincipals));
        var readOnly = new CheckBox { Content = T("file_services.share_read_only", "Read only") }; readOnly.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.ShareReadOnly)) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(readOnly);
        var enabled = new CheckBox { Content = T("file_services.share_enabled", "Enabled") }; enabled.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.ShareEnabled)) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(enabled);
        var guest = new CheckBox { Content = T("file_services.share_guest", "Allow guest read-only access") }; guest.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.ShareGuestAllowed)) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(guest);
        var save = new Button { Content = LocalizedText.Get("common.save", "Save") }; save.Click += async (_, _) => { save.IsEnabled = false; try { if (await vm.SaveShareAsync(editing)) dialog.Close(true); } finally { save.IsEnabled = true; } }; var cancel = new Button { Content = LocalizedText.Get("common.cancel", "Cancel") }; cancel.Click += (_, _) => dialog.Cancel(); panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, save } }); return new ScrollViewer { Content = panel };
    }, new Size(620, 650));
    private static void AddField(Panel panel, string label, string property) { panel.Children.Add(new TextBlock { Text = label }); var box = new TextBox(); box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay }); panel.Children.Add(box); }
    private static string T(string key, string fallback) => LocalizedText.Get(key, fallback);
}
