using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.ManagedRuntimes.MySql;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.Client.Apps.Databases;

public sealed partial class MySqlViewModel
{
    public InstallationTaskViewModel? MaintenanceInstallation { get; set; }
    public Func<MySqlMaintenanceCommand?, Task<bool>>? ConfirmMaintenanceAsync { get; set; }
    [ObservableProperty] private MySqlBackup? selectedBackup;
    [ObservableProperty] private string deleteConfirmation = "";

    public string VersionLabel => "MySQL " + (State?.Version ?? "8.4") + " LTS";
    public IReadOnlyList<MySqlBackup> MaintenanceBackups => State is { } state
        ? (state.Backups ?? []).Where(backup => backup.Version == state.Version).ToArray() : [];
    public string DeleteInstanceId => State?.InstanceId?.ToString("N") ?? "";
    private bool MaintenanceReady => !invalid && !Busy && CanManage && submission is null && !Installation.IsActive
        && AccountInstallation?.IsActive != true && AdministratorInstallation?.IsActive != true && RemoteInstallation?.IsActive != true
        && MaintenanceInstallation is { IsActive: false }
        && State is { Supported: true, PendingOperationId: null, InstanceId: not null, Version: not null, RemoteAccess: null };
    public bool CanBackup => MaintenanceReady && State is { Installed: true, Running: true, Verified: true };
    public bool CanRestore => CanBackup && SelectedBackup is { } selected && MaintenanceBackups.Contains(selected);
    public bool CanUpdate => CanBackup && State?.Version is { } version && MySqlMaintenancePolicy.CanUpdate(version, MySqlMaintenancePolicy.Target);
    public bool CanRepair => MaintenanceReady && State is { Installed: true, Running: false } && MaintenanceBackups.Count != 0;
    public bool CanReinstall => MaintenanceReady && State is { Installed: false };
    public bool CanDeleteData => CanBackup && DeleteConfirmation == DeleteInstanceId;

    partial void OnSelectedBackupChanged(MySqlBackup? value) => OnPropertyChanged(nameof(CanRestore));
    partial void OnDeleteConfirmationChanged(string value) => OnPropertyChanged(nameof(CanDeleteData));

    [RelayCommand] private Task BackupAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.Backup, CanBackup);
    [RelayCommand] private Task RestoreBackupAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.Restore, CanRestore);
    [RelayCommand] private Task UpdateAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.Update, CanUpdate);
    [RelayCommand] private Task RepairAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.Repair, CanRepair);
    [RelayCommand] private Task ReinstallAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.Reinstall, CanReinstall);
    [RelayCommand] private Task UninstallKeepAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.UninstallKeepData, CanBackup);
    [RelayCommand] private Task UninstallDeleteAsync() => SubmitMaintenanceAsync(MySqlMaintenanceAction.UninstallDeleteData, CanDeleteData);

    private async Task SubmitMaintenanceAsync(MySqlMaintenanceAction action, bool allowed)
    {
        if (!allowed || State is not { InstanceId: { } id, Version: { } version } state) return;
        var command = new MySqlMaintenanceCommand(id, action, action == MySqlMaintenanceAction.Update ? MySqlMaintenancePolicy.Target : version,
            action == MySqlMaintenanceAction.Restore ? SelectedBackup?.Id : null,
            action == MySqlMaintenanceAction.UninstallDeleteData ? DeleteConfirmation : null);
        if (!command.Valid) { Notice = LocalizedStatus.Key("database.maintenance_invalid"); return; }
        Busy = true;
        try
        {
            if (ConfirmMaintenanceAsync is null || !await ConfirmMaintenanceAsync(command) || invalid || !connection.IsCurrent) return;
            submission = new(true, MySqlAction.Maintain, state.Revision, Maintenance: command);
            submissionTask = MaintenanceInstallation;
            await SendAsync();
        }
        finally { Busy = false; }
    }
}
