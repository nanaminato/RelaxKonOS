using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.App.Squid.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Squid;

public sealed class SquidPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.squid.display_name";

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.squid"),
        "Squid Gateway", "1.0.0", "⇥", "HTTP and HTTPS forward proxy gateway",
        [AppPermissions.ServerSquidRead, AppPermissions.ServerSquidManage, AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.GatewayProxy]),
        IconPath: "avares://RelaxKonOS.App.Squid/Assets/squid.png",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent) return;

        var language = context.SystemLanguage;
        string Localize(string key, string fallback) => SquidLocalization.Resolve(language, key, fallback);
        string Label(string key) => Localize(key, key);
        var dependencies = context.ComponentDependencies;
        var manageComponent = dependencies is null ? null : new RelayCommand(() => dependencies.OpenManagement("squid"));
        var client = context.Squid;
        var model = new SquidViewModel(client, Localize) { GetComponentManagementCommand = () => manageComponent };
        IExternalAppWindowHandle? owner = null;
        var view = new SquidWorkspace(model)
        {
            CreateDependencySettings = () => owner is null ? null : dependencies?.CreateSettingsView(owner),
        };
        var window = context.Windows.ShowWindow(Label(TitleKey), view, new Rect(90, 55, 960, 760), Manifest.IconGlyph);
        owner = window;
        view.AttachWindowHeader(window.Window);
        using var initialize = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Closed);
        var token = initialize.Token;

        ModalDialog<bool>? activeEditor = null;
        view.ShowEditorAsync = async (title, editor) =>
        {
            SquidEditorDialog? content = null;
            try
            {
                await window.ShowDialogAsync<bool>(title, dialog =>
                {
                    activeEditor = dialog;
                    dialog.CanCancelAsync = () => Task.FromResult(!model.IsBusy);
                    return content = new SquidEditorDialog(editor, dialog);
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
                ? window.ConfirmNestedAsync(dialog, Label(title), Label(message), Label("common.confirm"))
                : window.ConfirmAsync(Label(title), Label(message), Label("common.confirm"));
        model.ConfirmDeleteAccountAsync = _ => ConfirmAsync("squid.account_delete", "squid.account_delete_message");
        model.ConfirmApplyAsync = () => ConfirmAsync("squid.apply_title", "squid.apply_message");
        model.ConfirmReloadAsync = () => ConfirmAsync("common.discard_title", "squid.reload_message");
        window.Window.CanCloseAsync = () => model.IsBusy || model.HasPending || model.HasUnsavedChanges || model.HasAccountDraft
            ? ConfirmAsync("common.discard_title", "squid.close_message") : Task.FromResult(true);

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
        if (permissions.GetStatus(AppPermissions.ServerSquidRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerSquidRead, token);
        if (token.IsCancellationRequested || !connection.IsCurrent) return;
        model.CanRead = permissions.IsGranted(AppPermissions.ServerSquidRead);
        if (!model.CanRead) return;
        await model.RefreshCommand.ExecuteAsync(null);
        if (token.IsCancellationRequested || !connection.IsCurrent || model.Snapshot?.Status is { Supported: false }) return;
        if (permissions.GetStatus(AppPermissions.ServerSquidManage) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerSquidManage, token);
        if (!token.IsCancellationRequested && connection.IsCurrent) model.CanManage = client.CanManage;

        void Dispatch(Action action)
        {
            void Apply() { if (!window.Closed.IsCancellationRequested) action(); }
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }
    }
}
