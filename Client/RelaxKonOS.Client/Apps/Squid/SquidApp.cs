using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.Squid;

public sealed class SquidApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.squid"), "Squid", "1.0.0", "⇥", "HTTP and HTTPS forward proxy gateway",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage],
        ServerRequirements: new ApplicationServerRequirements(Platforms: [ApplicationPlatformNames.Linux], Capabilities: [ServerCapabilities.GatewayProxy]),
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/proxy.png", InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        var client = (ISquidClient?)context.Services.GetService(typeof(ISquidClient));
        if (session?.State != AuthSessionState.Authenticated || client is null) return;
        var vm = new SquidViewModel(client);
        var window = context.ShowWindow("Squid", CreateView(vm), new Rect(90, 55, 960, 760), Manifest.IconGlyph);
        vm.ConfirmApplyAsync = () => ConfirmAsync("squid.apply_title", "squid.apply_message");
        vm.ConfirmReloadAsync = () => ConfirmAsync("common.discard_title", "squid.close_message");
        window.CanCloseAsync = () => vm.IsBusy || vm.HasPending || vm.HasUnsavedChanges
            ? ConfirmAsync("common.discard_title", "squid.close_message") : Task.FromResult(true);
        var login = session.CurrentSession;
        EventHandler<AuthSessionStateChangedEventArgs> sessionChanged = (_, _) =>
        {
            if (!ReferenceEquals(login, session.CurrentSession) || session.State != AuthSessionState.Authenticated)
                Avalonia.Threading.Dispatcher.UIThread.Post(vm.InvalidateSession);
        };
        session.StateChanged += sessionChanged;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= sessionChanged; vm.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            var read = context.Permissions.GetStatus(AppPermissions.ServerProxyRead);
            if (read == AppPermissionStatus.Undecided) read = await context.Permissions.RequestAsync(AppPermissions.ServerProxyRead);
            var manage = context.Permissions.GetStatus(AppPermissions.ServerProxyManage);
            if (manage == AppPermissionStatus.Undecided) manage = await context.Permissions.RequestAsync(AppPermissions.ServerProxyManage);
            vm.CanRead = read == AppPermissionStatus.Granted;
            vm.CanManage = manage == AppPermissionStatus.Granted && session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller;
            if (vm.CanRead) await vm.RefreshCommand.ExecuteAsync(null);
        }
        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get(title), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(LocalizedText.Get(message), value => { accepted = value; dialog.Close(value); })
            }, new RelaxKonOS.Core.Primitives.Size(520, 260));
            return accepted;
        }
    }
    internal static Control CreateView(SquidViewModel vm)
    {
        var fields = new StackPanel { Spacing = 10, Margin = new Thickness(20), DataContext = vm };
        fields.Children.Add(new TextBlock { Text = LocalizedText.Get("squid.intro"), TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; status.Bind(TextBlock.TextProperty, new Binding(nameof(vm.StatusText))); fields.Children.Add(status);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        AddButton(actions, "common.refresh", vm.RefreshCommand);
        AddButton(actions, "squid.resume", vm.ResumeCommand);
        AddButton(actions, "squid.load", vm.LoadStoredCommand);
        foreach (var (key, command) in new (string, System.Windows.Input.ICommand)[] {
            ("start", vm.StartCommand), ("stop", vm.StopCommand), ("restart", vm.RestartCommand), ("reload", vm.ReloadCommand),
            ("enable", vm.EnableCommand), ("disable", vm.DisableCommand) }) AddButton(actions, "squid." + key, command);
        fields.Children.Add(actions);
        var editor = new StackPanel { Spacing = 10 }; editor.Bind(Control.IsEnabledProperty, new Binding(nameof(vm.CanEdit)));
        Field(editor, "bind_address", nameof(vm.BindAddress)); Field(editor, "port", nameof(vm.Port));
        Toggle(editor, "remote_clients", nameof(vm.AllowRemoteClients));
        Field(editor, "networks", nameof(vm.AllowedNetworks), true); Field(editor, "allowed_domains", nameof(vm.AllowedDomains), true);
        Field(editor, "denied_domains", nameof(vm.DeniedDomains), true);
        Field(editor, "upstream_host", nameof(vm.UpstreamHost)); Field(editor, "upstream_port", nameof(vm.UpstreamPort));
        Toggle(editor, "adopt", nameof(vm.AdoptExistingConfiguration)); fields.Children.Add(editor);
        var edits = new WrapPanel(); AddButton(edits, "squid.preview", vm.PreviewCommand); AddButton(edits, "squid.validate", vm.ValidateCommand); AddButton(edits, "squid.apply", vm.ApplyCommand); fields.Children.Add(edits);
        var result = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 80, TextWrapping = TextWrapping.Wrap }; result.Bind(TextBox.TextProperty, new Binding(nameof(vm.ResultText))); fields.Children.Add(result);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 160, TextWrapping = TextWrapping.NoWrap };
        preview.Bind(TextBox.TextProperty, new Binding(nameof(vm.PreviewText))); fields.Children.Add(preview);
        return new ScrollViewer { Background = ThemeBrushes.Get("SurfaceBrush"), Foreground = ThemeBrushes.Get("TextPrimaryBrush"), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = fields, Classes = { "builtin-app" } };
    }
    private static void Field(StackPanel panel, string key, string property, bool multiline = false)
    {
        panel.Children.Add(new TextBlock { Text = LocalizedText.Get("squid." + key), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { AcceptsReturn = multiline, MinHeight = multiline ? 70 : 32 };
        input.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(input);
    }
    private static void Toggle(StackPanel panel, string key, string property)
    {
        var input = new CheckBox { Content = new TextBlock { Text = LocalizedText.Get("squid." + key), TextWrapping = TextWrapping.Wrap } };
        input.Bind(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay }); panel.Children.Add(input);
    }
    private static void AddButton(Panel panel, string key, System.Windows.Input.ICommand command) => panel.Children.Add(new Button
    { Content = LocalizedText.Get(key), Command = command, Margin = new Thickness(0, 0, 8, 8) });
}
