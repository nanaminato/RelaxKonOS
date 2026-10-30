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
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;

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
        var taskTarget = target.Page is ComponentCenterPage.Tasks or ComponentCenterPage.Workloads;
        _model.Locate(taskTarget ? target with { Page = ComponentCenterPage.All, WorkloadId = null } : target);
        if (target.WorkloadId is { } workload)
            _model.TaskWorkload = _model.TaskWorkloads.FirstOrDefault(item => item.WorkloadId == workload);
        if (taskTarget) _ = _model.OpenTasksCommand.ExecuteAsync(null);
        // Navigation only changes context, never a draft or approval.
        _model.Notice = LocalizedStatus.Key("component_center.located");
    }
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.component-center"), "Component Center", "1.0.0", "⇄", "Managed host components",
        [AppPermissions.ServerComponentsRead, AppPermissions.ServerComponentsManage, AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage, AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow,
        IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/proxy-server.png");

    public override void Activate(AppContext context)
    {
        var session = (IAuthSession?)context.Services.GetService(typeof(IAuthSession));
        if (session?.State != AuthSessionState.Authenticated) return;
        if (context.Services.GetService(typeof(IRuntimeCatalogClient)) is not IRuntimeCatalogClient catalog) return;
        var runtime = new ProxyServerRuntimeViewModel(catalog);
        // The Proxy Manager runtime is already fully implemented on Server and Helper; the center
        // observes it instead of asking users to keep using the application's own runtime page.
        var mihomo = context.Services.GetService(typeof(IMihomoRuntimeCatalogClient)) is IMihomoRuntimeCatalogClient mihomoCatalog
            ? new ProxyServerRuntimeViewModel(mihomoCatalog) : null;
        var additionalRuntimes = mihomo is null
            ? new Dictionary<string, ProxyServerRuntimeViewModel>()
            : new Dictionary<string, ProxyServerRuntimeViewModel> { ["mihomo"] = mihomo };
        var drafts = (ComponentDraftStore?)context.Services.GetService(typeof(ComponentDraftStore))
            ?? throw new InvalidOperationException("Component draft store is not registered.");
        var model = new ComponentCenterViewModel(runtime, drafts, session.ServiceId!, session.CurrentUser!.Id,
            (ITaskPlanPreviewClient?)context.Services.GetService(typeof(ITaskPlanPreviewClient)),
            (ITaskExecutionClient?)context.Services.GetService(typeof(ITaskExecutionClient)),
            (ComponentTaskReferenceStore?)context.Services.GetService(typeof(ComponentTaskReferenceStore)),
            additionalRuntimes,
            (context.Services.GetService(typeof(IEnumerable<IComponentHostObserver>)) as IEnumerable<IComponentHostObserver>)?.ToArray(),
            context.Services.GetService(typeof(IComponentLifecycleClient)) as IComponentLifecycleClient,
            hostPlatform: session.CurrentServer?.Platform switch { HostPlatformKind.Windows => "windows", HostPlatformKind.Linux => "linux", _ => null });
        if (context.Services.GetService(typeof(RelaxKonOS.Client.Apps.Git.IRemoteGitClient)) is RelaxKonOS.Client.Apps.Git.IRemoteGitClient git)
        {
            model.GitEngine = new(git);
            model.GitEngine.PropertyChanged += (_, _) => model.RefreshGitState();
        }
        model.ConnectionAvailable = true;
        _model = model;
        if (context.Services.GetService(typeof(RelaxKonOS.Client.Apps.WebServers.IRemoteWebServerClient)) is RelaxKonOS.Client.Apps.WebServers.IRemoteWebServerClient web
            && context.Services.GetService(typeof(RelaxKonOS.Client.Apps.Certificates.IRemoteCertificateClient)) is RelaxKonOS.Client.Apps.Certificates.IRemoteCertificateClient certificates
            && context.Services.GetService(typeof(RelaxKonOS.Client.Services.Privileged.IHostElevationBroker)) is RelaxKonOS.Client.Services.Privileged.IHostElevationBroker elevations)
        {
            model.NginxSelection = new(web, certificates, session, context.Permissions, elevations) { IsComponentCenter = true };
        }
        var view = new ComponentCenterWorkspace(model);
        var availability = context.Services.GetService(typeof(ApplicationAvailabilityService)) as ApplicationAvailabilityService;
        RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallationSnapshot? lastObservation = null;
        System.ComponentModel.PropertyChangedEventHandler observed = (sender, args) =>
        {
            if (args.PropertyName != nameof(runtime.Installation) || sender is not ProxyServerRuntimeViewModel changed
                || ReferenceEquals(lastObservation, changed.Installation)) return;
            lastObservation = changed.Installation;
            availability?.Clear(); // A lifecycle refresh invalidates launch badges; the next open checks the host again.
        };
        runtime.PropertyChanged += observed;
        if (mihomo is not null) mihomo.PropertyChanged += observed;
        var applications = context.Services.GetService(typeof(RelaxKonOS.Runtime.ApplicationManager)) as RelaxKonOS.Runtime.ApplicationManager;
        view.OpenApplication = applicationId => applications?.Launch(new AppId(applicationId));
        RelaxKonOS.WindowManager.ModalDialog<bool>? taskDialog = null;
        var taskDialogOpen = false;
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.component-center.display_name"),
            view, new Rect(80, 45, 1060, 800), Manifest.IconGlyph);
        model.SelectLocalComponentPackageAsync = async task =>
        {
            var top = TopLevel.GetTopLevel(view);
            if (top is null) return null;
            var files = await UsageFilePicker.OpenFilePickerAsync(top.StorageProvider, new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("component_center.download.local_picker"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(LocalizedText.Get("component_center.download.archive")) { Patterns = ["*.zip", "*.tar.gz", "*.gz"] }]
            }, UsageMemoryStore.Capture(context), "ComponentCenter.package");
            if (files.FirstOrDefault() is not { } file) return null;
            await using var stream = await file.OpenReadAsync();
            return await task.UploadPackageAsync(file.Name, stream) is { } reference ? new ComponentPackageSelection(reference, file.Name) : null;
        };
        model.SelectServerComponentPackageAsync = async task =>
        {
            if (context.Services.GetService(typeof(IExplorerClient)) is not IExplorerClient files) return null;
            var selected = await context.ShowDialogAsync<IReadOnlyList<string>>(window,
                LocalizedText.Get("component_center.download.server_picker"), dialog =>
                {
                    var picker = new ExplorerViewModel(files, new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, AllowMultiple: false,
                        Filters: [new ExplorerFileFilter(LocalizedText.Get("component_center.download.archive"), ["*.zip", "*.tar.gz", "*.gz"])]),
                        paths => dialog.Close(paths), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "ComponentCenter.package") { CancelAction = dialog.Cancel };
                    _ = picker.LoadRootAsync();
                    return new ExplorerMainView { DataContext = picker };
                }, new Size(800, 560));
            if (selected?.FirstOrDefault() is not { } path) return null;
            return await task.CreateFileReferenceAsync(path) is { } reference ? new ComponentPackageSelection(reference, path) : null;
        };
        model.OpenComponentDownloadAsync = url =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
            return Task.CompletedTask;
        };
        model.ShowTasksDialogAsync = async () =>
        {
            if (taskDialogOpen) return;
            taskDialogOpen = true;
            try
            {
                await context.ShowDialogAsync<bool>(window, LocalizedText.Get("component_center.task_dialog.title"), dialog =>
                {
                    taskDialog = dialog;
                    dialog.CanCancelAsync = () => Task.FromResult(!model.IsTaskBusy && !model.IsInspecting);
                    return new ComponentCenterTasksView(model, dialog.Cancel);
                }, new Size(840, 720));
            }
            finally { taskDialog = null; taskDialogOpen = false; }
        };
        if (model.NginxSelection is { } nginxSelection)
            nginxSelection.RequestIntegrationConfirmationAsync = () => ConfirmAsync(
                LocalizedText.Get("webservers.integration.confirmation.title"), LocalizedText.Get("webservers.integration.confirmation.message"));
        if (model.NginxSelection is { } nginxMaintenance)
            nginxMaintenance.RequestManagedUninstallConfirmationAsync = () => ConfirmAsync(
                LocalizedText.Get("webservers.managed.uninstall.title"), LocalizedText.Get("webservers.managed.uninstall.message"));
        foreach (var host in new ProxyServerRuntimeViewModel?[] { runtime, mihomo })
        {
            if (host is null) continue;
            var target = host;
            target.ConfirmAsync = (title, message) => ConfirmAsync(title.Resolve(), message.Resolve());
            target.PasswordPromptAsync = () => ShowOwnedAsync<string?>(
                LocalizedText.Get("installation.elevation_title"),
                dialog => new FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")),
                new Size(460, 230));
        }
        if (context.Services.GetService(typeof(RelaxKonOS.Client.Services.Installation.InstallationClient))
                is RelaxKonOS.Client.Services.Installation.InstallationClient installationClient
            && context.Services.GetService(typeof(RelaxKonOS.Client.Services.AppSettings.IAppSettingsClient))
                is RelaxKonOS.Client.Services.AppSettings.IAppSettingsClient settings)
        {
            foreach (var (componentId, serviceId) in new[] {
                ("git", RelaxKonOS.Protocol.Installations.InstallationServiceId.Git),
                ("tailscale", RelaxKonOS.Protocol.Installations.InstallationServiceId.Tailscale),
                ("nginx", RelaxKonOS.Protocol.Installations.InstallationServiceId.Nginx),
                ("mihomo", RelaxKonOS.Protocol.Installations.InstallationServiceId.Mihomo),
                ("squid", RelaxKonOS.Protocol.Installations.InstallationServiceId.Squid),
                ("samba", RelaxKonOS.Protocol.Installations.InstallationServiceId.Smb),
                ("docker", RelaxKonOS.Protocol.Installations.InstallationServiceId.Docker),
                ("frpc", RelaxKonOS.Protocol.Installations.InstallationServiceId.Frp),
                ("frps", RelaxKonOS.Protocol.Installations.InstallationServiceId.Frp) })
            {
                var task = new RelaxKonOS.Client.Services.Installation.InstallationTaskViewModel(installationClient, settings,
                    serviceId, "relaxkonos.component-center", () => model.RefreshPackageInstallationAsync(componentId),
                    () => ShowOwnedAsync<string?>(LocalizedText.Get("installation.elevation_title"),
                        dialog => new FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")),
                        new Size(460, 230)), "installation." + componentId);
                model.AttachPackageInstallation(componentId, task);
                if (componentId == "nginx" && model.NginxSelection is { } selectedNginx) selectedNginx.Installation = task;
                _ = task.RestoreAsync(); // Observe only, including after a window is reopened.
            }
            model.ConfirmPackageInstallAsync = freeze =>
            {
                var package = freeze.Target!.Package;
                var pin = freeze.Target.Pin;
                var installer = freeze.Target.Installer;
                var dependencies = package is not null ? string.Join("\n", package.AlsoInstalls.Select(item =>
                    $"{item.FileName} · {item.Bytes} B · SHA-256 {item.Sha256}")) : installer is not null ? "Windows Installer (MSI) · Tailscale Inc."
                        : string.Join(", ", pin!.AffectedComponents);
                return ConfirmAsync(LocalizedText.Get("component_center.native.action." + freeze.Request!.Change),
                    LocalizedStatus.Format("component_center.native.confirm", LocalizedText.Get("component_center.native.action." + freeze.Request!.Change), freeze.State!.HostId,
                        package?.Version ?? installer?.Version ?? pin!.Version, package?.RepositoryId ?? installer?.DownloadUrl ?? pin!.SourceUri,
                        package?.Sha256 ?? installer?.Sha256 ?? pin!.Sha256, dependencies, freeze.Request!.ComponentId).Resolve());
            };
        }
        model.ConfirmPackageRecoveryAsync = (component, operation, action) => ConfirmAsync(
            LocalizedText.Get("component_center.package.recovery." + action),
            LocalizedStatus.Format(action == RelaxKonOS.Protocol.ManagedRuntimes.ComponentRecoveryAction.ConfigureOfficialSource
                ? "component_center.package.tailscale_source_confirm" : action == RelaxKonOS.Protocol.ManagedRuntimes.ComponentRecoveryAction.RefreshSources
                ? "component_center.package.refresh_confirm" : "component_center.package.recovery_confirm", component, operation,
                LocalizedText.Get("component_center.package.recovery." + action)).Resolve());
        model.ConfirmTaskAsync = async plan => await ShowOwnedAsync<bool>(LocalizedText.Get("component_center.task_confirm_title"),
            dialog => new TaskPlanConfirmationView(dialog, plan), new Size(720, 600)) == true;
        model.TaskCredentialsAsync = () => ShowOwnedAsync<RelaxKonOS.Client.Apps.FileServices.HostAdministratorCredentials?>(
            LocalizedText.Get("installation.elevation_title"), dialog => new HostAdministratorCredentialsDialogView(dialog,
                LocalizedText.Get("component_center.task_credentials"), null, session.CurrentUser?.Username ?? ""), new Size(560, 340));
        model.ConfirmTaskRecoveryAsync = (action, recovery) => ConfirmAsync(LocalizedText.Get("component_center.task_recovery_action." + action),
            LocalizedStatus.Format("component_center.task_recovery_confirm", recovery.Run.HostId, recovery.Recovery.OperationId,
                LocalizedText.Get("component_center.task_recovery_effect." + action)).Resolve());
        window.CanCloseAsync = async () =>
        {
            if (!model.HasDrafts || runtime.IsBusy || mihomo?.IsBusy == true) return true;
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
            () => session.CurrentServer?.Capabilities.Contains(ServerCapabilities.Components) == true,
            connected => model.ConnectionAvailable = connected,
            mihomo is null ? null : [mihomo]);
        runtime.RefreshAccess = access.Synchronize;
        if (mihomo is not null) mihomo.RefreshAccess = access.Synchronize;
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
            // The selected component may be the Proxy Manager runtime; refresh only what it owns.
            if (model.DetailRuntime is { } active && !ReferenceEquals(active, runtime) && active.CanRead
                && !active.IsBusy && !active.IsInvalidated && active.HasActiveOperation)
                await active.RefreshActiveOperationAsync();
        };
        taskRefresh.Start();
        System.ComponentModel.PropertyChangedEventHandler? selectedDomainChanged = null;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed; session.StateChanged -= sessionChanged;
            taskRefresh.Stop();
            runtime.RefreshAccess = null;
            if (ReferenceEquals(_model, model)) _model = null;
            runtime.PropertyChanged -= observed;
            if (mihomo is not null)
            {
                mihomo.PropertyChanged -= observed;
                mihomo.RefreshAccess = null;
                mihomo.Dispose();
            }
            model.PropertyChanged -= selectedDomainChanged;
            model.ShowTasksDialogAsync = null;
            model.Dispose();
            runtime.Dispose();
        };
        var preparingDomain = false;
        string? preparedDomain = null;
        selectedDomainChanged = async (_, args) =>
        {
            if (args.PropertyName != nameof(model.SelectedDefinition) || preparingDomain || !runtime.CanRead || runtime.IsInvalidated || preparedDomain == model.SelectedDefinition?.ComponentId) return;
            preparingDomain = true;
            preparedDomain = model.SelectedDefinition?.ComponentId;
            try
            {
                if (model.SelectedDefinition?.ComponentId == "nginx" && model.NginxSelection is { } nginx)
                {
                    if (await PermissionAsync(AppPermissions.ServerWebServersRead))
                    {
                        await PermissionAsync(AppPermissions.ServerWebServersManage);
                        await nginx.RefreshCommand.ExecuteAsync(null);
                    }
                }
                if (model.SelectedDefinition?.ComponentId == "git" && model.GitEngine is { } gitEngine)
                {
                    if (await PermissionAsync(AppPermissions.ServerGitRead))
                    {
                        model.GitCanManage = await PermissionAsync(AppPermissions.ServerGitManage);
                        await gitEngine.RefreshEngineStatusCommand.ExecuteAsync(null);
                        model.NotifyGit();
                    }
                }
                if (model.CanResolvePackage)
                    await model.ResolvePackageCommand.ExecuteAsync(null);
                if (model.ShowOfficialDownloads)
                {
                    try { await model.RefreshPackageInstallationAsync(model.SelectedDefinition!.ComponentId); }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException)
                    { model.OfficialDownloadNotice = LocalizedStatus.Key("component_center.package.problem.state_unavailable"); }
                    await model.LoadOfficialReleasesAsync(false);
                }
            }
            finally
            {
                preparingDomain = false;
                if (preparedDomain != model.SelectedDefinition?.ComponentId)
                    selectedDomainChanged?.Invoke(model, new(nameof(model.SelectedDefinition)));
            }
        };
        model.PropertyChanged += selectedDomainChanged;
        context.WindowManager.WindowClosed += closed;
        _ = InitializeAsync();
        async Task InitializeAsync()
        {
            if (session.CurrentServer?.Capabilities.Contains(ServerCapabilities.Components) != true)
            {
                runtime.OutcomeText = LocalizedStatus.Key("proxy_server.problem.platform_unsupported");
                if (mihomo is not null) mihomo.OutcomeText = runtime.OutcomeText;
                return;
            }
            if (!await PermissionAsync(AppPermissions.ServerComponentsRead))
            {
                runtime.OutcomeText = LocalizedStatus.Key("proxy_server.permission_denied");
                if (mihomo is not null) mihomo.OutcomeText = runtime.OutcomeText;
                return;
            }
            runtime.CanRead = true;
            runtime.CanManage = session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller
                && await PermissionAsync(AppPermissions.ServerComponentsManage);
            access.Synchronize();
            if (model.HostPlatform != "windows")
            {
                await runtime.EnsureLoadedAsync();
                if (mihomo is { CanRead: true }) await mihomo.EnsureLoadedAsync();
            }
            selectedDomainChanged?.Invoke(model, new(nameof(model.SelectedDefinition)));
        }
        async Task<bool> PermissionAsync(string permission)
        {
            var status = context.Permissions.GetStatus(permission);
            if (status == AppPermissionStatus.Undecided) status = await context.Permissions.RequestAsync(permission);
            return status == AppPermissionStatus.Granted && !runtime.IsInvalidated && session.State == AuthSessionState.Authenticated
                && ReferenceEquals(login, session.CurrentSession);
        }
        Task<TResult?> ShowOwnedAsync<TResult>(string title, Func<RelaxKonOS.WindowManager.ModalDialog<TResult>, Avalonia.Controls.Control> factory, RelaxKonOS.Core.Primitives.Size size)
            => taskDialog is { } activeTaskDialog ? activeTaskDialog.ShowDialogAsync(title, factory, size)
                : context.ShowDialogAsync(window, title, factory, size);

        async Task<bool> ConfirmAsync(string title, string message)
        {
            var accepted = false;
            await ShowOwnedAsync<bool>( title, dialog => new ConfirmDialogView
            { DataContext = new ConfirmDialogViewModel(message, value => { accepted = value; dialog.Close(value); }) }, new Size(560, 280));
            return accepted;
        }
    }

}
