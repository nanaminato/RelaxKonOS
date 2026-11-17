using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Privileged;
namespace RelaxKonOS.Server.Installations;
public sealed class PostgreSqlBackupAutomationInstallationService(IPrivilegedOperationTransport helper,PostgreSqlComponentAdapter component,PostgreSqlCredentialStore credentials)
    :PostgreSqlMaintenanceInstallationService(helper,component,credentials)
{
    public override InstallationServiceId Id=>InstallationServiceId.PostgreSqlBackupAutomation;
    protected override bool Accept(PostgreSqlMaintenanceCommand command)=>command.Action is PostgreSqlMaintenanceAction.ConfigureBackupSchedule or PostgreSqlMaintenanceAction.ScheduledBackup;
}
