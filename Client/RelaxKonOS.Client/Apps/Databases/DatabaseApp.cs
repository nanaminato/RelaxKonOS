using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Databases;

public sealed class DatabaseApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.database"), "Database engines", "0.1.0", "▤", "Database engine management",
        [AppPermissions.ServerComponentsRead], InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetRequiredService<IAuthSession>();
        if (session.State != AuthSessionState.Authenticated) return;
        var model = new DatabaseViewModel(context.Services.GetRequiredService<IPostgreSqlClient>())
        {
            Dependency = ComponentDependencySettingsViewModel.Create(context, "relaxkonos.database"),
            CanManage = session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller
        };
        var view = new Views.DatabaseWorkspace();
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.database.display_name"), view, new Rect(90, 60, 980, 700), Manifest.IconGlyph);
        model.Installation = new(context.Services.GetRequiredService<InstallationClient>(), context.Services.GetRequiredService<IAppSettingsClient>(),
            InstallationServiceId.PostgreSql, "relaxkonos.database", model.RefreshAsync,
            () => context.ShowDialogAsync<string?>(window, LocalizedText.Get("installation.elevation_title"),
                dialog => new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")), new Size(460, 230)));
        model.AdminRecoveryInstallation = new(context.Services.GetRequiredService<InstallationClient>(), context.Services.GetRequiredService<IAppSettingsClient>(),
            InstallationServiceId.PostgreSqlAdminRecovery, "relaxkonos.database", model.RefreshAsync,
            () => context.ShowDialogAsync<string?>(window, LocalizedText.Get("installation.elevation_title"),
                dialog => new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")), new Size(460, 230)), "admin-recovery-installation");
        model.ConfirmAdminRecoveryAsync = () => Confirm("database.admin_recovery_confirm");
        model.AdminRecoveryInstallation.PropertyChanged += (_, _) => { model.NotifyState(); model.AccountManager?.Notify(); };
        var remoteTask = new InstallationTaskViewModel(context.Services.GetRequiredService<InstallationClient>(), context.Services.GetRequiredService<IAppSettingsClient>(),
            InstallationServiceId.PostgreSqlRemoteAccess, "relaxkonos.database", model.RefreshAsync,
            () => context.ShowDialogAsync<string?>(window, LocalizedText.Get("installation.elevation_title"),
                dialog => new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")), new Size(460, 230)), "remote-installation");
        model.RemoteManager = new(context.Services.GetRequiredService<RelaxKonOS.Client.Apps.Certificates.IRemoteCertificateClient>(), remoteTask)
        { CanManage = model.CanManage, ConfirmAsync = () => Confirm("database.remote_confirm"), OtherBusy = () => model.MaintenanceBusy || model.Busy || model.CanResume || model.Installation.IsActive
            || model.AdminRecoveryInstallation.IsActive || model.CanResumeAdminRecovery || model.AccountManager?.Busy == true || model.AccountManager?.HasPendingRequest == true || model.AccountManager?.Installation.IsActive == true };
        remoteTask.PropertyChanged += (_, _) => { model.RemoteManager.Notify(); model.NotifyState(); model.AccountManager?.Notify(); };
        model.RemoteManager.PropertyChanged += (_, _) => model.NotifyState();
        var accountsTask = new InstallationTaskViewModel(context.Services.GetRequiredService<InstallationClient>(), context.Services.GetRequiredService<IAppSettingsClient>(),
            InstallationServiceId.PostgreSqlAccounts, "relaxkonos.database", model.RefreshAsync,
            () => context.ShowDialogAsync<string?>(window, LocalizedText.Get("installation.elevation_title"),
                dialog => new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")), new Size(460, 230)), "accounts-installation");
        model.AccountManager = new(context.Services.GetRequiredService<IPostgreSqlClient>(), accountsTask)
        { CanManage = model.CanManage, LifecycleBusy = () => model.MaintenanceBusy || model.Busy || model.Installation.IsActive || model.CanResume || model.AdminRecoveryInstallation.IsActive || model.CanResumeAdminRecovery || model.RemoteManager.Busy || model.RemoteManager.HasPending || remoteTask.IsActive };
        model.AccountManager.ConfirmAsync = command => Confirm("database.accounts_confirm",
            LocalizedText.Get("database.account_action." + command.Action) + " · " + command.Database + " · " + (command.Account ?? "—") + " · "
                + (command.Permission is { } permission ? LocalizedText.Get("database.permission." + permission) : ""));
        model.AccountManager.ConfirmRecoveryAsync = () => Confirm("database.confirm.Recover");
        model.AccountManager.AuthorizeAsync = async () =>
        {
            var password = await context.ShowDialogAsync<string?>(window, LocalizedText.Get("installation.elevation_title"),
                dialog => new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog, LocalizedText.Get("installation.elevation_message")), new Size(460, 230));
            if (string.IsNullOrEmpty(password)) return false;
            try { return await context.Services.GetRequiredService<InstallationClient>().ElevateAsync(InstallationServiceId.PostgreSqlAccounts, password, default); }
            finally { password = null; }
        };
        accountsTask.PropertyChanged += (_, _) => { model.AccountManager.Notify(); model.NotifyState(); };
        model.AccountManager.PropertyChanged += (_, _) => model.NotifyState();
        var maintenanceTask=new InstallationTaskViewModel(context.Services.GetRequiredService<InstallationClient>(),context.Services.GetRequiredService<IAppSettingsClient>(),
            InstallationServiceId.PostgreSqlMaintenance,"relaxkonos.database",model.RefreshAsync,
            ()=>context.ShowDialogAsync<string?>(window,LocalizedText.Get("installation.elevation_title"),
                dialog=>new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog,LocalizedText.Get("installation.elevation_message")),new Size(460,230)),"maintenance-installation");
        model.MaintenanceManager=new(context.Services.GetRequiredService<IPostgreSqlClient>(),maintenanceTask)
        {CanManage=model.CanManage,ConfirmAsync=command=>Confirm(command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.DeleteInstance?"database.delete_instance_confirm":command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.MigrateMajor?"database.migration_confirm":command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.DeleteBackup?"database.backup_delete_confirm":"database.maintenance_confirm",command is null?LocalizedText.Get("database.recover"):LocalizedText.Get("database.maintenance_action."+command.Action)+" · "+(command.Target?.Version??command.BackupId?.ToString()??command.InstanceId.ToString())),
            OtherBusy=()=>model.Busy||model.CanResume||model.Installation.IsActive||model.AdminRecoveryInstallation.IsActive||model.CanResumeAdminRecovery||model.RemoteManager.Busy||model.RemoteManager.HasPending||remoteTask.IsActive||model.AccountManager.Busy||model.AccountManager.HasPendingRequest||accountsTask.IsActive};
        maintenanceTask.PropertyChanged+=(_,_)=>{model.MaintenanceManager.Notify();model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        model.MaintenanceManager.PropertyChanged+=(_,_)=>{model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        var backupPolicyTask=new InstallationTaskViewModel(context.Services.GetRequiredService<InstallationClient>(),context.Services.GetRequiredService<IAppSettingsClient>(),
            InstallationServiceId.PostgreSqlBackupAutomation,"relaxkonos.database",model.RefreshAsync,
            ()=>context.ShowDialogAsync<string?>(window,LocalizedText.Get("installation.elevation_title"),
                dialog=>new RelaxKonOS.Client.Apps.FileServices.Views.FileServicesPasswordDialogView(dialog,LocalizedText.Get("installation.elevation_message")),new Size(460,230)),"backup-policy-installation");
        model.MaintenanceManager.ScheduleInstallation=backupPolicyTask;
        model.MaintenanceManager.ConfirmScheduleAsync=policy=>Confirm("database.backup_schedule_confirm",
            LocalizedText.Get(policy.Enabled?"database.backup_schedule_enabled":"database.backup_schedule_disabled")+" · "+policy.IntervalHours+" h · "+policy.KeepLast+" · "+LocalizedText.Get(policy.AllowDowntime?"database.backup_schedule_downtime":"database.backup_schedule_stopped_only"));
        backupPolicyTask.PropertyChanged+=(_,_)=>{model.MaintenanceManager.Notify();model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        _=backupPolicyTask.RestoreAsync();
        _=maintenanceTask.RestoreAsync();
        view.DataContext = model;
        model.Installation.PropertyChanged += (_, _) => { model.NotifyState(); model.AccountManager.Notify(); };
        model.ChoosePortAsync = async () =>
        {
            var port = await context.ShowDialogAsync<int>(window, LocalizedText.Get("database.initialize"),
                dialog => new Views.DatabaseInitializeDialog(value => dialog.Close(value)), new Size(520, 340));
            return port is >= 1024 and <= 65535 ? port : null;
        };
        model.ConfirmAsync = (action, state) => Confirm("database.confirm." + action);
        model.ConfirmRecoveryAsync = _ => Confirm("database.confirm.Recover");
        async Task<bool> Confirm(string key, string? detail = null)
        {
            var accepted = false;
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("application.relaxkonos.database.display_name"), dialog => new ConfirmDialogView
            { DataContext = new ConfirmDialogViewModel(LocalizedText.Get(key) + (detail is null ? "" : "\n\n" + detail), value => { accepted = value; dialog.Close(value); }) }, new Size(540, 280));
            return accepted;
        }
        var login = session.CurrentSession;
        EventHandler<AuthSessionStateChangedEventArgs> changed = (_, _) =>
        { if (!ReferenceEquals(login, session.CurrentSession) || session.State != AuthSessionState.Authenticated) Avalonia.Threading.Dispatcher.UIThread.Post(model.Invalidate); };
        session.StateChanged += changed;
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            session.StateChanged -= changed; context.WindowManager.WindowClosed -= closed; model.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = accountsTask.RestoreAsync();
        _ = remoteTask.RestoreAsync(); _ = model.RemoteManager.RefreshCertificatesAsync();
        _ = model.AdminRecoveryInstallation.RestoreAsync();
        _ = model.RefreshAsync(); _ = model.Installation.RestoreAsync(); _ = model.Dependency.RefreshCommand.ExecuteAsync(null);
    }
}
