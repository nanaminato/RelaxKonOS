using InstallationApiException = RelaxKonOS.AppSDK.InstallationApiException;
using IServerPostgreSql = RelaxKonOS.AppSDK.IServerPostgreSql;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.App.Databases;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Databases;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Databases;
using RelaxKonOS.Client.Apps.Databases.Views;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

internal static class DatabaseWorkspaceChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        void Check(bool ok,string label) { if(!ok)throw new Exception(label);Console.WriteLine("PASS Database "+label); }
        var fake=new Client();
        using var model=new DatabaseViewModel(fake) { CanManage=true,
            Installation=new(null!,null!,InstallationServiceId.PostgreSql,"test",()=>Task.CompletedTask,()=>Task.FromResult(false)) };
        model.RefreshAsync().GetAwaiter().GetResult();
        Check(model.CanInitialize && !model.CanStart && !model.CanStop,"installed engine exposes only initialization");
        var prompted=false;model.ChoosePortAsync=()=>{prompted=true;return Task.FromResult<int?>(null);};
        model.InitializeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(prompted && model.Installation.Operation is null,"cancelled initialization sends no request");
        model.Snapshot=fake.State with { InstanceId=Guid.NewGuid(),Running=true,ConnectionVerified=true };
        Check(!model.CanInitialize && model.CanStop && model.CanVerify && !model.CanStart,"running instance exposes stop and verify");
        model.Snapshot=model.Snapshot with { PendingOperationId=Guid.NewGuid() };
        Check(!model.CanStop && !model.CanVerify && model.CanRecover,"pending transaction blocks ordinary actions");
        model.Snapshot=model.Snapshot with { PendingOperationId=null,Running=false,ConnectionVerified=false };
        Check(model.CanStart && !model.CanStop,"stopped instance exposes start");
        model.CanManage=false;model.NotifyState();Check(!model.CanStart && !model.CanRecover,"viewer cannot modify state");model.CanManage=true;
        var output=Path.Combine(AppContext.BaseDirectory,"preview-qa","database");Directory.CreateDirectory(output);
        foreach(var language in new[]{"zh-CN","en-US","ja-JP"})
        {
            settings.Language=language;Dispatcher.UIThread.RunJobs();
            const string confirmation = "database.mysql_remote_confirm";
            var appText = DatabaseLocalization.Resolve(localization, confirmation, "[missing]");
            Check(appText != "[missing]" && appText == localization.Get(confirmation, "[missing]"),
                "application confirmation text matches the desktop resource in " + language);
            foreach(var size in new[]{new PixelSize(980,700),new PixelSize(760,580)})
            {
                var view=new DatabaseWorkspace { DataContext=model };
                var window=new Window { Width=size.Width,Height=size.Height,Content=view };
                window.Show();Dispatcher.UIThread.RunJobs();window.UpdateLayout();
                Check(view.GetVisualDescendants().OfType<Button>().Any(x=>x.IsVisible && x.Command==model.StartCommand),"start control renders in "+language);
                Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(x=>x.Text?.StartsWith("database.")==true),"localized overview in "+language);
                using(var bitmap=new RenderTargetBitmap(size)){bitmap.Render(window);bitmap.Save(Path.Combine(output,language+"-"+size.Width+".png"));}
                window.Close();Dispatcher.UIThread.RunJobs();
            }
        }
        using var handler = new LostResponseHandler();
        using var http = new HttpClient(handler);
        model.Installation.Dispose();
        model.Installation = new(new InstallationClient(http, DispatchProxy.Create<IAuthSession, EntrySession>()), null!,
            InstallationServiceId.PostgreSql, "test", () => Task.CompletedTask, () => Task.FromResult(false));
        model.ConfirmAsync = (_, _) => Task.FromResult(true);
        model.StartCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(model.CanResume && !model.CanStart && !model.CanStop, "lost response preserves original request and blocks new work");
        model.Snapshot = model.Snapshot! with { Revision = 99 };
        model.ResumeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(handler.Bodies.Count == 2 && handler.Bodies[0] == handler.Bodies[1] && handler.Keys[0] == handler.Keys[1],
            "retry preserves exact command and idempotency key despite refreshed state");
        Check(!model.CanResume, "accepted original request clears retry state");
        using var adminHandler = new LostResponseHandler(InstallationServiceId.PostgreSqlAdminRecovery);
        using var adminHttp = new HttpClient(adminHandler);
        using var adminModel = new DatabaseViewModel(fake) { CanManage = true, Snapshot = model.Snapshot! with { Managed = false, ConnectionVerified = false, ProblemCode = "database.native_failed" },
            Installation = new(null!, null!, InstallationServiceId.PostgreSql, "test", () => Task.CompletedTask, () => Task.FromResult(false)),
            AdminRecoveryInstallation = new(new InstallationClient(adminHttp, DispatchProxy.Create<IAuthSession, EntrySession>()), null!,
                InstallationServiceId.PostgreSqlAdminRecovery, "test", () => Task.CompletedTask, () => Task.FromResult(false)) };
        Check(adminModel.CanAdminRecovery, "lost peer identity still exposes dedicated recovery");
        adminModel.ConfirmAdminRecoveryAsync = () => Task.FromResult(false);
        adminModel.AdminRecoveryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(adminHandler.Bodies.Count == 0, "cancelled administrator recovery has no effect");
        adminModel.ConfirmAdminRecoveryAsync = () => Task.FromResult(true);
        adminModel.AdminRecoveryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(adminModel.CanResumeAdminRecovery && !adminModel.CanAdminRecovery && !adminModel.CanStart, "lost recovery response blocks new operations");
        adminModel.Snapshot = adminModel.Snapshot! with { Revision = 1000 };
        adminModel.ResumeAdminRecoveryCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(adminHandler.Bodies.Count == 2 && adminHandler.Bodies[0] == adminHandler.Bodies[1] && adminHandler.Keys[0] == adminHandler.Keys[1],
            "administrator recovery retry preserves original intent and key");
        adminModel.Snapshot = adminModel.Snapshot! with { PendingOperationId = Guid.NewGuid(), PendingAdminRecovery = new(adminModel.Snapshot.InstanceId!.Value, 2) };
        Check(adminModel.CanAdminRecovery && !adminModel.CanRecover, "administrator pending operation uses dedicated recovery entry");
        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = language; Dispatcher.UIThread.RunJobs();
            var maintenance = new DatabaseMaintenance { DataContext = adminModel };
            var window = new Window { Width = 760, Height = 680, Content = maintenance };
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Check(maintenance.GetVisualDescendants().OfType<Button>().Any(x => x.Command == adminModel.AdminRecoveryCommand), "administrator recovery control renders in " + language);
            Check(!maintenance.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.StartsWith("database.") == true), "administrator recovery copy localized in " + language);
            using (var bitmap = new RenderTargetBitmap(new PixelSize(760, 680))) { bitmap.Render(window); bitmap.Save(Path.Combine(output, "admin-" + language + ".png")); }
            window.Close(); Dispatcher.UIThread.RunJobs();
        }
        adminModel.CanManage = false;
        Check(!adminModel.CanAdminRecovery && !adminModel.CanResumeAdminRecovery, "viewer cannot recover administrator");
        adminModel.Invalidate();
        Check(!adminModel.CanAdminRecovery, "invalidated session cannot recover administrator");
        using var remoteHandler=new LostResponseHandler(InstallationServiceId.PostgreSqlRemoteAccess);
        using var remoteHttp=new HttpClient(remoteHandler);
        using var remoteModel=new PostgreSqlRemoteViewModel(null!,new(new InstallationClient(remoteHttp,DispatchProxy.Create<IAuthSession,EntrySession>()),null!,
            InstallationServiceId.PostgreSqlRemoteAccess,"test",()=>Task.CompletedTask,()=>Task.FromResult(false)))
        { CanManage=true,Snapshot=model.Snapshot! with { Running=true,ConnectionVerified=true }, ListenAddress="192.168.10.5",ServerName="db.example.test",Sources="0.0.0.0/0",
            SelectedAccount=new("appdb","appuser",11,PostgreSqlPermission.ReadWrite,Guid.NewGuid(),1,false),
            SelectedCertificate=System.Text.Json.JsonSerializer.Deserialize<RelaxKonOS.Protocol.Certificates.CertificateDto>("{\"id\":\""+Guid.NewGuid()+"\",\"primaryDomain\":\"db.example.test\",\"fingerprintSha256\":\""+new string('A',64)+"\"}") };
        remoteModel.ConfirmAsync=()=>Task.FromResult(true);
        remoteModel.EnableCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(remoteHandler.Bodies.Count==0,"remote access rejects unrestricted source before submission");
        remoteModel.Sources="192.168.10.0/24";remoteModel.ConfirmAsync=()=>Task.FromResult(false);
        remoteModel.EnableCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(remoteHandler.Bodies.Count==0,"cancelled remote access does not submit");
        remoteModel.ConfirmAsync=()=>Task.FromResult(true);
        remoteModel.EnableCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(remoteModel.CanResume && !remoteModel.CanEnable,"remote response loss preserves intent and blocks new work");
        remoteModel.Sources="192.168.20.0/24";
        remoteModel.ResumeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(remoteHandler.Bodies.Count==2 && remoteHandler.Bodies[0]==remoteHandler.Bodies[1] && remoteHandler.Keys[0]==remoteHandler.Keys[1],"remote retry keeps original source policy and key");
        foreach(var language in new[]{"zh-CN","en-US","ja-JP"})
        {
            settings.Language=language;Dispatcher.UIThread.RunJobs();
            var view=new DatabaseRemoteAccess { DataContext=remoteModel };var window=new Window { Width=760,Height=680,Content=view };
            window.Show();Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(x=>x.Text?.StartsWith("database.")==true),"remote access localized in "+language);
            using(var bitmap=new RenderTargetBitmap(new PixelSize(760,680))){bitmap.Render(window);bitmap.Save(Path.Combine(output,"remote-"+language+".png"));}
            window.Close();Dispatcher.UIThread.RunJobs();
        }
        remoteModel.CanManage=false;Check(!remoteModel.CanEnable && !remoteModel.CanClose,"viewer cannot change remote access");
        remoteModel.Invalidate();Check(!remoteModel.CanEnable,"invalidated session cannot expose database");
        using var maintenanceHandler=new LostResponseHandler(InstallationServiceId.PostgreSqlMaintenance);
        using var maintenanceHttp=new HttpClient(maintenanceHandler);
        using var maintenanceModel=new PostgreSqlMaintenanceViewModel(new Client(),new(new InstallationClient(maintenanceHttp,DispatchProxy.Create<IAuthSession,EntrySession>()),null!,
            InstallationServiceId.PostgreSqlMaintenance,"test",()=>Task.CompletedTask,()=>Task.FromResult(false)))
            {CanManage=true,Snapshot=model.Snapshot! with {PendingOperationId=null,ProblemCode=null},ConfirmAsync=_=>Task.FromResult(false)};
        Check(maintenanceModel.CanBackup&&!maintenanceModel.CanDelete&&!maintenanceModel.CanUpdate,"maintenance requires a frozen target and typed deletion confirmation");
        maintenanceModel.BackupCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(maintenanceHandler.Bodies.Count==0,"cancelled backup makes no request");
        maintenanceModel.DeleteConfirmation=maintenanceModel.Snapshot!.InstanceId!.Value.ToString("N");
        Check(maintenanceModel.CanDelete,"exact instance confirmation enables destructive removal");
        maintenanceModel.ConfirmAsync=_=>Task.FromResult(true);
        maintenanceModel.BackupCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(maintenanceModel.CanResume&&!maintenanceModel.CanDelete,"lost maintenance response blocks new destructive work");
        maintenanceModel.ResumeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(maintenanceHandler.Bodies.Count==2&&maintenanceHandler.Bodies[0]==maintenanceHandler.Bodies[1]&&maintenanceHandler.Keys[0]==maintenanceHandler.Keys[1],"maintenance retry retains frozen request and idempotency key");
        using var sourceFailureHttp=new HttpClient(new SourceFailureHandler());
        var sourceClient=new PostgreSqlClient(sourceFailureHttp,DispatchProxy.Create<IAuthSession,EntrySession>());
        var sourceProblem=false;
        try{sourceClient.MigrationPlanAsync(default).GetAwaiter().GetResult();}catch(InstallationApiException error){sourceProblem=error.ProblemCode=="database.migration_ca_required";}
        Check(sourceProblem,"migration prerequisite code survives HTTP problem response");
        using var migrationHandler=new LostResponseHandler(InstallationServiceId.PostgreSqlMaintenance);
        using var migrationHttp=new HttpClient(migrationHandler);
        var migrationClient=new Client {State=maintenanceModel.Snapshot! with {Version="16.15-0ubuntu0.24.04.1",Candidate=new("postgresql-18","18.7-1.pgdg24.04+2","amd64","fixture","https://apt.postgresql.org/pub/repos/apt","noble-pgdg","main","https://apt.postgresql.org/pub/repos/apt/pool/fixture.deb",new string('a',64),1,[])}};
        using var migrationModel=new PostgreSqlMaintenanceViewModel(migrationClient,new(new InstallationClient(migrationHttp,DispatchProxy.Create<IAuthSession,EntrySession>()),null!,InstallationServiceId.PostgreSqlMaintenance,"test",()=>Task.CompletedTask,()=>Task.FromResult(false)))
            {CanManage=true,Snapshot=migrationClient.State,ConfirmAsync=_=>Task.FromResult(false)};
        migrationModel.MigrationPlanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(!migrationModel.CanMigrate&&!migrationModel.CanUpdate,"major discovery neither grants source trust nor becomes patch update");
        migrationModel.MigrationSourceAccepted=true;Check(migrationModel.CanMigrate,"explicit source selection enables reviewed migration");
        migrationModel.MigrateCommand.ExecuteAsync(null).GetAwaiter().GetResult();Check(migrationHandler.Bodies.Count==0,"cancelled migration does not submit");
        migrationModel.ConfirmAsync=_=>Task.FromResult(true);migrationModel.MigrateCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(migrationModel.CanResume&&!migrationModel.CanMigrate,"lost migration response preserves pending intent");
        migrationModel.MigrationSourceAccepted=false;migrationModel.ResumeCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(migrationHandler.Bodies.Count==2&&migrationHandler.Bodies[0]==migrationHandler.Bodies[1]&&migrationHandler.Keys[0]==migrationHandler.Keys[1],"migration retries exact target and confirmed signing identity");
        migrationModel.Invalidate();Check(!migrationModel.CanResume&&!migrationModel.CanMigrate,"invalidated session cannot resume migration");
        using var policyHandler=new LostResponseHandler(InstallationServiceId.PostgreSqlBackupAutomation);
        using var policyHttp=new HttpClient(policyHandler);
        maintenanceModel.ScheduleInstallation=new(new InstallationClient(policyHttp,DispatchProxy.Create<IAuthSession,EntrySession>()),null!,
            InstallationServiceId.PostgreSqlBackupAutomation,"test",()=>Task.CompletedTask,()=>Task.FromResult(false));
        maintenanceModel.ConfirmScheduleAsync=_=>Task.FromResult(false);
        maintenanceModel.ScheduleEnabled=true;maintenanceModel.IntervalHours=24;maintenanceModel.KeepLast=2;
        Check(maintenanceModel.CanConfigureSchedule&&!maintenanceModel.AllowDowntime,"policy defaults to no recurring downtime");
        maintenanceModel.SaveScheduleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(policyHandler.Bodies.Count==0,"cancelled ongoing grant is not submitted");
        maintenanceModel.ConfirmScheduleAsync=_=>Task.FromResult(true);maintenanceModel.KeepLast=0;
        maintenanceModel.SaveScheduleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(policyHandler.Bodies.Count==0,"invalid retention rejected before submission");
        maintenanceModel.KeepLast=2;maintenanceModel.AllowDowntime=true;
        maintenanceModel.SaveScheduleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(maintenanceModel.CanResumeSchedule&&!maintenanceModel.CanBackup&&!maintenanceModel.CanConfigureSchedule,"lost policy response blocks conflicting work");
        maintenanceModel.KeepLast=6;maintenanceModel.AllowDowntime=false;
        maintenanceModel.ResumeScheduleCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(policyHandler.Bodies.Count==2&&policyHandler.Bodies[0]==policyHandler.Bodies[1]&&policyHandler.Keys[0]==policyHandler.Keys[1],"policy retry retains exact downtime authorization and retention");
        var originalMaintenanceSnapshot=maintenanceModel.Snapshot!;
        maintenanceModel.Snapshot=originalMaintenanceSnapshot with {PendingOperationId=Guid.NewGuid(),PendingMaintenance=new(PostgreSqlMaintenanceAction.ScheduledBackup,originalMaintenanceSnapshot.InstanceId!.Value,0,ScheduleId:Guid.NewGuid(),DueAt:DateTimeOffset.UtcNow)};
        Check(maintenanceModel.CanRecoverSchedule&&!maintenanceModel.CanRecover,"scheduled pending work uses its dedicated recovery authority");
        maintenanceModel.Snapshot=originalMaintenanceSnapshot;
        foreach(var language in new[]{"zh-CN","en-US","ja-JP"})
        {
            settings.Language=language;Dispatcher.UIThread.RunJobs();
            var hostModel=new DatabaseViewModel(new Client()){MaintenanceManager=maintenanceModel};
            var view=new DatabaseMaintenance{DataContext=hostModel};var window=new Window{Width=980,Height=760,Content=view};
            window.Show();Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            Check(view.GetVisualDescendants().OfType<Button>().Any(x=>x.Command==maintenanceModel.BackupCommand),"maintenance controls render in "+language);
            Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(x=>x.Text?.StartsWith("database.")==true),"maintenance copy localized in "+language);
            using(var bitmap=new RenderTargetBitmap(new PixelSize(980,760))){bitmap.Render(window);bitmap.Save(Path.Combine(output,"maintenance-"+language+".png"));}
            window.Close();Dispatcher.UIThread.RunJobs();
        }
        maintenanceModel.CanManage=false;Check(!maintenanceModel.CanBackup&&!maintenanceModel.CanRecover&&!maintenanceModel.CanConfigureSchedule&&!maintenanceModel.CanRecoverSchedule,"viewer cannot maintain instance or grant automatic backups");
        maintenanceModel.Invalidate();Check(!maintenanceModel.CanResume,"invalidated maintenance session cannot replay");
        var peer = Guid.NewGuid();
        var primary = Guid.NewGuid();
        var selectionClient = new Client();
        var instanceList = new PostgreSqlInstanceSummary[] { new(primary, 55432), new(peer, 55433) };
        selectionClient.State = selectionClient.State with { InstanceId = peer, Port = 55433, Instances = instanceList };
        using var selectionModel = new DatabaseViewModel(selectionClient)
        {
            Snapshot = selectionClient.State with { InstanceId = primary, Port = 55432 },
            SelectedInstance = instanceList[1]
        };
        selectionModel.SwitchInstanceCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(selectionClient.LastInstance == peer && selectionModel.Snapshot?.InstanceId == peer && selectionModel.Port == "55433",
            "instance selection requests and displays the selected cluster");
        using var peerMaintenance = new PostgreSqlMaintenanceViewModel(selectionClient, new(
            new InstallationClient(maintenanceHttp, DispatchProxy.Create<IAuthSession,EntrySession>()), null!,
            InstallationServiceId.PostgreSqlMaintenance, "test", () => Task.CompletedTask, () => Task.FromResult(false)))
        {
            CanManage = true, Snapshot = selectionClient.State, DeleteConfirmation = primary.ToString("N")
        };
        Check(!peerMaintenance.CanDeleteInstance, "primary UUID cannot confirm secondary deletion");
        peerMaintenance.DeleteConfirmation = peer.ToString("N");
        Check(peerMaintenance.CanDeleteInstance && !peerMaintenance.CanDelete, "secondary confirmation enables independent deletion only");
        peerMaintenance.Snapshot = selectionClient.State with { InstanceId = primary, Instances = [instanceList[0]] };
        peerMaintenance.DeleteConfirmation = primary.ToString("N");
        Check(!peerMaintenance.CanDeleteInstance && peerMaintenance.CanDelete && peerMaintenance.CanKeepData,
            "single primary restores shared package maintenance controls");
        selectionClient.State = selectionClient.State with { InstanceId = primary, Port = 55432, Instances = [instanceList[0]] };
        selectionClient.MissingInstance = peer;
        selectionModel.RefreshAsync().GetAwaiter().GetResult();
        Check(selectionClient.LastInstance is null && selectionModel.Snapshot?.InstanceId == primary
            && selectionModel.SelectedInstance is null, "refresh falls back to primary after secondary deletion");
        using var mysqlHttp = new HttpClient();
        var mysqlSession = DispatchProxy.Create<IAuthSession, TailscaleScopeAuthFixture>();
        var memory = new UsageMemoryStore(Path.Combine(AppContext.BaseDirectory, "preview-qa", "database-workspace-memory.json"));
        var mysqlConnection = new ExternalServerConnection(memory.Capture(mysqlSession));
        using var mysql = new MySqlViewModel(new MySqlClient(mysqlHttp, mysqlSession), mysqlConnection)
        {
            CanManage = true,
            State = new(true, 1, false, null, false, false, null, null, null),
            Installation = new(null!, null!, InstallationServiceId.MySql, "test",
                () => Task.CompletedTask, () => Task.FromResult(false))
        };
        mysql.AccountInstallation = new(null!, null!, InstallationServiceId.MySqlAccounts, "test",
            () => Task.CompletedTask, () => Task.FromResult(false));
        mysql.RemoteInstallation = new(null!, null!, InstallationServiceId.MySqlRemoteAccess, "test",
            () => Task.CompletedTask, () => Task.FromResult(false));
        mysql.MaintenanceInstallation = new(null!, null!, InstallationServiceId.MySqlMaintenance, "test",
            () => Task.CompletedTask, () => Task.FromResult(false));
        Check(!mysql.CanManageAccounts, "MySQL account controls require verified running instance");
        Check(!mysql.CanOpenRemote, "MySQL remote opening requires a verified instance");
        Check(mysql.CanInstall && !mysql.CanInitialize, "MySQL fresh host exposes installation");
        mysql.State = mysql.State with { Installed = true };
        Check(mysql.CanInitialize && !mysql.CanInstall, "MySQL installed host exposes initialization");
        mysql.State = mysql.State with { InstanceId = Guid.NewGuid(), Running = true, Verified = true };
        var mysqlView = new MySqlOverview { DataContext = mysql };
        var mysqlWindow = new Window { Width = 760, Height = 460, Content = mysqlView };
        mysqlWindow.Show();
        Dispatcher.UIThread.RunJobs();
        Check(mysqlView.GetVisualDescendants().OfType<Button>().Any(button => button.Command == mysql.StopCommand && button.IsEnabled),
            "MySQL page renders running lifecycle controls");
        Check(mysql.CanManageAccounts && mysqlView.GetVisualDescendants().OfType<TextBox>().Any(box => box.PasswordChar != '\0'),
            "MySQL running account form masks the password");
        Check(mysqlView.GetVisualDescendants().OfType<Button>().Any(button => button.Command == mysql.OpenRemoteCommand && button.IsEnabled),
            "MySQL page exposes remote opening for a verified instance");
        mysql.AdministratorInstallation = new(null!, null!, InstallationServiceId.MySqlAdminRecovery, "test",
            () => Task.CompletedTask, () => Task.FromResult(false));
        mysql.State = mysql.State with { RemoteAccess = new(mysql.State.InstanceId!.Value, "10.77.0.1", "10.77.0.2",
            "app", "reader", MySqlGrant.ReadOnly, "mysql.example.test", Guid.NewGuid(), new string('A', 64)), Verified = false };
        Check(mysql.CanCloseRemote && !mysql.CanOpenRemote && !mysql.CanManageAccounts && !mysql.CanRecoverAdministrator && !mysql.CanStop,
            "MySQL keeps close available on an unverified remote policy and gates other mutations");
        mysql.State = mysql.State with { RemoteAccess = null, Verified = true };
        var obsoleteBackup = new MySqlBackup(Guid.NewGuid(), "8.4.11", DateTimeOffset.UtcNow);
        var currentBackup = new MySqlBackup(Guid.NewGuid(), "8.4.13", DateTimeOffset.UtcNow);
        mysql.State = mysql.State with { Version = "8.4.13", Backups = [obsoleteBackup, currentBackup] };
        mysql.SelectedBackup = obsoleteBackup;
        Check(mysql.CanBackup && !mysql.CanUpdate && !mysql.CanRestore, "MySQL current patch offers a backup and rejects old-version restore selection");
        mysql.SelectedBackup = currentBackup;
        Check(mysql.CanRestore && !mysql.CanDeleteData, "MySQL eligible backup and independent deletion confirmation gate");
        mysql.DeleteConfirmation = mysql.State.InstanceId!.Value.ToString("N");
        Check(mysql.CanDeleteData, "MySQL delete data requires the exact instance identity");
        mysql.State = mysql.State with { Version = "8.4.11" };
        Check(mysql.CanUpdate, "MySQL desktop exposes only the qualified cross-patch direction");
        mysql.State = mysql.State with { Installed = false, Running = false, Verified = false };
        Check(mysql.CanReinstall && !mysql.CanInstall && !mysql.CanStart, "retained MySQL data offers exact-version reinstall only");
        mysql.State = mysql.State with { Installed = true, Running = true, Verified = true };
        Check(!mysql.CanRecoverAdministrator, "verified MySQL administrator needs no recovery");
        mysql.State = mysql.State with { Verified = false, ProblemCode = "database.connection_unverified" };
        Check(mysql.CanRecoverAdministrator && !mysql.CanManageAccounts, "lost MySQL administrator has dedicated recovery entry");
        mysqlWindow.Close();
        mysql.State = mysql.State with { PendingOperationId = Guid.NewGuid(), ProblemCode = "database.recovery_required" };
        Check(mysql.CanRecover && !mysql.CanStop, "MySQL pending operation exposes only recovery");
        using var mysqlConnectionObserver = mysqlConnection.ObserveInvalidation(mysql.Invalidate);
        var mysqlAuth = (TailscaleScopeAuthFixture)(object)mysqlSession;
        mysqlAuth.ServiceId = "https://replacement.invalid";
        mysqlAuth.NotifyStateChanged();
        Check(!mysql.CanRecover, "MySQL invalidated session cannot mutate");
        model.Invalidate();Check(!model.CanStart && !model.CanRecover,"session invalidation disables stale window");
        Console.WriteLine("Database previews: "+output);
    }
    private sealed class SourceFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {Content=JsonContent.Create(new {code="database.migration_ca_required"})});
    }
    private sealed class LostResponseHandler(InstallationServiceId service = InstallationServiceId.PostgreSql) : HttpMessageHandler
    {
        public List<string> Bodies = []; public List<string> Keys = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(ct)); Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                if (Bodies.Count == 1) throw new HttpRequestException("fixture lost response");
            }
            return new(HttpStatusCode.Accepted) { Content = JsonContent.Create(new InstallationOperationDto(Guid.NewGuid(),
                service, InstallationOperationKind.Configure, InstallationOperationState.Succeeded,
                InstallationStage.Completed, null, null, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, false), options: RelaxKonOSJsonOptions.Default) };
        }
    }
    private sealed class Client:IServerPostgreSql
    {
        public PostgreSqlState State=new(true,true,true,"16.15-0ubuntu0.24.04.1",2,null,55432,false,false,null,null,null);
        public Task StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken ct) => Task.CompletedTask;
        public Task<PostgreSqlState> MigrationPlanAsync(CancellationToken ct)=>ReadAsync(ct);
        public Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken ct)=>ReadAsync(ct);
        public Guid? MissingInstance { get; set; }
        public Guid? LastInstance { get; private set; }
        public Task<PostgreSqlState> ReadAsync(CancellationToken ct, Guid? instance = null)
        {
            LastInstance = instance;
            return instance.HasValue && instance == MissingInstance
                ? Task.FromException<PostgreSqlState>(new InstallationApiException("database.instance_invalid", System.Net.HttpStatusCode.ServiceUnavailable))
                : Task.FromResult(State);
        }
    }
}
