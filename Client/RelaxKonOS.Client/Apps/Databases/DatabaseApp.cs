using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.App.Databases.Views;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.App.Databases;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
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
        var language = context.Services.GetRequiredService<ISystemLanguage>();
        string Text(string key) => DatabaseLocalization.Resolve(language, key, $"[missing: {key}]");
        var connection = new ExternalServerConnection(context.Services.GetRequiredService<UsageMemoryStore>().Capture(session));
        var dependency = ComponentDependencySettingsViewModel.Create(context, Manifest.Id.Value);
        var settings = new ExternalAppSettings(Manifest.Id, context.Services.GetRequiredService<IAppSettingsClient>());
        var model = new DatabaseViewModel(context.Services.GetRequiredService<IServerPostgreSql>())
        {
            DependencySettings = new ComponentDependencySettingsView { DataContext = dependency },
            CanManage = connection.IsController
        };
        Task<bool> AuthorizeInstallationAsync(InstallationServiceId service) => context.Services
            .GetRequiredService<RelaxKonOS.Client.Services.Privileged.IHostElevationBroker>()
            .RequestGrantAsync(InstallationClient.ElevationCapability(service), service.ToString().ToLowerInvariant());
        var view = new DatabaseWorkspace();
        DatabaseWorkspaceLanguage.Bind(view, language);
        var window = context.ShowWindow(Text("application.relaxkonos.database.display_name"), view, new Rect(90, 60, 980, 700), Manifest.IconGlyph);
        model.Installation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.PostgreSql, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSql));
        var mysql = new MySqlViewModel(context.Services.GetRequiredService<IServerMySql>(), connection)
        {
            CanManage = model.CanManage,
            ConfirmAsync = () => Confirm("database.mysql_confirm")
        };
        mysql.Installation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.MySql, false, mysql.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.MySql),
            "mysql-installation");
        mysql.Installation.PropertyChanged += (_, _) => mysql.Notify();
        Task<bool> MySqlElevationAsync() => AuthorizeInstallationAsync(InstallationServiceId.MySqlAccounts);
        mysql.AccountInstallation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.MySqlAccounts, false, mysql.RefreshAsync, MySqlElevationAsync, "mysql-accounts-installation");
        mysql.AccountInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmAccountAsync = (action, account) => Confirm("database.accounts_confirm",
            Text("database.account_action." + action) + " · " + account.Database +
            (account.User is null ? "" : " · " + account.User + "@" + account.Host));
        mysql.AuthorizeAccountsAsync = MySqlElevationAsync;
        _ = mysql.AccountInstallation.RestoreAsync();
        mysql.AdministratorInstallation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.MySqlAdminRecovery, false, mysql.RefreshAsync, () => AuthorizeInstallationAsync(InstallationServiceId.MySqlAdminRecovery), "mysql-admin-recovery-installation");
        mysql.AdministratorInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmAdministratorAsync = () => Confirm("database.mysql_admin_recovery_confirm");
        _ = mysql.AdministratorInstallation.RestoreAsync();
        mysql.RemoteCertificates = context.Services.GetRequiredService<RelaxKonOS.AppSDK.IServerCertificates>();
        mysql.RemoteInstallation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.MySqlRemoteAccess, false, mysql.RefreshAsync, () => AuthorizeInstallationAsync(InstallationServiceId.MySqlRemoteAccess), "mysql-remote-installation");
        mysql.RemoteInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmRemoteAsync = () => Confirm("database.mysql_remote_confirm");
        _ = mysql.RemoteInstallation.RestoreAsync();
        mysql.MaintenanceInstallation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.MySqlMaintenance, false, mysql.RefreshAsync, () => AuthorizeInstallationAsync(InstallationServiceId.MySqlMaintenance), "mysql-maintenance-installation");
        mysql.MaintenanceInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmMaintenanceAsync = command => Confirm("database.maintenance_confirm",
            command is null ? Text("database.recover") : Text("database.maintenance_action." + command.Action) + " · " + command.Version);
        _ = mysql.MaintenanceInstallation.RestoreAsync();
        model.MySql = mysql;
        _ = mysql.Installation.RestoreAsync();
        model.AdminRecoveryInstallation = new(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.PostgreSqlAdminRecovery, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlAdminRecovery), "admin-recovery-installation");
        model.ConfirmAdminRecoveryAsync = () => Confirm("database.admin_recovery_confirm");
        model.AdminRecoveryInstallation.PropertyChanged += (_, _) => { model.NotifyState(); model.AccountManager?.Notify(); };
        var remoteTask = new InstallationTaskViewModel(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.PostgreSqlRemoteAccess, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlRemoteAccess), "remote-installation");
        model.RemoteManager = new(context.Services.GetRequiredService<RelaxKonOS.AppSDK.IServerCertificates>(), remoteTask)
        { CanManage = model.CanManage, ConfirmAsync = () => Confirm("database.remote_confirm"), OtherBusy = () => model.MaintenanceBusy || model.Busy || model.CanResume || model.Installation.IsActive
            || model.AdminRecoveryInstallation.IsActive || model.CanResumeAdminRecovery || model.AccountManager?.Busy == true || model.AccountManager?.HasPendingRequest == true || model.AccountManager?.Installation.IsActive == true };
        remoteTask.PropertyChanged += (_, _) => { model.RemoteManager.Notify(); model.NotifyState(); model.AccountManager?.Notify(); };
        model.RemoteManager.PropertyChanged += (_, _) => model.NotifyState();
        var accountsTask = new InstallationTaskViewModel(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.PostgreSqlAccounts, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlAccounts), "accounts-installation");
        model.AccountManager = new(context.Services.GetRequiredService<IServerPostgreSql>(), accountsTask)
        { CanManage = model.CanManage, LifecycleBusy = () => model.MaintenanceBusy || model.Busy || model.Installation.IsActive || model.CanResume || model.AdminRecoveryInstallation.IsActive || model.CanResumeAdminRecovery || model.RemoteManager.Busy || model.RemoteManager.HasPending || remoteTask.IsActive };
        model.AccountManager.ConfirmAsync = command => Confirm("database.accounts_confirm",
            Text("database.account_action." + command.Action) + " · " + command.Database + " · " + (command.Account ?? "—") + " · "
                + (command.Permission is { } permission ? Text("database.permission." + permission) : ""));
        model.AccountManager.ConfirmRecoveryAsync = () => Confirm("database.confirm.Recover");
        model.AccountManager.AuthorizeAsync = () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlAccounts);
        accountsTask.PropertyChanged += (_, _) => { model.AccountManager.Notify(); model.NotifyState(); };
        model.AccountManager.PropertyChanged += (_, _) => model.NotifyState();
        var maintenanceTask=new InstallationTaskViewModel(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.PostgreSqlMaintenance,false,model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlMaintenance),"maintenance-installation");
        model.MaintenanceManager=new(context.Services.GetRequiredService<IServerPostgreSql>(),maintenanceTask)
        {CanManage=model.CanManage,ConfirmAsync=command=>Confirm(command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.DeleteInstance?"database.delete_instance_confirm":command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.MigrateMajor?"database.migration_confirm":command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.DeleteBackup?"database.backup_delete_confirm":"database.maintenance_confirm",command is null?Text("database.recover"):Text("database.maintenance_action."+command.Action)+" · "+(command.Target?.Version??command.BackupId?.ToString()??command.InstanceId.ToString())),
            OtherBusy=()=>model.Busy||model.CanResume||model.Installation.IsActive||model.AdminRecoveryInstallation.IsActive||model.CanResumeAdminRecovery||model.RemoteManager.Busy||model.RemoteManager.HasPending||remoteTask.IsActive||model.AccountManager.Busy||model.AccountManager.HasPendingRequest||accountsTask.IsActive};
        maintenanceTask.PropertyChanged+=(_,_)=>{model.MaintenanceManager.Notify();model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        model.MaintenanceManager.PropertyChanged+=(_,_)=>{model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        var backupPolicyTask=new InstallationTaskViewModel(DesktopInstallationText.Instance, context.Services.GetRequiredService<InstallationClient>(), settings,
            InstallationServiceId.PostgreSqlBackupAutomation,false,model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlBackupAutomation),"backup-policy-installation");
        model.MaintenanceManager.ScheduleInstallation=backupPolicyTask;
        model.MaintenanceManager.ConfirmScheduleAsync=policy=>Confirm("database.backup_schedule_confirm",
            Text(policy.Enabled?"database.backup_schedule_enabled":"database.backup_schedule_disabled")+" · "+policy.IntervalHours+" h · "+policy.KeepLast+" · "+Text(policy.AllowDowntime?"database.backup_schedule_downtime":"database.backup_schedule_stopped_only"));
        backupPolicyTask.PropertyChanged+=(_,_)=>{model.MaintenanceManager.Notify();model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        _=backupPolicyTask.RestoreAsync();
        _=maintenanceTask.RestoreAsync();
        view.DataContext = model;
        model.Installation.PropertyChanged += (_, _) => { model.NotifyState(); model.AccountManager.Notify(); };
        model.ChoosePortAsync = async () =>
        {
            var port = await context.ShowDialogAsync<int>(window, Text("database.initialize"),
                dialog => new DatabaseInitializeDialog(language, value => dialog.Close(value)), new Size(520, 340));
            return port is >= 1024 and <= 65535 ? port : null;
        };
        model.ConfirmAsync = (action, state) => Confirm("database.confirm." + action);
        model.ConfirmRecoveryAsync = _ => Confirm("database.confirm.Recover");
        async Task<bool> Confirm(string key, string? detail = null)
        {
            return await context.ShowDialogAsync<bool>(window, Text("application.relaxkonos.database.display_name"),
                dialog => new DatabaseConfirmDialog(language, key, detail, value => dialog.Close(value)), new Size(540, 280)) == true;
        }
        var observation = connection.ObserveInvalidation(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (context.WindowManager.Windows.Contains(window))
                model.Invalidate();
        }));
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed;
            observation.Dispose();
            dependency.Dispose();
            model.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = accountsTask.RestoreAsync();
        _ = remoteTask.RestoreAsync(); _ = model.RemoteManager.RefreshCertificatesAsync();
        _ = model.AdminRecoveryInstallation.RestoreAsync();
        _ = model.RefreshAsync(); _ = model.Installation.RestoreAsync(); _ = dependency.RefreshCommand.ExecuteAsync(null);
    }
}
