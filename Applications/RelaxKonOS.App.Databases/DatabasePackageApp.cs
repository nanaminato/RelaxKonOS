using RelaxKonOS.App.Databases.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.App.Databases;

public sealed class DatabasePackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.database"), "Database engines", "0.2.0", "▤", "Database engine management",
        [AppPermissions.ServerComponentsRead, AppPermissions.ServerDatabasesRead, AppPermissions.ServerDatabasesManage, AppPermissions.ServerCertificatesRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Database]),
        IconPath: "avares://RelaxKonOS.App.Databases/Assets/database.png", InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        var connection = context.ServerConnection;
        if (!connection.IsCurrent || !connection.Capabilities.Contains(ServerCapabilities.Database, StringComparer.Ordinal)) return;
        var permissions = context.Permissions;
        if (permissions.GetStatus(AppPermissions.ServerDatabasesRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerDatabasesRead, cancellationToken);
        if (permissions.IsGranted(AppPermissions.ServerDatabasesRead) && connection.IsController)
        {
            if (permissions.GetStatus(AppPermissions.ServerDatabasesManage) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(AppPermissions.ServerDatabasesManage, cancellationToken);
            if (permissions.IsGranted(AppPermissions.ServerDatabasesManage)
                && permissions.GetStatus(AppPermissions.ServerCertificatesRead) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(AppPermissions.ServerCertificatesRead, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsCurrent || !connection.Capabilities.Contains(ServerCapabilities.Database, StringComparer.Ordinal)) return;

        var language = context.SystemLanguage;
        string Text(string key) => DatabaseLocalization.Resolve(language, key, $"[missing: {key}]");
        var installation = context.DatabaseInstallation;
        var settings = context.SettingsStore;
        var taskText = DatabaseInstallationText.Create(language);
        Task<bool> AuthorizeInstallationAsync(InstallationServiceId service) => installation.RequestAuthorizationAsync(service);
        var model = new DatabaseViewModel(context.PostgreSql)
        {
            CanManage = connection.IsController && permissions.IsGranted(AppPermissions.ServerDatabasesRead)
                && permissions.IsGranted(AppPermissions.ServerDatabasesManage)
        };
        var view = new DatabaseWorkspace();
        DatabaseWorkspaceLanguage.Bind(view, language);
        var window = context.Windows.ShowWindow(Text("application.relaxkonos.database.display_name"), view, new Rect(90, 60, 980, 700), Manifest.IconGlyph);
        model.DependencySettings = context.ComponentDependencies?.CreateSettingsView(window);
        model.Installation = new(taskText, installation, settings,
            InstallationServiceId.PostgreSql, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSql));
        var mysql = new MySqlViewModel(context.MySql, connection)
        {
            CanManage = model.CanManage,
            ConfirmAsync = () => Confirm("database.mysql_confirm")
        };
        mysql.Installation = new(taskText, installation, settings,
            InstallationServiceId.MySql, false, mysql.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.MySql),
            "mysql-installation");
        mysql.Installation.PropertyChanged += (_, _) => mysql.Notify();
        Task<bool> MySqlElevationAsync() => AuthorizeInstallationAsync(InstallationServiceId.MySqlAccounts);
        mysql.AccountInstallation = new(taskText, installation, settings,
            InstallationServiceId.MySqlAccounts, false, mysql.RefreshAsync, MySqlElevationAsync, "mysql-accounts-installation");
        mysql.AccountInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmAccountAsync = (action, account) => Confirm("database.accounts_confirm",
            Text("database.account_action." + action) + " · " + account.Database +
            (account.User is null ? "" : " · " + account.User + "@" + account.Host));
        mysql.AuthorizeAccountsAsync = MySqlElevationAsync;
        _ = mysql.AccountInstallation.RestoreAsync();
        mysql.AdministratorInstallation = new(taskText, installation, settings,
            InstallationServiceId.MySqlAdminRecovery, false, mysql.RefreshAsync, () => AuthorizeInstallationAsync(InstallationServiceId.MySqlAdminRecovery), "mysql-admin-recovery-installation");
        mysql.AdministratorInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmAdministratorAsync = () => Confirm("database.mysql_admin_recovery_confirm");
        _ = mysql.AdministratorInstallation.RestoreAsync();
        mysql.RemoteCertificates = context.Certificates;
        mysql.RemoteInstallation = new(taskText, installation, settings,
            InstallationServiceId.MySqlRemoteAccess, false, mysql.RefreshAsync, () => AuthorizeInstallationAsync(InstallationServiceId.MySqlRemoteAccess), "mysql-remote-installation");
        mysql.RemoteInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmRemoteAsync = () => Confirm("database.mysql_remote_confirm");
        _ = mysql.RemoteInstallation.RestoreAsync();
        mysql.MaintenanceInstallation = new(taskText, installation, settings,
            InstallationServiceId.MySqlMaintenance, false, mysql.RefreshAsync, () => AuthorizeInstallationAsync(InstallationServiceId.MySqlMaintenance), "mysql-maintenance-installation");
        mysql.MaintenanceInstallation.PropertyChanged += (_, _) => mysql.Notify();
        mysql.ConfirmMaintenanceAsync = command => Confirm("database.maintenance_confirm",
            command is null ? Text("database.recover") : Text("database.maintenance_action." + command.Action) + " · " + command.Version);
        _ = mysql.MaintenanceInstallation.RestoreAsync();
        model.MySql = mysql;
        _ = mysql.Installation.RestoreAsync();
        model.AdminRecoveryInstallation = new(taskText, installation, settings,
            InstallationServiceId.PostgreSqlAdminRecovery, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlAdminRecovery), "admin-recovery-installation");
        model.ConfirmAdminRecoveryAsync = () => Confirm("database.admin_recovery_confirm");
        model.AdminRecoveryInstallation.PropertyChanged += (_, _) => { model.NotifyState(); model.AccountManager?.Notify(); };
        var remoteTask = new InstallationTaskViewModel(taskText, installation, settings,
            InstallationServiceId.PostgreSqlRemoteAccess, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlRemoteAccess), "remote-installation");
        model.RemoteManager = new(context.Certificates, remoteTask)
        { CanManage = model.CanManage, ConfirmAsync = () => Confirm("database.remote_confirm"), OtherBusy = () => model.MaintenanceBusy || model.Busy || model.CanResume || model.Installation.IsActive
            || model.AdminRecoveryInstallation.IsActive || model.CanResumeAdminRecovery || model.AccountManager?.Busy == true || model.AccountManager?.HasPendingRequest == true || model.AccountManager?.Installation.IsActive == true };
        remoteTask.PropertyChanged += (_, _) => { model.RemoteManager.Notify(); model.NotifyState(); model.AccountManager?.Notify(); };
        model.RemoteManager.PropertyChanged += (_, _) => model.NotifyState();
        var accountsTask = new InstallationTaskViewModel(taskText, installation, settings,
            InstallationServiceId.PostgreSqlAccounts, false, model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlAccounts), "accounts-installation");
        model.AccountManager = new(context.PostgreSql, accountsTask)
        { CanManage = model.CanManage, LifecycleBusy = () => model.MaintenanceBusy || model.Busy || model.Installation.IsActive || model.CanResume || model.AdminRecoveryInstallation.IsActive || model.CanResumeAdminRecovery || model.RemoteManager.Busy || model.RemoteManager.HasPending || remoteTask.IsActive };
        model.AccountManager.ConfirmAsync = command => Confirm("database.accounts_confirm",
            Text("database.account_action." + command.Action) + " · " + command.Database + " · " + (command.Account ?? "—") + " · "
                + (command.Permission is { } permission ? Text("database.permission." + permission) : ""));
        model.AccountManager.ConfirmRecoveryAsync = () => Confirm("database.confirm.Recover");
        model.AccountManager.AuthorizeAsync = () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlAccounts);
        accountsTask.PropertyChanged += (_, _) => { model.AccountManager.Notify(); model.NotifyState(); };
        model.AccountManager.PropertyChanged += (_, _) => model.NotifyState();
        var maintenanceTask=new InstallationTaskViewModel(taskText, installation, settings,
            InstallationServiceId.PostgreSqlMaintenance,false,model.RefreshAsync,
            () => AuthorizeInstallationAsync(InstallationServiceId.PostgreSqlMaintenance),"maintenance-installation");
        model.MaintenanceManager=new(context.PostgreSql,maintenanceTask)
        {CanManage=model.CanManage,ConfirmAsync=command=>Confirm(command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.DeleteInstance?"database.delete_instance_confirm":command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.MigrateMajor?"database.migration_confirm":command?.Action==RelaxKonOS.Protocol.Databases.PostgreSqlMaintenanceAction.DeleteBackup?"database.backup_delete_confirm":"database.maintenance_confirm",command is null?Text("database.recover"):Text("database.maintenance_action."+command.Action)+" · "+(command.Target?.Version??command.BackupId?.ToString()??command.InstanceId.ToString())),
            OtherBusy=()=>model.Busy||model.CanResume||model.Installation.IsActive||model.AdminRecoveryInstallation.IsActive||model.CanResumeAdminRecovery||model.RemoteManager.Busy||model.RemoteManager.HasPending||remoteTask.IsActive||model.AccountManager.Busy||model.AccountManager.HasPendingRequest||accountsTask.IsActive};
        maintenanceTask.PropertyChanged+=(_,_)=>{model.MaintenanceManager.Notify();model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        model.MaintenanceManager.PropertyChanged+=(_,_)=>{model.NotifyState();model.AccountManager.Notify();model.RemoteManager.Notify();};
        var backupPolicyTask=new InstallationTaskViewModel(taskText, installation, settings,
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
            var port = await window.ShowDialogAsync<int>(Text("database.initialize"),
                dialog => new DatabaseInitializeDialog(language, value => dialog.Close(value)), new Size(520, 340));
            return port is >= 1024 and <= 65535 ? port : null;
        };
        model.ConfirmAsync = (action, state) => Confirm("database.confirm." + action);
        model.ConfirmRecoveryAsync = _ => Confirm("database.confirm.Recover");
        async Task<bool> Confirm(string key, string? detail = null)
        {
            return await window.ShowDialogAsync<bool>(Text("application.relaxkonos.database.display_name"),
                dialog => new DatabaseConfirmDialog(language, key, detail, value => dialog.Close(value)), new Size(540, 280)) == true;
        }
        var observation = connection.ObserveInvalidation(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!window.Closed.IsCancellationRequested)
                model.Invalidate();
        }));
        window.Closed.Register(() =>
        {
            observation.Dispose();
            model.Dispose();
        });
        _ = accountsTask.RestoreAsync();
        _ = remoteTask.RestoreAsync(); if (permissions.IsGranted(AppPermissions.ServerCertificatesRead)) _ = model.RemoteManager.RefreshCertificatesAsync();
        _ = model.AdminRecoveryInstallation.RestoreAsync();
        _ = model.RefreshAsync(); _ = model.Installation.RestoreAsync();
    }
}
