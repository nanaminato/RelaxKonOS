using Avalonia.Threading;
using RelaxKonOS.App.Tunnels.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.App.Tunnels;

public sealed class TunnelManagerPackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.tunnels"),
        "Tunnel Manager", "1.1.0", "↔", "Manage FRP tunnel desired state and runtime status",
        [AppPermissions.ServerTunnelsRead, AppPermissions.ServerTunnelsManage, AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Tunnels]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => TunnelsLocalization.Resolve(language, key, key);
        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent)
        {
            context.Windows.ShowWindow(Label("tunnels.title"), new TunnelLoginRequiredView(Label, language),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var permissions = context.Permissions;
        if (permissions.GetStatus(AppPermissions.ServerTunnelsRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerTunnelsRead, cancellationToken);
        if (permissions.IsGranted(AppPermissions.ServerTunnelsRead))
        {
            if (permissions.GetStatus(AppPermissions.ServerTunnelsManage) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(AppPermissions.ServerTunnelsManage, cancellationToken);
            if (permissions.GetStatus(AppPermissions.ServerComponentsRead) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(AppPermissions.ServerComponentsRead, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsCurrent) return;

        var client = context.Tunnels;
        var vm = new TunnelManagerViewModel(client, permissions.IsGranted(AppPermissions.ServerTunnelsManage), Label);
        IExternalAppWindowHandle? owner = null;
        Avalonia.Controls.Control? settings = null;
        var view = new TunnelManagerView
        {
            DataContext = vm,
            CreateDependencySettings = () => owner is null ? null : settings ??= context.ComponentDependencies?.CreateSettingsView(owner),
        };
        var window = context.Windows.ShowWindow(Label("tunnels.title"), view, new Rect(90, 65, 1040, 680), Manifest.IconGlyph);
        owner = window;
        view.AttachWindowHeader(window.Window);

        Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
            window.ConfirmAsync(title, message, confirmLabel);

        vm.RequestConfirmationAsync = (title, message) => ConfirmAsync(title, message, title);
        vm.ShowManagedFrpsConfigurationAsync = async () =>
        {
            await vm.LoadManagedFrpsForEditingAsync();
            try { await window.ShowDialogAsync<bool?>(Label("tunnels.frps.configuration"), dialog =>
                new TunnelManagedFrpsConfigurationView { DataContext = vm, CloseAction = dialog.Cancel }, new Size(720, 690)); }
            finally { vm.EndManagedFrpsEditing(); }
        };
        vm.ShowManagedFrpsDiagnosticsAsync = async () =>
        {
            await window.ShowDialogAsync<bool?>(Label("tunnels.frps.diagnostics"), dialog =>
                new TunnelManagedFrpsDiagnosticsView { DataContext = vm, CloseAction = dialog.Cancel }, new Size(720, 600));
        };
        vm.OpenProfileEditorAsync = async profile =>
        {
            var editableProfile = profile is null ? null : await client.GetProfileAsync(profile.Id);
            if (profile is not null && editableProfile is null) return;
            var editor = new TunnelProfileEditorViewModel(client, editableProfile, Label)
            {
                SavedAsync = vm.RefreshAfterChildAsync,
                RequestDeletionConfirmationAsync = () => ConfirmAsync(Label("common.delete"), Label("tunnels.server.delete_confirmation"), Label("common.delete")),
            };
            using var observation = ObserveLanguage(editor, window.Closed);
            await window.ShowDialogAsync<bool?>(
                Label(profile is null ? "tunnels.server.new_title" : "tunnels.server.edit_title"), dialog =>
                {
                    editor.CloseAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return new TunnelProfileEditorView { DataContext = editor };
                }, new Size(620, 540));
        };
        vm.OpenTunnelEditorAsync = async tunnel =>
        {
            var editor = new TunnelDefinitionEditorViewModel(client, vm.Profiles, tunnel, Label)
            {
                SavedAsync = vm.RefreshAfterChildAsync,
                RequestDeletionConfirmationAsync = () => ConfirmAsync(Label("common.delete"), Label("tunnels.tunnel.delete_confirmation"), Label("common.delete")),
            };
            using var observation = ObserveLanguage(editor, window.Closed);
            await window.ShowDialogAsync<bool?>(
                Label(tunnel is null ? "tunnels.tunnel.new_title" : "tunnels.tunnel.edit_title"), dialog =>
                {
                    editor.CloseAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return new TunnelDefinitionEditorView { DataContext = editor };
                }, new Size(650, 520));
        };
        vm.OpenLogsWindowAsync = profile =>
        {
            var logViewModel = new TunnelLogViewModel(client, profile, Label);
            var logWindow = context.Windows.ShowWindow(string.Format(System.Globalization.CultureInfo.CurrentCulture, Label("tunnels.logs.title_format"), profile.Name),
                new TunnelLogWindowView { DataContext = logViewModel }, new Rect(270, 175, 720, 510), Manifest.IconGlyph);
            var observation = ObserveLanguage(logViewModel, logWindow.Closed);
            logWindow.Closed.Register(() =>
            {
                observation.Dispose();
                DisposeOnUiThread(logViewModel);
            });
            _ = logViewModel.StartAsync();
            return Task.CompletedTask;
        };
        var languageObservation = ObserveLanguage(vm, window.Closed, () => window.Window.Title = Label("tunnels.title"));
        var connectionObservation = connection.ObserveInvalidation(() => DisposeOnUiThread(vm));
        window.Closed.Register(() =>
        {
            languageObservation.Dispose();
            connectionObservation.Dispose();
            DisposeOnUiThread(vm);
        });
        await vm.StartAsync();

        IDisposable ObserveLanguage(TunnelObservableObject model, CancellationToken closed, Action? updateTitle = null)
        {
            var subscription = new LanguageSubscription();
            void Refresh(object? sender, SystemLanguageChangedEventArgs args)
            {
                void Apply()
                {
                    if (closed.IsCancellationRequested || subscription.IsDisposed) return;
                    updateTitle?.Invoke();
                    model.OnLanguageChanged(sender, args);
                }
                if (Dispatcher.UIThread.CheckAccess()) Apply();
                else Dispatcher.UIThread.Post(Apply);
            }

            subscription.Unsubscribe = () => language.LanguageChanged -= Refresh;
            language.LanguageChanged += Refresh;
            return subscription;
        }
    }

    private sealed class LanguageSubscription : IDisposable
    {
        public Action? Unsubscribe;
        public bool IsDisposed => Volatile.Read(ref Unsubscribe) is null;
        public void Dispose() => Interlocked.Exchange(ref Unsubscribe, null)?.Invoke();
    }
    private static void DisposeOnUiThread(IDisposable model)
    {
        if (Dispatcher.UIThread.CheckAccess()) model.Dispose();
        else Dispatcher.UIThread.Post(model.Dispose);
    }
}
