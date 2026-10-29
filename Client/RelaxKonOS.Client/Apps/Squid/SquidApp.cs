using Avalonia.Controls;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Squid.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.Squid;

/// <summary>Built-in HTTP/CONNECT gateway console. The Server answers the contract on every platform
/// so a host without Squid opens this window and reads its own "unsupported" state instead of the
/// shell hiding the application entirely.</summary>
public sealed class SquidApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.squid"), "Squid", "1.0.0", "⇥", "HTTP and HTTPS forward proxy gateway",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage, AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.GatewayProxy]),
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/squid.png", InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        var client = (ISquidClient?)context.Services.GetService(typeof(ISquidClient));
        if (session?.State != AuthSessionState.Authenticated || client is null) return;
        var dependency = RelaxKonOS.Client.Apps.ComponentCenter.ComponentDependencySettingsViewModel.Create(context, "relaxkonos.squid");
        var vm = new SquidViewModel(client) { Dependency = dependency };
        var view = new SquidWorkspace(vm);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.squid.display_name"), view,
            new Rect(90, 55, 960, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        RelaxKonOS.WindowManager.ModalDialog<bool>? activeEditor = null;
        view.ShowEditorAsync = (title, editor) => RelaxKonOS.Client.Controls.WorkspaceEditorDialogView.PresentAsync(context, window, title, editor, () => vm.IsBusy, active => activeEditor = active);

        vm.ConfirmDeleteAccountAsync = username => ConfirmAsync("squid.account_delete", "squid.account_delete_message");
        vm.ConfirmApplyAsync = () => ConfirmAsync("squid.apply_title", "squid.apply_message");
        vm.ConfirmReloadAsync = () => ConfirmAsync("common.discard_title", "squid.reload_message");
        window.CanCloseAsync = () => vm.IsBusy || vm.HasPending || vm.HasUnsavedChanges || vm.HasAccountDraft
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
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= sessionChanged;
            dependency.Dispose(); vm.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            var read = context.Permissions.GetStatus(AppPermissions.ServerProxyRead);
            if (read == AppPermissionStatus.Undecided) read = await context.Permissions.RequestAsync(AppPermissions.ServerProxyRead);
            vm.CanRead = read == AppPermissionStatus.Granted;
            if (vm.CanRead) await vm.RefreshCommand.ExecuteAsync(null);
            // A host that reported "unsupported" can never accept a mutation, so do not ask for the
            // manage grant there. An unavailable host keeps the prompt: the facts are merely unknown.
            if (!vm.CanRead || vm.Snapshot?.Status is { Supported: false }) return;
            var manage = context.Permissions.GetStatus(AppPermissions.ServerProxyManage);
            if (manage == AppPermissionStatus.Undecided) manage = await context.Permissions.RequestAsync(AppPermissions.ServerProxyManage);
            vm.CanManage = manage == AppPermissionStatus.Granted && session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller;
        }
        Task<TResult?> ShowOwnedAsync<TResult>(string title, Func<RelaxKonOS.WindowManager.ModalDialog<TResult>, Avalonia.Controls.Control> factory, RelaxKonOS.Core.Primitives.Size size)
            => activeEditor is { } editor ? editor.ShowDialogAsync(title, factory, size) : context.ShowDialogAsync(window, title, factory, size);

        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await ShowOwnedAsync<bool>( LocalizedText.Get(title), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(LocalizedText.Get(message), value => { accepted = value; dialog.Close(value); })
            }, new Size(520, 260));
            return accepted;
        }
    }

    /// <summary>Used by the window-preview checks to render the real workspace without a window host.</summary>
    internal static Control CreateView(SquidViewModel vm) => new SquidWorkspace(vm);
}
