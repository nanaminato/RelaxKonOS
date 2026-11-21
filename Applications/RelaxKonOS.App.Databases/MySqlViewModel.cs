using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.App.Databases;

public sealed partial class MySqlViewModel(IServerMySql client, IExternalServerConnection connection) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool invalid;
    private bool reading;
    private MySqlRequest? submission;
    public InstallationTaskViewModel Installation { get; set; } = null!;
    public bool CanManage { get; init; }
    public InstallationTaskViewModel? AdministratorInstallation { get; set; }
    public Func<Task<bool>>? ConfirmAdministratorAsync { get; set; }
    public Func<Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] private MySqlState? state;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private DatabaseStatus notice = DatabaseStatus.Literal("");

    public DatabaseStatus Status => DatabaseStatus.Key(State switch
    {
        null => "database.state_unavailable",
        { ProblemCode: { } problem } => problem,
        { Installed: false } => "database.not_installed",
        { InstanceId: null } => "database.not_initialized",
        { Running: true, Verified: true } => "database.connection_verified",
        { Running: true } => "database.connection_unverified",
        _ => "database.stopped"
    });
    private bool Writable => !invalid && !Busy && CanManage && !Installation.IsActive && AccountInstallation?.IsActive != true && AdministratorInstallation?.IsActive != true && RemoteInstallation?.IsActive != true && MaintenanceInstallation?.IsActive != true && submission is null
        && State is { Supported: true, PendingOperationId: null, ProblemCode: null, RemoteAccess: null };
    public bool CanInstall => Writable && State is { Installed: false, InstanceId: null };
    public bool CanInitialize => Writable && State is { Installed: true, InstanceId: null };
    public bool CanStart => Writable && State is { Installed: true, InstanceId: not null, Running: false };
    public bool CanStop => Writable && State is { Running: true };
    public bool CanRecover => !invalid && !Busy && CanManage && !Installation.IsActive && AccountInstallation?.IsActive != true && AdministratorInstallation?.IsActive != true && RemoteInstallation?.IsActive != true && MaintenanceInstallation?.IsActive != true && submission is null && State?.PendingOperationId is not null;
    public bool CanRecoverAdministrator => !invalid && !Busy && CanManage && !Installation.IsActive && RemoteInstallation?.IsActive != true
        && AccountInstallation?.IsActive != true && MaintenanceInstallation?.IsActive != true && AdministratorInstallation is { IsActive: false } && submission is null
        && State is { Supported: true, Installed: true, InstanceId: not null, PendingOperationId: null, Verified: false, RemoteAccess: null };
    public bool CanResume => !invalid && !Busy && CanManage && !Installation.IsActive && AccountInstallation?.IsActive != true && AdministratorInstallation?.IsActive != true && RemoteInstallation?.IsActive != true && MaintenanceInstallation?.IsActive != true && submission is not null;

    partial void OnStateChanged(MySqlState? value)
    {
        SelectedDatabase = Databases.FirstOrDefault(x => x == SelectedDatabase);
        SelectedAccount = Accounts.FirstOrDefault(x => x.Identity == SelectedAccount?.Identity);
        SelectedBackup = MaintenanceBackups.FirstOrDefault(x => x.Id == SelectedBackup?.Id);
        Notify();
    }
    partial void OnBusyChanged(bool value) => Notify();
    public void Notify()
    {
        foreach (var property in new[] { nameof(Status), nameof(CanInstall), nameof(CanInitialize), nameof(CanStart),
            nameof(CanStop), nameof(CanRecover), nameof(CanRecoverAdministrator), nameof(CanResume), nameof(CanOpenRemote), nameof(CanCloseRemote), nameof(RemoteConnectionInfo), nameof(CanManageAccounts), nameof(Databases), nameof(Accounts),
            nameof(VersionLabel), nameof(MaintenanceBackups), nameof(DeleteInstanceId), nameof(CanBackup), nameof(CanRestore), nameof(CanUpdate), nameof(CanRepair), nameof(CanReinstall), nameof(CanDeleteData) }) OnPropertyChanged(property);
    }

    [RelayCommand] public async Task RefreshAsync()
    {
        if (invalid || reading) return;
        reading = true;
        try
        {
            var current = await client.ReadAsync(lifetime.Token);
            if (!invalid && connection.IsCurrent)
            {
                State = current;
                Notice = DatabaseStatus.Literal("");
            }
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            State = null;
            Notice = DatabaseStatus.Key("database.state_unavailable");
        }
        catch (OperationCanceledException) { }
        finally { reading = false; }
    }

    [RelayCommand] private Task InstallAsync() => SubmitAsync(MySqlAction.Install, CanInstall);
    [RelayCommand] private Task InitializeAsync() => SubmitAsync(MySqlAction.Initialize, CanInitialize);
    [RelayCommand] private Task StartAsync() => SubmitAsync(MySqlAction.Start, CanStart);
    [RelayCommand] private Task StopAsync() => SubmitAsync(MySqlAction.Stop, CanStop);
    [RelayCommand] private Task VerifyAsync() => SubmitAsync(MySqlAction.Verify, CanStop);
    [RelayCommand] private Task RecoverAsync() => SubmitAsync(null, CanRecover);
    [RelayCommand] private Task RecoverAdministratorAsync() => SubmitAsync(MySqlAction.RecoverAdministrator, CanRecoverAdministrator);
    [RelayCommand] private async Task ResumeAsync()
    {
        if (!CanResume) return;
        Busy = true;
        try { await SendAsync(); }
        finally { Busy = false; }
    }
    private async Task SubmitAsync(MySqlAction? action, bool allowed)
    {
        if (!allowed || State is not { } snapshot) return;
        Busy = true;
        try
        {
            var administrator = action == MySqlAction.RecoverAdministrator || action is null && snapshot.PendingAdministratorRecovery;
            var remote = action is null && snapshot.PendingRemote is not null;
            var maintenance = action is null && snapshot.PendingMaintenance is not null;
            Func<Task<bool>>? confirm = maintenance ? ConfirmMaintenanceAsync is { } ask ? () => ask(null) : null
                : remote ? ConfirmRemoteAsync : administrator ? ConfirmAdministratorAsync : ConfirmAsync;
            if (confirm is null || !await confirm() || invalid || !connection.IsCurrent) return;
            submission = action is null ? new(true, RecoverOperationId: snapshot.PendingOperationId)
                : new(true, action, snapshot.Revision, AdministratorInstanceId: administrator ? snapshot.InstanceId : null);
            submissionTask = maintenance ? MaintenanceInstallation : remote ? RemoteInstallation : administrator ? AdministratorInstallation : action is null && snapshot.PendingAccount is not null ? AccountInstallation : Installation;
            await SendAsync();
        }
        finally { Busy = false; }
    }

    private async Task SendAsync()
    {
        var kind = submission!.RecoverOperationId is not null ? InstallationOperationKind.Repair
            : submission.Action == MySqlAction.Install ? InstallationOperationKind.Install : InstallationOperationKind.Configure;
        var task = submissionTask ?? Installation;
        var previous = task.Operation?.OperationId;
        await task.SubmitAsync(kind, submission);
        if (!task.HasPendingSubmission || task.Operation?.OperationId is { } accepted && accepted != previous)
            submission = null;
        Notify();
    }

    public void Invalidate()
    {
        invalid = true;
        lifetime.Cancel();
        State = null;
        submission = null;
        AccountPassword = "";
    }

    public void Dispose()
    {
        Invalidate();
        Installation.Dispose();
        AccountInstallation?.Dispose();
        AdministratorInstallation?.Dispose();
        RemoteInstallation?.Dispose();
        MaintenanceInstallation?.Dispose();
        lifetime.Dispose();
    }
}
