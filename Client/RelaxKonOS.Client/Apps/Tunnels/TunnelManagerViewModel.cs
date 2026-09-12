using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;
using System.Collections.ObjectModel;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.Client.Apps.Tunnels;

/// <summary>Shared state for the tunnel list, runtime page, and independent child windows.</summary>
public sealed partial class TunnelManagerViewModel(IRemoteTunnelClient client, bool canManage) : LocalizedObservableObject, IDisposable
{
    public InstallationTaskViewModel Installation { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    public ObservableCollection<TunnelServerProfileDto> Profiles { get; } = [];
    public ObservableCollection<TunnelDefinitionDto> Tunnels { get; } = [];

    [ObservableProperty] private TunnelServerProfileDto? _selectedProfile;
    [ObservableProperty] private TunnelDefinitionDto? _selectedTunnel;
    [ObservableProperty] private TunnelRuntimeDto? _runtime;
    [ObservableProperty] private string _runtimeText = "—";
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.Ref("tunnels.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _runtimeVersion = "v0.71.0";
    [ObservableProperty] private string _frpsBindAddress = "0.0.0.0";
    [ObservableProperty] private int _frpsBindPort = 7000;
    [ObservableProperty] private string _frpsAllowPorts = string.Empty;
    [ObservableProperty] private int? _frpsHttpPort;
    [ObservableProperty] private int? _frpsHttpsPort;
    [ObservableProperty] private bool _frpsForceTls;
    [ObservableProperty] private string _frpsToken = string.Empty;
    [ObservableProperty] private bool _frpsTokenConfigured;
    [ObservableProperty] private bool _frpsDashboardEnabled;
    [ObservableProperty] private string _frpsDashboardAddress = "127.0.0.1";
    [ObservableProperty] private int? _frpsDashboardPort;
    [ObservableProperty] private string _frpsDashboardUser = string.Empty;
    [ObservableProperty] private string _frpsDashboardPassword = string.Empty;
    [ObservableProperty] private bool _frpsDashboardPasswordConfigured;
    [ObservableProperty] private bool _isFrpsLoaded;
    [ObservableProperty] private bool _frpsLoadFailed;
    [ObservableProperty] private ManagedFrpsState _frpsState = ManagedFrpsState.NotConfigured;
    [ObservableProperty] private DateTimeOffset? _frpsStartedAt;
    [ObservableProperty] private string _frpsStateText = "—";
    [ObservableProperty] private string _frpsLogsText = string.Empty;
    [ObservableProperty] private string _frpsAuditText = string.Empty;

    public Func<TunnelServerProfileDto?, Task>? OpenProfileEditorAsync { get; set; }
    public Func<TunnelDefinitionDto?, Task>? OpenTunnelEditorAsync { get; set; }
    public Func<TunnelServerProfileDto, Task>? OpenLogsWindowAsync { get; set; }
    public Func<Task<string?>>? RequestServerRuntimePackageAsync { get; set; }
    public Func<string, Task>? ShowRuntimeDownloadUrlAsync { get; set; }
    public Func<Task>? ShowManagedFrpsConfigurationAsync { get; set; }
    public Func<Task>? ShowManagedFrpsDiagnosticsAsync { get; set; }
    public Func<string, string, Task<bool>>? RequestConfirmationAsync { get; set; }

    public bool CanManage => canManage;
    public bool HasSelectedProfile => SelectedProfile is not null;
    public bool HasSelectedTunnel => SelectedTunnel is not null;
    public int ConnectedTunnelCount => Tunnels.Count(x => x.State == TunnelConnectionState.Connected);
    public int SavedTunnelCount => Tunnels.Count(x => x.State == TunnelConnectionState.SavedNotApplied);
    public bool CanInstallRuntime => CanManage && !IsBusy;
    public bool RuntimeIsInstalled => Runtime is { Mode: TunnelRuntimeMode.Managed, State: TunnelRuntimeState.Available };
    public bool RuntimeIsNotInstalled => !RuntimeIsInstalled;
    public string RuntimeInstalledVersion => Runtime?.Version ?? "—";
    public bool CanUninstallRuntime => CanManage && RuntimeIsInstalled && !IsBusy;
    public bool FrpsIsRunning => FrpsState == ManagedFrpsState.Running;
    public bool FrpsIsStarting => FrpsState == ManagedFrpsState.Starting;
    public string FrpsStateLabel => LocalizedText.Get($"tunnels.frps.state.{FrpsState}");
    public string FrpsActionText => LocalizedText.Get(FrpsIsRunning ? "tunnels.frps.stop" : "tunnels.frps.start");
    public int FrpsConnectionCount => ConnectedTunnelCount;
    public string FrpsEndpoint => $"{FrpsBindAddress}:{FrpsBindPort}";
    public string FrpsAllowedPortsSummary => string.IsNullOrWhiteSpace(FrpsAllowPorts) ? LocalizedText.Get("tunnels.frps.allow_ports_unset") : FrpsAllowPorts;
    public string FrpsStartedAtText => FrpsStartedAt is { } startedAt ? startedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : LocalizedText.Get("tunnels.frps.not_started");
    public bool IsFrpsLoading => !IsFrpsLoaded && !FrpsLoadFailed;

    public async Task StartAsync()
    {
        await RefreshAsync();
        _ = RefreshTunnelStatesPeriodicallyAsync();
    }
    public Task RefreshAfterChildAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        if (!IsFrpsLoaded) FrpsLoadFailed = false;
        try { await RefreshCoreAsync(); StatusText = LocalizedText.Ref("tunnels.status.ready"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FrpsLoadFailed = !IsFrpsLoaded; StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task NewProfileAsync() => OpenProfileEditorAsync?.Invoke(null) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task EditProfileAsync() => SelectedProfile is { } profile
        ? OpenProfileEditorAsync?.Invoke(profile) ?? Task.CompletedTask
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task NewTunnelAsync() => OpenTunnelEditorAsync?.Invoke(null) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task EditTunnelAsync() => SelectedTunnel is { } tunnel
        ? OpenTunnelEditorAsync?.Invoke(tunnel) ?? Task.CompletedTask
        : Task.CompletedTask;

    [RelayCommand]
    private Task OpenLogsAsync(TunnelServerProfileDto? profile) => profile is null
        ? Task.CompletedTask
        : OpenLogsWindowAsync?.Invoke(profile) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanApplySelected))] private Task ApplySelectedAsync() => RunProfileOperationAsync(profile => client.ApplyAsync(profile.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanApplySelected))] private Task StopSelectedAsync() => RunProfileOperationAsync(profile => client.StopAsync(profile.Id, _lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private async Task InstallRuntimeAsync()
    {
        if (!await ConfirmAsync("common.install", "tunnels.runtime.install_confirmation", RuntimeVersion)) return;
        await Installation.SubmitAsync(InstallationOperationKind.Install, new FrpInstallationRequest(true, RuntimeVersion));
    }
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private async Task InstallRuntimeFromServerFileAsync()
    {
        if (RequestServerRuntimePackageAsync is not { } request) return;
        var path = await request();
        if (string.IsNullOrWhiteSpace(path) || !await ConfirmAsync("common.install", "tunnels.runtime.install_confirmation", RuntimeVersion)) return;
        var reference = await Installation.CreateFileReferenceAsync(path);
        if (reference is not null)
            await Installation.SubmitAsync(InstallationOperationKind.Install, new FrpInstallationRequest(true, RuntimeVersion, FileReferenceId: reference));
    }
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private async Task ShowRuntimeDownloadAsync()
    {
        try
        {
            var download = await client.GetManagedRuntimeDownloadAsync(RuntimeVersion, _lifetime.Token);
            if (download is null) { StatusText = LocalizedText.Ref("tunnels.runtime_download_unavailable"); return; }
            await (ShowRuntimeDownloadUrlAsync?.Invoke(download.Url) ?? Task.CompletedTask);
        }
        catch (Exception exception) { StatusText = ProblemText(exception); }
    }
    [RelayCommand(CanExecute = nameof(CanUninstallRuntime))]
    private async Task UninstallRuntimeAsync()
    {
        if (!await ConfirmAsync("common.delete", "tunnels.runtime.uninstall_confirmation")) return;
        await Installation.SubmitAsync(InstallationOperationKind.Uninstall, new FrpInstallationRequest(true));
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SaveManagedFrpsAsync()
    {
        if (IsBusy) return;
        if (!await ConfirmAsync("common.save", "tunnels.frps.save_confirmation")) return;
        IsBusy = true;
        try
        {
            var saved = await client.UpdateManagedFrpsAsync(new(true, FrpsBindAddress, FrpsBindPort, ParsePortRanges(FrpsAllowPorts), FrpsHttpPort, FrpsHttpsPort, FrpsForceTls,
                string.IsNullOrWhiteSpace(FrpsToken) ? null : FrpsToken, FrpsDashboardEnabled, FrpsDashboardAddress, FrpsDashboardPort,
                string.IsNullOrWhiteSpace(FrpsDashboardUser) ? null : FrpsDashboardUser, string.IsNullOrWhiteSpace(FrpsDashboardPassword) ? null : FrpsDashboardPassword), _lifetime.Token);
            ApplyFrps(saved, includeToken: true); FrpsDashboardPassword = string.Empty; StatusText = LocalizedText.Ref("tunnels.status.frps_saved");
        }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StartManagedFrpsAsync() => RunFrpsOperationAsync(() => client.StartManagedFrpsAsync(_lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanManage))] private Task StopManagedFrpsAsync() => RunFrpsOperationAsync(() => client.StopManagedFrpsAsync(_lifetime.Token));
    [RelayCommand(CanExecute = nameof(CanManageFrps))]
    private Task ToggleManagedFrpsAsync() => FrpsIsRunning
        ? RunFrpsOperationAsync(() => client.StopManagedFrpsAsync(_lifetime.Token))
        : RunFrpsOperationAsync(() => client.StartManagedFrpsAsync(_lifetime.Token));
    [RelayCommand]
    private async Task OpenManagedFrpsConfigurationAsync()
    {
        await RefreshManagedFrpsAsync();
        if (ShowManagedFrpsConfigurationAsync is not null) await ShowManagedFrpsConfigurationAsync();
    }
    [RelayCommand]
    private async Task OpenManagedFrpsDiagnosticsAsync()
    {
        await RefreshManagedFrpsDiagnosticsAsync();
        if (ShowManagedFrpsDiagnosticsAsync is not null) await ShowManagedFrpsDiagnosticsAsync();
    }
    [RelayCommand]
    private async Task RefreshManagedFrpsAsync()
    {
        try { ApplyFrps(await client.GetManagedFrpsAsync(_lifetime.Token)); }
        catch (Exception ex) { FrpsLoadFailed = !IsFrpsLoaded; StatusText = ProblemText(ex); }
    }
    [RelayCommand]
    private async Task RefreshManagedFrpsDiagnosticsAsync()
    {
        try
        {
            var logsTask = client.GetManagedFrpsLogsAsync(_lifetime.Token);
            var auditTask = client.GetManagedFrpsAuditAsync(_lifetime.Token);
            await Task.WhenAll(logsTask, auditTask);
            FrpsLogsText = string.Join(Environment.NewLine, (await logsTask).Select(x => $"{x.Timestamp:HH:mm:ss} {x.Level}: {x.Message}"));
            FrpsAuditText = string.Join(Environment.NewLine, (await auditTask).Select(FormatAudit));
        }
        catch (Exception ex) { StatusText = ProblemText(ex); }
    }
    [RelayCommand(CanExecute = nameof(CanManage))] private Task RollbackRuntimeAsync() => Installation.SubmitAsync(InstallationOperationKind.Repair, new FrpInstallationRequest(true, Rollback: true));

    public async Task LoadManagedFrpsForEditingAsync()
    {
        ApplyFrps(await client.GetManagedFrpsForEditingAsync(_lifetime.Token), includeToken: true);
    }

    private bool CanApplySelected => CanManage && HasSelectedProfile && !IsBusy;
    private bool CanManageFrps => CanManage && !IsBusy && !FrpsIsStarting;
    partial void OnSelectedProfileChanged(TunnelServerProfileDto? value) => NotifyProfileCommands();
    partial void OnSelectedTunnelChanged(TunnelDefinitionDto? value) => EditTunnelCommand.NotifyCanExecuteChanged();
    partial void OnRuntimeChanged(TunnelRuntimeDto? value)
    {
        OnPropertyChanged(nameof(RuntimeIsInstalled));
        OnPropertyChanged(nameof(RuntimeIsNotInstalled));
        OnPropertyChanged(nameof(RuntimeInstalledVersion));
        UninstallRuntimeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyProfileCommands(); EditTunnelCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged(); ShowRuntimeDownloadCommand.NotifyCanExecuteChanged(); UninstallRuntimeCommand.NotifyCanExecuteChanged(); RollbackRuntimeCommand.NotifyCanExecuteChanged();
        ToggleManagedFrpsCommand.NotifyCanExecuteChanged();
    }
    partial void OnFrpsStateChanged(ManagedFrpsState value)
    {
        OnPropertyChanged(nameof(FrpsIsRunning));
        OnPropertyChanged(nameof(FrpsIsStarting));
        OnPropertyChanged(nameof(FrpsStateLabel));
        OnPropertyChanged(nameof(FrpsActionText));
        ToggleManagedFrpsCommand.NotifyCanExecuteChanged();
    }
    partial void OnFrpsBindAddressChanged(string value) => OnPropertyChanged(nameof(FrpsEndpoint));
    partial void OnFrpsBindPortChanged(int value) => OnPropertyChanged(nameof(FrpsEndpoint));
    partial void OnFrpsAllowPortsChanged(string value) => OnPropertyChanged(nameof(FrpsAllowedPortsSummary));
    partial void OnFrpsStartedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(FrpsStartedAtText));
    partial void OnIsFrpsLoadedChanged(bool value) => OnPropertyChanged(nameof(IsFrpsLoading));
    partial void OnFrpsLoadFailedChanged(bool value) => OnPropertyChanged(nameof(IsFrpsLoading));
    private void NotifyProfileCommands()
    {
        ApplySelectedCommand.NotifyCanExecuteChanged(); StopSelectedCommand.NotifyCanExecuteChanged();
        EditProfileCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> ConfirmAsync(string titleKey, string messageKey, params object[] arguments) =>
        RequestConfirmationAsync is not null && await RequestConfirmationAsync(LocalizedText.Get(titleKey), LocalizedText.Format(messageKey, arguments));

    private async Task RunProfileOperationAsync(Func<TunnelServerProfileDto, Task<TunnelOperationResultDto>> operation)
    {
        if (IsBusy || SelectedProfile is null) return;
        IsBusy = true;
        try
        {
            var result = await operation(SelectedProfile);
            StatusText = result.Succeeded ? LocalizedText.Ref("tunnels.status.operation", LocalizedText.Get($"tunnels.connection_state.{result.State}")) : ProblemText(result.ProblemCode);
        }
        catch (Exception ex) { StatusText = ProblemText(ex); }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    private async Task RefreshCoreAsync()
    {
        var profilesTask = client.ListProfilesAsync(_lifetime.Token);
        var tunnelsTask = client.ListAsync(_lifetime.Token);
        var runtimeTask = client.GetRuntimeAsync(_lifetime.Token);
        var frpsTask = client.GetManagedFrpsAsync(_lifetime.Token);

        // Keep the persisted profiles and tunnel definitions visible even when an optional
        // runtime-status request fails (for example while connected to an older server).
        // Previously, a single 404 prevented both collections from being applied.
        var profiles = await profilesTask;
        Replace(Profiles, profiles);
        KeepSelections();

        var tunnels = await tunnelsTask;
        Replace(Tunnels, tunnels);
        KeepSelections(); UpdateCounters();

        // The FRPS overview is independently useful. Apply it as soon as its request succeeds
        // so an unrelated runtime request cannot make a saved local server look unconfigured.
        ApplyFrps(await frpsTask);
        Runtime = await runtimeTask; RuntimeText = FormatRuntime(Runtime);
    }

    private async Task RefreshTunnelStatesPeriodicallyAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
                if (IsBusy) continue;

                try
                {
                    var tunnels = await client.ListAsync(_lifetime.Token);
                    Replace(Tunnels, tunnels);
                    KeepSelections();
                    UpdateCounters();
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch { /* Keep the current display and retry at the next interval. */ }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void KeepSelections()
    {
        if (SelectedProfile is { } profile) SelectedProfile = Profiles.FirstOrDefault(x => x.Id == profile.Id);
        if (SelectedTunnel is { } tunnel) SelectedTunnel = Tunnels.FirstOrDefault(x => x.Id == tunnel.Id);
    }
    private void UpdateCounters() { OnPropertyChanged(nameof(ConnectedTunnelCount)); OnPropertyChanged(nameof(SavedTunnelCount)); OnPropertyChanged(nameof(FrpsConnectionCount)); }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }
    private static string FormatRuntime(TunnelRuntimeDto runtime) => string.Join(" · ", new[]
    {
        LocalizedText.Get($"tunnels.runtime.state.{runtime.State}"),
        runtime.Version,
        string.IsNullOrEmpty(runtime.ProblemCode) ? null : ProblemText(runtime.ProblemCode)
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string ProblemText(Exception ex) => ex is TunnelRequestException request ? ProblemText(request.ProblemCode) : LocalizedText.Get("tunnels.status.failed");
    private static string ProblemText(string problemCode)
    {
        var key = $"tunnels.problem.{problemCode}";
        var text = LocalizedText.Get(key);
        return text == key ? LocalizedText.Get("tunnels.status.failed") : text;
    }
    private static string FormatAudit(TunnelAuditEntryDto entry)
    {
        var action = LocalizedText.Get($"tunnels.audit.action.{entry.Action}");
        var result = LocalizedText.Get($"tunnels.audit.result.{entry.Result}");
        var problem = string.IsNullOrEmpty(entry.ProblemCode) ? null : ProblemText(entry.ProblemCode);
        return string.Join(" · ", new[] { entry.Timestamp.ToLocalTime().ToString("g"), action, result, problem }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
    private async Task RunFrpsOperationAsync(Func<Task<TunnelOperationResultDto>> operation) { if (IsBusy) return; IsBusy = true; try { var result = await operation(); StatusText = result.Succeeded ? LocalizedText.Ref("tunnels.status.frps_updated") : ProblemText(result.ProblemCode); } catch (Exception ex) { StatusText = ProblemText(ex); } finally { IsBusy = false; } await RefreshManagedFrpsAsync(); }
    private void ApplyFrps(ManagedFrpsConfigurationDto value, bool includeToken = false) { FrpsBindAddress = value.BindAddress; FrpsBindPort = value.BindPort; FrpsAllowPorts = string.Join(", ", value.AllowPorts.Select(x => x.Start == x.End ? x.Start.ToString() : $"{x.Start}-{x.End}")); FrpsHttpPort = value.VhostHttpPort; FrpsHttpsPort = value.VhostHttpsPort; FrpsForceTls = value.ForceTls; FrpsTokenConfigured = value.TokenConfigured; if (includeToken) FrpsToken = value.Token ?? string.Empty; FrpsDashboardEnabled = value.DashboardEnabled; FrpsDashboardAddress = value.DashboardAddress; FrpsDashboardPort = value.DashboardPort; FrpsDashboardUser = value.DashboardUser ?? string.Empty; FrpsDashboardPasswordConfigured = value.DashboardPasswordConfigured; FrpsState = value.State; FrpsStartedAt = value.StartedAt; FrpsStateText = string.IsNullOrEmpty(value.ProblemCode) ? string.Empty : ProblemText(value.ProblemCode); IsFrpsLoaded = true; FrpsLoadFailed = false; }
    private static IReadOnlyList<TunnelPortRangeDto> ParsePortRanges(string value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => { var parts = x.Split('-', StringSplitOptions.TrimEntries); return parts.Length switch { 1 when int.TryParse(parts[0], out var single) => new TunnelPortRangeDto(single, single), 2 when int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var end) => new TunnelPortRangeDto(start, end), _ => throw new TunnelRequestException("tunnel.frps_invalid_allow_ports") }; }).ToArray();
    public void Dispose() => _lifetime.Cancel();
}
