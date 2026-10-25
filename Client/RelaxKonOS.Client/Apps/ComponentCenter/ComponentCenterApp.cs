using Size = RelaxKonOS.Core.Primitives.Size;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.FileServices.Views;
using RelaxKonOS.Client.Apps.ProxyServer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

using RelaxKonOS.Client.Apps.ProxyServer;
using Avalonia.Controls;

/// <summary>Desktop entry point for managed host component lifecycles.</summary>
public sealed class ComponentCenterApp : RemoteApplicationBase, IAppActivationHandler
{
    private ComponentCenterViewModel? _model;
    public bool CanHandleActivation(Uri uri) => ComponentCenterNavigation.TryParse(uri, out _);
    public void HandleActivation(AppContext context, AppActivationRequest request, RelaxKonOS.WindowManager.ManagedWindow? existingWindow)
    {
        if (_model is null || !ComponentCenterNavigation.TryParse(request.Uri, out var target)) return;
        if (target!.HostId != _model.HostIdentity || _model.Runtime.IsInvalidated)
        {
            _model.Notice = LocalizedStatus.Key("component_center.host_mismatch");
            return;
        }
        _model.Locate(target);
        // Navigation only changes context, never a draft or approval.
        _model.Notice = LocalizedStatus.Key("component_center.located");
    }
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.component-center"), "Component Center", "1.0.0", "⇄", "Managed host components",
        [AppPermissions.ServerProxyRead, AppPermissions.ServerProxyManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow,
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/proxy-server.png");

    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        if (session?.State != AuthSessionState.Authenticated) return;
        if (context.Services.GetService(typeof(IRuntimeCatalogClient)) is not IRuntimeCatalogClient catalog) return;
        var runtime = new ProxyServerRuntimeViewModel(catalog);
        var drafts = (ComponentDraftStore?)context.Services.GetService(typeof(ComponentDraftStore))
            ?? throw new InvalidOperationException("Component draft store is not registered.");
        var model = new ComponentCenterViewModel(runtime, drafts, session.ServiceId!, session.CurrentUser!.Id,
            (ITaskPlanPreviewClient?)context.Services.GetService(typeof(ITaskPlanPreviewClient)),
            (ITaskExecutionClient?)context.Services.GetService(typeof(ITaskExecutionClient)),
            (ComponentTaskReferenceStore?)context.Services.GetService(typeof(ComponentTaskReferenceStore)));
        model.ConnectionAvailable = true;
        _model = model;
        var view = new ComponentCenterWorkspace(model);
        var availability = context.Services.GetService(typeof(ApplicationAvailabilityService)) as ApplicationAvailabilityService;
        RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallationSnapshot? lastObservation = null;
        System.ComponentModel.PropertyChangedEventHandler observed = (_, args) =>
        {
            if (args.PropertyName != nameof(runtime.Installation) || ReferenceEquals(lastObservation, runtime.Installation)) return;
            lastObservation = runtime.Installation;
            availability?.Clear(); // A lifecycle refresh invalidates launch badges; the next open checks the host again.
        };
        runtime.PropertyChanged += observed;
        view.OpenApplication = () => (context.Services.GetService(typeof(RelaxKonOS.Runtime.ApplicationManager)) as RelaxKonOS.Runtime.ApplicationManager)?.Launch(new AppId("relaxkonos.proxy-server"));
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.component-center.display_name"),
            view, new Rect(80, 45, 1060, 800), Manifest.IconGlyph);
        runtime.ConfirmAsync = (title, message) => ConfirmAsync(title.Resolve(), message.Resolve());
        model.ConfirmTaskAsync = async plan => await ShowOwnedAsync<bool>(LocalizedText.Get("component_center.task_confirm_title"),
            dialog => new TaskPlanConfirmationView(dialog, plan), new Size(720, 600)) == true;
        model.TaskCredentialsAsync = () => ShowOwnedAsync<RelaxKonOS.Client.Apps.FileServices.HostAdministratorCredentials?>(
            LocalizedText.Get("installation.elevation_title"), dialog => new HostAdministratorCredentialsDialogView(dialog,
                LocalizedText.Get("component_center.task_credentials"), null, session.CurrentUser?.Username ?? ""), new Size(560, 340));
        model.ConfirmTaskRecoveryAsync = (action, recovery) => ConfirmAsync(LocalizedText.Get("component_center.task_recovery_action." + action),
            LocalizedStatus.Format("component_center.task_recovery_confirm", recovery.Run.HostId, recovery.Recovery.OperationId,
                LocalizedText.Get("component_center.task_recovery_effect." + action)).Resolve());
        runtime.PasswordPromptAsync = () => ShowOwnedAsync<string?>(
            LocalizedText.Get("installation.elevation_title"),
            dialog => new FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")),
            new Size(460, 230));
        window.CanCloseAsync = async () =>
        {
            if (!model.HasDrafts || runtime.IsBusy) return true;
            var keep = true; // Dismissing the dialog never discards a draft.
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("component_center.close_title"), dialog =>
                new ConfirmDialogView { DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Get("component_center.close_message"), value => { keep = value; dialog.Close(value); },
                    LocalizedText.Get("component_center.keep"), LocalizedText.Get("component_center.discard")) }, new Size(560, 280));
            if (!keep) model.DiscardCommand.Execute(null);
            model.Persist();
            return true;
        };
        // Closing the UI must not cancel a host transaction already accepted by the ledger.
        var login = session.CurrentSession;
        var access = new ComponentCenterAccess(session, context.Permissions, runtime,
            () => session.CurrentServer?.Capabilities.Contains(ServerCapabilities.ProxyServer) == true,
            connected => model.ConnectionAvailable = connected);
        runtime.RefreshAccess = access.Synchronize;
        EventHandler<AuthSessionStateChangedEventArgs> sessionChanged = (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(access.Synchronize);
        };
        session.StateChanged += sessionChanged;
        var taskRefresh = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        taskRefresh.Tick += async (_, _) =>
        {
            access.Synchronize();
            var wasTaskActive = model.CurrentTaskRun?.State is RelaxKonOS.Protocol.TaskOrchestration.TaskRunState.Queued or RelaxKonOS.Protocol.TaskOrchestration.TaskRunState.Running or RelaxKonOS.Protocol.TaskOrchestration.TaskRunState.Cancelling;
            await model.TickTaskAsync();
            if (runtime.CanRead && !runtime.IsBusy && !model.IsTaskBusy && !runtime.IsInvalidated && (runtime.HasActiveOperation || wasTaskActive))
                await runtime.RefreshActiveOperationAsync();
        };
        taskRefresh.Start();
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= sessionChanged;
            taskRefresh.Stop();
            runtime.RefreshAccess = null;
            if (ReferenceEquals(_model, model)) _model = null;
            runtime.PropertyChanged -= observed;
            model.Dispose();
            runtime.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            if (session.CurrentServer?.Capabilities.Contains(ServerCapabilities.ProxyServer) != true)
            {
                runtime.OutcomeText = LocalizedStatus.Key("proxy_server.problem.platform_unsupported");
                return;
            }
            if (!await PermissionAsync(AppPermissions.ServerProxyRead))
            {
                runtime.OutcomeText = LocalizedStatus.Key("proxy_server.permission_denied");
                return;
            }
            runtime.CanRead = true;
            runtime.CanManage = session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller
                && await PermissionAsync(AppPermissions.ServerProxyManage);
            access.Synchronize();
            await runtime.EnsureLoadedAsync();
        }
        async Task<bool> PermissionAsync(string permission)
        {
            var status = context.Permissions.GetStatus(permission);
            if (status == AppPermissionStatus.Undecided) status = await context.Permissions.RequestAsync(permission);
            return status == AppPermissionStatus.Granted && !runtime.IsInvalidated && session.State == AuthSessionState.Authenticated
                && ReferenceEquals(login, session.CurrentSession);
        }
        Task<TResult?> ShowOwnedAsync<TResult>(string title, Func<RelaxKonOS.WindowManager.ModalDialog<TResult>, Avalonia.Controls.Control> factory, RelaxKonOS.Core.Primitives.Size size)
            => context.ShowDialogAsync(window, title, factory, size);

        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await ShowOwnedAsync<bool>( title, dialog => new ConfirmDialogView
            { DataContext = new ConfirmDialogViewModel(message, value => { accepted = value; dialog.Close(value); }) }, new Size(560, 280));
            return accepted;
        }
    }

}
