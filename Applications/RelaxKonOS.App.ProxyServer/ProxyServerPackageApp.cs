using Avalonia.Threading;
using RelaxKonOS.App.ProxyServer.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.ProxyServer;

public sealed class ProxyServerPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.proxy-server.display_name";

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.proxy-server"),
        "Proxy Server", "1.0.0", "⇄", "sing-box proxy server",
        [AppPermissions.ServerProxyServerRead, AppPermissions.ServerProxyServerManage, AppPermissions.ServerProxyServerExport,
            AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.ProxyServer]),
        IconPath: "avares://RelaxKonOS.App.ProxyServer/Assets/proxy-server.png",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent) return;

        var language = context.SystemLanguage;
        string Localize(string key, string fallback) => ProxyServerLocalization.Resolve(language, key, fallback);
        string Label(string key) => Localize(key, key);
        var model = new ProxyServerViewModel(context.ProxyServer, Localize);
        IExternalAppWindowHandle? owner = null;
        var view = new ProxyServerWorkspace(model)
        {
            CreateDependencySettings = () => owner is null ? null : context.ComponentDependencies?.CreateSettingsView(owner),
        };
        var window = context.Windows.ShowWindow(Label(TitleKey), view, new Rect(80, 45, 1060, 800), Manifest.IconGlyph);
        owner = window;
        view.AttachWindowHeader(window.Window);
        using var initialize = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Closed);
        var token = initialize.Token;

        ModalDialog<bool>? activeEditor = null;
        view.ShowEditorAsync = async (title, editor) =>
        {
            ProxyServerEditorDialog? content = null;
            try
            {
                await window.ShowDialogAsync<bool>(title, dialog =>
                {
                    activeEditor = dialog;
                    dialog.CanCancelAsync = () => Task.FromResult(!model.IsBusy);
                    return content = new ProxyServerEditorDialog(editor, dialog);
                }, new Size(720, 650));
            }
            finally
            {
                activeEditor = null;
                content?.DetachEditor();
            }
        };

        Task<bool> ConfirmAsync(string title, string message) =>
            activeEditor is { } dialog
                ? window.ConfirmNestedAsync(dialog, title, message, Label("common.confirm"))
                : window.ConfirmAsync(title, message, Label("common.confirm"));
        model.ConfirmAsync = (title, message) => ConfirmAsync(title.Resolve(), message.Resolve());
        window.Window.CanCloseAsync = () => model.IsBusy || model.HasUnsavedChanges
            ? ConfirmAsync(Label("common.discard_title"), Label("proxy_server.close_warning"))
            : Task.FromResult(true);

        void RefreshLanguage(object? sender, SystemLanguageChangedEventArgs args) => Dispatch(() =>
        {
            window.Window.Title = Label(TitleKey);
            model.RefreshLanguage();
        });
        language.LanguageChanged += RefreshLanguage;
        var observation = connection.ObserveInvalidation(() => Dispatch(model.InvalidateSession));
        window.Closed.Register(() =>
        {
            language.LanguageChanged -= RefreshLanguage;
            observation.Dispose();
            if (Dispatcher.UIThread.CheckAccess()) model.Dispose();
            else Dispatcher.UIThread.Post(model.Dispose);
        });

        var permissions = context.Permissions;
        model.CanRead = await RequestAsync(AppPermissions.ServerProxyServerRead);
        if (!model.CanRead) return;
        await model.RefreshCommand.ExecuteAsync(null);
        if (token.IsCancellationRequested || model.Snapshot?.Capabilities.Supported != true || !connection.IsController) return;
        model.CanManage = await RequestAsync(AppPermissions.ServerProxyServerManage);
        model.CanExport = await RequestAsync(AppPermissions.ServerProxyServerExport);
        if (model.CanExport && model.SelectedCredential is null && model.Credentials.Count > 0)
            model.SelectedCredential = model.Credentials[0];

        async Task<bool> RequestAsync(string permission)
        {
            if (token.IsCancellationRequested || !connection.IsCurrent
                || permission != AppPermissions.ServerProxyServerRead && !connection.IsController) return false;
            if (permissions.GetStatus(permission) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(permission, token);
            return !token.IsCancellationRequested && connection.IsCurrent && permissions.IsGranted(permission)
                && (permission == AppPermissions.ServerProxyServerRead || connection.IsController);
        }

        void Dispatch(Action action)
        {
            void Apply() { if (!window.Closed.IsCancellationRequested) action(); }
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }
    }
}
