using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;

namespace RelaxKonOS.Client.Apps.Databases;

public sealed partial class DatabaseViewModel(IPostgreSqlClient client) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool invalid;
    private bool reading;
    private PostgreSqlInstallationRequest? submission;
    private InstallationOperationKind submissionKind;
    [ObservableProperty] private PostgreSqlState? snapshot;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private LocalizedStatus notice = LocalizedStatus.Literal("");
    public InstallationTaskViewModel Installation { get; set; } = null!;
    public InstallationTaskViewModel? AdminRecoveryInstallation { get; set; }
    public Func<Task<bool>>? ConfirmAdminRecoveryAsync { get; set; }
    private PostgreSqlInstallationRequest? adminSubmission;
    public ComponentDependencySettingsViewModel Dependency { get; set; } = null!;
    public bool CanManage { get; set; }
    public PostgreSqlAccountsViewModel? AccountManager { get; set; }
    public PostgreSqlMaintenanceViewModel? MaintenanceManager { get; set; }
    public bool MaintenanceBusy => MaintenanceManager?.Busy==true||MaintenanceManager?.HasPending==true||MaintenanceManager?.Installation.IsActive==true||MaintenanceManager?.ScheduleInstallation?.IsActive==true;
    public PostgreSqlRemoteViewModel? RemoteManager { get; set; }
    public Func<PostgreSqlAction, PostgreSqlState, Task<bool>>? ConfirmAsync { get; set; }
    public Func<Task<int?>>? ChoosePortAsync { get; set; }
    public Func<Guid, Task<bool>>? ConfirmRecoveryAsync { get; set; }
    public LocalizedStatus StateText => LocalizedStatus.Key(Snapshot is not { } s ? "database.state_unavailable" : !s.Supported ? "database.platform_unsupported"
        : s.PendingOperationId is not null ? "database.recovery_required" : s.PackageRemoved ? "database.package_retained" : !s.Installed ? "database.not_installed" : s.InstanceId is null ? "database.not_initialized"
        : s.Running ? "database.running" : "database.stopped");
    public LocalizedStatus ConnectionText => LocalizedStatus.Key(Snapshot?.ConnectionVerified == true ? "database.connection_verified" : "database.connection_unverified");
    public string Version => Snapshot?.Version ?? "—";
    public string Instance => Snapshot?.InstanceId?.ToString() ?? "—";
    public string Port => Snapshot?.Port.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "—";
    private bool Writable => !MaintenanceBusy && RemoteManager?.Busy != true && RemoteManager?.HasPending != true && RemoteManager?.Installation.IsActive != true && adminSubmission is null && AdminRecoveryInstallation?.IsActive != true && AccountManager?.HasPendingRequest != true && AccountManager?.Busy != true && AccountManager?.Installation.IsActive != true && submission is null && !invalid && !Busy && Installation?.IsActive != true && CanManage && Snapshot is { Supported: true, Managed: true, PendingOperationId: null, ProblemCode: null };
    public bool CanInitialize => Writable && Snapshot?.InstanceId is null;
    public bool CanStart => Writable && Snapshot is { InstanceId: not null, Running: false };
    public bool CanStop => Writable && Snapshot?.Running == true;
    public bool CanVerify => CanStop;
    public bool CanResume => !invalid && !Busy && CanManage && Installation?.IsActive != true && submission is not null;
    public bool CanRecover => !MaintenanceBusy && Snapshot?.PendingMaintenance is null && Snapshot?.PendingRemoteAccess is null && Snapshot?.PendingAdminRecovery is null && Snapshot?.PendingAccount is null && submission is null && !invalid && !Busy && CanManage && Installation?.IsActive != true && Snapshot?.PendingOperationId is not null;
    partial void OnSnapshotChanged(PostgreSqlState? value) { if(MaintenanceManager is not null)MaintenanceManager.Snapshot=value; if (AccountManager is not null) AccountManager.Snapshot = value; if (RemoteManager is not null) RemoteManager.Snapshot = value; NotifyState(); }
    partial void OnBusyChanged(bool value) => NotifyState();
    public void NotifyState()
    {
        foreach (var name in new[] { nameof(StateText), nameof(ConnectionText), nameof(Version), nameof(Instance), nameof(Port),
            nameof(CanAdminRecovery), nameof(CanResumeAdminRecovery), nameof(CanInitialize), nameof(CanStart), nameof(CanStop), nameof(CanVerify), nameof(CanRecover), nameof(CanResume) }) OnPropertyChanged(name);
    }
    public bool CanAdminRecovery => !MaintenanceBusy && RemoteManager?.Busy != true && RemoteManager?.HasPending != true && RemoteManager?.Installation.IsActive != true && Snapshot?.RemoteAccess is null && !invalid && !Busy && CanManage && Installation?.IsActive != true
        && AccountManager?.Busy != true && AccountManager?.Installation.IsActive != true && AccountManager?.HasPendingRequest != true
        && submission is null && adminSubmission is null && AdminRecoveryInstallation is { IsActive: false }
        && Snapshot is { Supported: true, Installed: true, InstanceId: not null } s
        && (s.PendingOperationId is null || s.PendingAdminRecovery is not null);
    public bool CanResumeAdminRecovery => !invalid && !Busy && CanManage && adminSubmission is not null && AdminRecoveryInstallation is { IsActive: false };
    [RelayCommand] private async Task AdminRecoveryAsync()
    {
        if (!CanAdminRecovery || Snapshot is not { InstanceId: { } instance } before) return;
        Busy = true;
        try
        {
            if (ConfirmAdminRecoveryAsync is null || !await ConfirmAdminRecoveryAsync() || invalid) return;
            adminSubmission = before.PendingAdminRecovery is not null
                ? new(true, RecoverOperationId: before.PendingOperationId)
                : new(true, AdminRecovery: new(instance, before.Revision));
            await SendAdminRecoveryAsync();
        }
        finally { Busy = false; }
    }
    [RelayCommand] private async Task ResumeAdminRecoveryAsync()
    {
        if (!CanResumeAdminRecovery) return;
        Busy = true;
        try { await SendAdminRecoveryAsync(); }
        finally { Busy = false; }
    }
    private async Task SendAdminRecoveryAsync()
    {
        var previous = AdminRecoveryInstallation!.Operation?.OperationId;
        await AdminRecoveryInstallation.SubmitAsync(adminSubmission!.RecoverOperationId is null ? InstallationOperationKind.Configure : InstallationOperationKind.Repair, adminSubmission);
        if (!AdminRecoveryInstallation.HasPendingSubmission || AdminRecoveryInstallation.Operation?.OperationId is { } accepted && accepted != previous) adminSubmission = null;
        NotifyState();
    }
    [RelayCommand] public async Task RefreshAsync()
    {
        if (invalid || reading) return;
        reading = true;
        try { Snapshot = await client.ReadAsync(lifetime.Token); Notice = Snapshot.ProblemCode is { } code ? LocalizedStatus.Key(code) : LocalizedStatus.Literal(""); }
        catch (OperationCanceledException) { }
        catch { Snapshot = null; Notice = LocalizedStatus.Key("database.state_unavailable"); }
        finally { reading = false; }
    }
    [RelayCommand] private Task InitializeAsync() => SubmitAsync(PostgreSqlAction.Initialize);
    [RelayCommand] private Task StartAsync() => SubmitAsync(PostgreSqlAction.Start);
    [RelayCommand] private Task StopAsync() => SubmitAsync(PostgreSqlAction.Stop);
    [RelayCommand] private Task VerifyAsync() => SubmitAsync(PostgreSqlAction.Verify);
    private async Task SubmitAsync(PostgreSqlAction action)
    {
        if (!Writable || Snapshot is not { } before) return;
        if (action == PostgreSqlAction.Initialize && !CanInitialize || action == PostgreSqlAction.Start && !CanStart
            || action == PostgreSqlAction.Stop && !CanStop || action == PostgreSqlAction.Verify && !CanVerify) return;
        Busy = true;
        try
        {
            var port = action == PostgreSqlAction.Initialize ? await (ChoosePortAsync?.Invoke() ?? Task.FromResult<int?>(null)) : before.Port;
            if (port is null || invalid) return;
            if (ConfirmAsync is null || !await ConfirmAsync(action, before) || invalid) return;
            await SendAsync(InstallationOperationKind.Configure, new PostgreSqlInstallationRequest(true,
                Command: new(action, before.InstanceId ?? Guid.NewGuid(), before.Revision, port.Value)));
        }
        finally { Busy = false; }
    }
    [RelayCommand] private async Task RecoverAsync()
    {
        if (!CanRecover || Snapshot?.PendingOperationId is not { } id) return;
        Busy = true;
        try
        {
            if (ConfirmRecoveryAsync is null || !await ConfirmRecoveryAsync(id) || invalid) return;
            await SendAsync(InstallationOperationKind.Repair, new PostgreSqlInstallationRequest(true, RecoverOperationId: id));
        }
        finally { Busy = false; }
    }
    private async Task SendAsync(InstallationOperationKind kind, PostgreSqlInstallationRequest request)
    {
        submission = request; submissionKind = kind; NotifyState();
        var previous = Installation.Operation?.OperationId;
        await Installation.SubmitAsync(kind, request);
        if (!Installation.HasPendingSubmission || Installation.Operation?.OperationId is { } accepted && accepted != previous) submission = null;
        NotifyState();
    }
    [RelayCommand] private async Task ResumeAsync()
    {
        if (!CanResume || submission is not { } request) return;
        Busy = true;
        try { await SendAsync(submissionKind, request); }
        finally { Busy = false; }
    }
    public void Invalidate() { invalid = true; lifetime.Cancel(); MaintenanceManager?.Invalidate(); AccountManager?.Invalidate(); RemoteManager?.Invalidate(); Snapshot = null; Notice = LocalizedStatus.Key("database.session_changed"); NotifyState(); }
    public void Dispose() { Invalidate(); lifetime.Dispose(); Installation?.Dispose(); AdminRecoveryInstallation?.Dispose(); Dependency?.Dispose(); MaintenanceManager?.Dispose(); AccountManager?.Dispose(); RemoteManager?.Dispose(); }
}
