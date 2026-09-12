using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;
using System.Collections.ObjectModel;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Client.Apps.Proxy;

/// <summary>Presentation state for the host-global proxy workspace. Controller details stay on the RelaxKonOS.Server.</summary>
public sealed partial class ProxyManagerViewModel : LocalizedObservableObject
{
    public InstallationTaskViewModel Installation { get; set; } = null!;

    private readonly IProxyRepository repository;
    private readonly ITaskManagerClient? systemMonitor;
    private bool _updatingOverviewProxySelection;
    [ObservableProperty] private bool _hasManagePermission;
    [ObservableProperty] private bool _hasTunManagePermission;

    public ProxyManagerViewModel(IProxyRepository repository, bool canManage, bool canManageTun, ITaskManagerClient? systemMonitor = null)
    {
        this.repository = repository;
        this.systemMonitor = systemMonitor;
        _hasManagePermission = canManage;
        _hasTunManagePermission = canManageTun;
    }

    public ObservableCollection<ProxyProfileDto> Profiles { get; } = [];
    public ObservableCollection<ProxySubscriptionDto> Subscriptions { get; } = [];
    public ObservableCollection<ProxyGroupItem> Groups { get; } = [];
    public ObservableCollection<ProxyConnectionDto> Connections { get; } = [];
    public ObservableCollection<ProxyLogEntryDto> Logs { get; } = [];
    public ObservableCollection<string> SystemProxyHostOptions { get; } = ["127.0.0.1"];
    public IEnumerable<ProxySubscriptionDto> VisibleSubscriptions => Subscriptions;

    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.Ref("proxy.status.loading");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ProxyOverviewDto? _overview;
    [ObservableProperty] private ProxyRuntimeDto? _runtime;
    [ObservableProperty] private ProxyDnsStatusDto? _dnsStatus;
    [ObservableProperty] private ProxyTrafficDto? _traffic;
    [ObservableProperty] private ProxySettingsDto? _settings;
    [ObservableProperty] private ProxyGeoDataDto? _geoData;
    [ObservableProperty] private ProxyProfileDto? _selectedProfile;
    [ObservableProperty] private ProxySubscriptionDto? _selectedSubscription;
    [ObservableProperty] private ProxyGroupItem? _selectedGroup;
    [ObservableProperty] private ProxyConnectionDto? _selectedConnection;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _selectedProxy = string.Empty;
    [ObservableProperty] private ProxyRoutingMode _routingMode = ProxyRoutingMode.Rule;
    [ObservableProperty] private ProxyNodeSortMode _proxyNodeSortMode = ProxyNodeSortMode.Default;
    [ObservableProperty] private bool _isLatencyTestTargetVisible;
    [ObservableProperty] private bool _isLatencyTesting;
    [ObservableProperty] private bool _isSubscriptionRefreshActive;
    [ObservableProperty] private string _latencyTestTarget = "https://www.gstatic.com/generate_204";
    [ObservableProperty] private string _subscriptionLink = string.Empty;
    [ObservableProperty] private string _runtimeSubscriptionText = string.Empty;
    [ObservableProperty] private ProxyNodeItem? _selectedOverviewProxyNode;
    [ObservableProperty] private bool _isTunSettingsSelected;

    public Func<Task<string?>>? RequestServerRuntimePackageAsync { get; set; }
    public Func<string, Task>? ShowRuntimeDownloadUrlAsync { get; set; }
    public Func<Task<string?>>? RequestServerGeoDataFileAsync { get; set; }
    public Func<Task<bool>>? RequestSystemProxySubscriptionDownloadAsync { get; set; }
    public Action? ShowRuntimeSubscriptionWindow { get; set; }
    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    /// <summary>Opens the settings dialog for one concrete network mode; the two modes never share an ambiguous entry point.</summary>
    public Func<bool, Task>? OpenNetworkSettingsDialogAsync { get; set; }
    /// <summary>Owned by the workspace so overview cards can navigate without reaching into view code.</summary>
    public Action<string>? NavigateRequested { get; set; }


    public void SetServerRuntimePackageRequest(Func<Task<string?>>? request)
    {
        RequestServerRuntimePackageAsync = request;
        InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged();
    }
    public void SetServerGeoDataFileRequest(Func<Task<string?>>? request)
    {
        RequestServerGeoDataFileAsync = request;
        ConfigureGeoDataFromServerFileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Updates the capability snapshot only after the owning application has completed its permission prompt.</summary>
    public void SetPermissions(bool canManage, bool canManageTun)
    {
        HasManagePermission = canManage;
        HasTunManagePermission = canManageTun;
        NotifyCommands();
    }

    /// <summary>Dialog forms edit an in-memory record. Keep a cheap immutable snapshot so Cancel really means cancel.</summary>
    public ProxySettingsDto? CapturePendingSettings() => Settings;

    public void RestorePendingSettings(ProxySettingsDto? settings) => Settings = settings;

    public int RunningConnectionCount => Connections.Count;
    public string RuntimeVersion => Runtime?.Version ?? "—";
    public string RuntimeState => Runtime is null ? "—" : LocalizeEnum("proxy.runtime_state", Runtime.State);
    public bool ProxyIsRunning => Runtime?.State == ProxyRuntimeState.Running;
    public bool ProxyCanToggle => Runtime?.State is ProxyRuntimeState.Stopped or ProxyRuntimeState.Running;
    public string ProxyActionText => LocalizedText.Get(ProxyIsRunning ? "proxy.stop" : "proxy.start");
    public string ProxyStateLabel => Runtime?.State switch
    {
        ProxyRuntimeState.Running => LocalizedText.Get("proxy.runtime_running"),
        ProxyRuntimeState.Stopped => LocalizedText.Get("proxy.runtime_stopped"),
        _ => LocalizedText.Get("proxy.runtime_unavailable"),
    };
    public bool RuntimeIsInstalled => Runtime is { Mode: ProxyRuntimeMode.Managed, IntegrityVerified: true };
    public bool RuntimeIsNotInstalled => !RuntimeIsInstalled;
    public string RuntimeInstalledVersion => Runtime?.Version ?? "—";
    public string RuntimeAvailableVersion => LocalizedText.Format("proxy.runtime.available_version", RuntimeInstalledVersion);
    public string HealthState => Overview is null ? "—" : LocalizeEnum("proxy.health_state", Overview.Health.State);
    public string TunState => Overview is null ? "—" : LocalizeEnum("proxy.tun_state", Overview.Health.TunState);
    public string DnsState => DnsStatus is { Enabled: true }
        ? (DnsStatus.HijackEnabled ? LocalizedText.Get("proxy.dns.hijack_enabled") : LocalizedText.Get("proxy.dns.enabled"))
        : LocalizedText.Get("proxy.dns.disabled");
    public string ActiveProfileName => Overview?.ActiveProfile?.Name ?? LocalizedText.Get("proxy.none");
    public ProxySubscriptionDto? ActiveSubscription => Subscriptions.FirstOrDefault(subscription => subscription.IsActive);
    public string ActiveSubscriptionName => ActiveSubscription?.Name ?? LocalizedText.Get("proxy.none");
    public string ActiveSubscriptionUpdatedAt => ActiveSubscription?.LastUpdatedAt is { } updated
        ? updated.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : "—";
    public string SystemProxyEndpoint => SystemProxyEnabled ? $"http://{SystemProxyHost}:{MixedPort}" : "—";
    public string RuntimeUptime => "—";
    public string RuleCount => "—";
    public string ServerOperatingSystem => Overview?.OperatingSystem ?? "—";
    /// <summary>Displayed only as an informational status; no startup configuration is read or changed.</summary>
    public string StartupState => LocalizedText.Get("proxy.system_info.startup_disabled");
    public string RuntimeServiceMode => Runtime?.Mode switch
    {
        ProxyRuntimeMode.Managed => LocalizedText.Get("proxy.system_info.managed_service"),
        ProxyRuntimeMode.External => LocalizedText.Get("proxy.system_info.external_runtime"),
        _ => "—",
    };
    public string ConfigurationUpdatedAt => Overview?.ActiveProfile?.UpdatedAt is { } updated
        ? updated.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
        : "—";
    public string ClientVersion => GetType().Assembly.GetName().Version?.ToString() ?? "—";
    /// <summary>The Server orders groups from proxy-groups in the active YAML, making the first one the overview entry point.</summary>
    public ProxyGroupItem? CurrentProxyGroupItem => Groups.FirstOrDefault();
    public string CurrentProxyGroupName => CurrentProxyGroupItem?.Name ?? LocalizedText.Get("proxy.none");
    public string CurrentProxyNodeName => CurrentProxyGroupItem?.Selected ?? "—";
    public IEnumerable<ProxyNodeItem> OverviewProxyNodes => CurrentProxyGroupItem?.Nodes ?? [];
    public string SystemProxyState => LocalizedText.Get(SystemProxyEnabled ? "proxy.overview.enabled" : "proxy.overview.disabled");
    public string TunOverviewState => TunState;
    public string UploadRate => FormatTraffic(Traffic?.UploadBytesPerSecond, "/s");
    public string DownloadRate => FormatTraffic(Traffic?.DownloadBytesPerSecond, "/s");
    public string UploadTotal => FormatTraffic(Traffic?.UploadTotalBytes);
    public string DownloadTotal => FormatTraffic(Traffic?.DownloadTotalBytes);
    public string MemoryUsage => FormatTraffic(Traffic?.MemoryBytes);
    public bool IsTunAvailable => Overview?.PlatformCapabilities.SupportsTun == true;
    public bool IsTunEnabled => Overview?.Health.TunState == ProxyTunState.Enabled;
    public bool IsSystemProxySettingsSelected => !IsTunSettingsSelected;
    public string NetworkSettingsDialogTitle => LocalizedText.Get(IsTunSettingsSelected
        ? "proxy.tun_managed_settings"
        : "proxy.system_proxy_settings");
    private ProxyTunSettingsDto TunSettings => Settings?.Tun ?? ProxyTunSettingsDto.Default;
    public IReadOnlyList<string> TunStacks { get; } = ["system", "gvisor", "mixed"];
    public string TunStack { get => TunSettings.Stack; set => SetTunSettings(TunSettings with { Stack = value }); }
    public string TunDeviceName { get => TunSettings.DeviceName; set => SetTunSettings(TunSettings with { DeviceName = value }); }
    public bool TunAutoRoute { get => TunSettings.AutoRoute; set => SetTunSettings(TunSettings with { AutoRoute = value }); }
    public bool TunStrictRoute { get => TunSettings.StrictRoute; set => SetTunSettings(TunSettings with { StrictRoute = value }); }
    public bool TunAutoDetectInterface { get => TunSettings.AutoDetectInterface; set => SetTunSettings(TunSettings with { AutoDetectInterface = value }); }
    public string TunDnsHijack { get => TunSettings.DnsHijack; set => SetTunSettings(TunSettings with { DnsHijack = value }); }
    public int TunMtu { get => TunSettings.Mtu; set => SetTunSettings(TunSettings with { Mtu = value }); }
    public string SystemProxyModeHint => LocalizedText.Get(SystemProxyEnabled
        ? "proxy.system_proxy_mode_hint_enabled"
        : "proxy.system_proxy_mode_hint_disabled");
    public string TunModeHint => LocalizedText.Get("proxy.tun_mode_hint");
    public bool IsRecoveryRequired => Overview?.Recovery.RecoveryRequired == true;
    public bool HasControllerAuthenticationFailure => Overview?.Health.ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed;
    public string ControllerAuthenticationTitle => LocalizedText.Get("proxy.controller_authentication.title");
    public string ControllerAuthenticationHint => LocalizedText.Get("proxy.controller_authentication.hint");
    public bool SystemProxyEnabled { get => Settings?.SystemProxyEnabled == true; set => SetSettings(systemProxyEnabled: value); }
    public string SystemProxyHost { get => Settings?.SystemProxyHost ?? "127.0.0.1"; set => SetSettings(systemProxyHost: value); }
    private ProxySystemProxyOptionsDto SystemProxyOptions => Settings?.SystemProxy ?? ProxySystemProxyOptionsDto.Default;
    public bool SystemProxyUsePac { get => SystemProxyOptions.UsePac; set => SetSystemProxyOptions(SystemProxyOptions with { UsePac = value }); }
    public bool SystemProxyGuardEnabled { get => SystemProxyOptions.GuardEnabled; set => SetSystemProxyOptions(SystemProxyOptions with { GuardEnabled = value }); }
    public int SystemProxyGuardIntervalSeconds { get => SystemProxyOptions.GuardIntervalSeconds; set => SetSystemProxyOptions(SystemProxyOptions with { GuardIntervalSeconds = value }); }
    public bool SystemProxyUseDefaultBypass { get => SystemProxyOptions.UseDefaultBypass; set => SetSystemProxyOptions(SystemProxyOptions with { UseDefaultBypass = value }); }
    public string SystemProxyBypassList { get => SystemProxyOptions.BypassList; set => SetSystemProxyOptions(SystemProxyOptions with { BypassList = value ?? string.Empty }); }
    public bool AllowLan { get => Settings?.AllowLan == true; set => SetSettings(allowLan: value); }
    public bool DnsEnabled { get => Settings?.DnsEnabled != false; set => SetSettings(dnsEnabled: value); }
    public bool Ipv6Enabled { get => Settings?.Ipv6Enabled != false; set => SetSettings(ipv6Enabled: value); }
    public bool UnifiedDelay { get => Settings?.UnifiedDelay == true; set => SetSettings(unifiedDelay: value); }
    public string LogLevel { get => Settings?.LogLevel ?? "warning"; set => SetSettings(logLevel: value); }
    public int MixedPort { get => Settings?.MixedPort ?? 7890; set => SetSettings(mixedPort: value); }
    public bool AllowInsecureSubscriptionSources { get => Settings?.AllowInsecureSubscriptionSources == true; set => SetSettings(allowInsecureSubscriptionSources: value); }
    public bool IsRuleRouting => RoutingMode == ProxyRoutingMode.Rule;
    public bool IsGlobalRouting => RoutingMode == ProxyRoutingMode.Global;
    public bool IsDirectRouting => RoutingMode == ProxyRoutingMode.Direct;
    public string ProxyNodeSortLabel => LocalizedText.Get(ProxyNodeSortMode switch
    {
        ProxyNodeSortMode.Name => "proxy.latency_sort_name",
        ProxyNodeSortMode.Delay => "proxy.latency_sort_delay",
        _ => "proxy.latency_sort_default",
    });
    public bool GeoDataIsConfigured => GeoData?.IsConfigured == true;
    public string GeoDataState => LocalizedText.Get(GeoDataIsConfigured ? "proxy.geodata.configured" : "proxy.geodata.not_configured");
    public IReadOnlyList<string> LogLevels { get; } = ["silent", "error", "warning", "info", "debug"];

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var overviewTask = repository.GetOverviewAsync();
            var profilesTask = repository.ListProfilesAsync();
            var subscriptionsTask = repository.ListSubscriptionsAsync();
            var settingsTask = repository.GetSettingsAsync();
            var geoDataTask = repository.GetGeoDataAsync();
            await Task.WhenAll(overviewTask, profilesTask, subscriptionsTask, settingsTask, geoDataTask);

            Overview = await overviewTask;
            Runtime = Overview.Runtime;
            Replace(Profiles, await profilesTask);
            Replace(Subscriptions, await subscriptionsTask);
            OnPropertyChanged(nameof(VisibleSubscriptions));
            Settings = await settingsTask;
            GeoData = await geoDataTask;
            // A stopped/not-yet-installed runtime cannot answer controller requests. The shell
            // must still show its install and profile pages rather than fail the entire refresh.
            await Task.WhenAll(
                LoadOptionalAsync(() => repository.ListGroupsAsync(), ReplaceGroups),
                LoadOptionalAsync(() => repository.GetRoutingModeAsync(), value => RoutingMode = value.Mode),
                LoadOptionalAsync(() => repository.ListConnectionsAsync(), values => Replace(Connections, values)),
                RefreshTrafficAsync(),
                LoadOptionalAsync(() => repository.ListLogsAsync(), values => Replace(Logs, values)),
                LoadOptionalAsync(() => repository.GetDnsStatusAsync(), value => DnsStatus = value));
            await LoadSystemProxyHostOptionsAsync();
            StatusText = Overview.Recovery.RecoveryRequired
                ? LocalizedText.Ref("proxy.status.recovery_required")
                : HasControllerAuthenticationFailure
                    ? LocalizedText.Ref("proxy.status.controller_authentication_failed")
                    : LocalizedText.Ref("proxy.status.ready", RuntimeState, HealthState);
            RaiseSummaryProperties();
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    /// <summary>Small live overview refresh; it deliberately avoids reloading profiles or logs.</summary>
    public Task RefreshTrafficAsync() => LoadOptionalAsync(() => repository.GetTrafficAsync(), value =>
        Traffic = string.IsNullOrEmpty(value.ProblemCode) ? value : null);

    [RelayCommand(CanExecute = nameof(CanStartProxy))] private Task StartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Start);
    [RelayCommand(CanExecute = nameof(CanStopProxy))] private Task StopProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Stop);
    [RelayCommand(CanExecute = nameof(CanRestartProxy))] private Task RestartProxyAsync() => LifecycleAsync(ProxyLifecycleAction.Restart);
    [RelayCommand(CanExecute = nameof(CanToggleProxy))]
    private Task ToggleProxyAsync() => LifecycleAsync(ProxyIsRunning ? ProxyLifecycleAction.Stop : ProxyLifecycleAction.Start);
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))] private Task InstallRuntimeAsync() => Installation.SubmitAsync(InstallationOperationKind.Install, new MihomoInstallationRequest(true));
    [RelayCommand(CanExecute = nameof(CanInstallRuntimeFromServerFile))]
    private async Task InstallRuntimeFromServerFileAsync()
    {
        if (RequestServerRuntimePackageAsync is not { } request) return;
        var path = await request();
        if (string.IsNullOrWhiteSpace(path)) return;
        var reference = await Installation.CreateFileReferenceAsync(path);
        if (reference is not null)
            await Installation.SubmitAsync(InstallationOperationKind.Install, new MihomoInstallationRequest(true, FileReferenceId: reference));
    }
    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private async Task ShowRuntimeDownloadAsync()
    {
        try
        {
            var download = await repository.GetManagedRuntimeDownloadAsync();
            if (download is null) { StatusText = LocalizedText.Ref("proxy.runtime_download_unavailable"); return; }
            await (ShowRuntimeDownloadUrlAsync?.Invoke(download.Url) ?? Task.CompletedTask);
        }
        catch (Exception exception) { SetFailureStatus(exception); }
    }
    [RelayCommand(CanExecute = nameof(CanInstalledRuntime))] private Task RollbackRuntimeAsync() => Installation.SubmitAsync(InstallationOperationKind.Repair, new MihomoInstallationRequest(true, Rollback: true));
    [RelayCommand(CanExecute = nameof(CanInstalledRuntime))] private Task UninstallRuntimeAsync() => Installation.SubmitAsync(InstallationOperationKind.Uninstall, new MihomoInstallationRequest(true));
    [RelayCommand(CanExecute = nameof(CanEnableTun))]
    private Task EnableTunAsync() => (SelectedProfile ?? Overview?.ActiveProfile) is { } profile
        ? QueueAsync(() => repository.EnableTunAsync(profile.Id))
        : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanTun))] private Task DisableTunAsync() => QueueAsync(() => repository.DisableTunAsync());
    [RelayCommand(CanExecute = nameof(CanTun))] private Task EmergencyDisableAsync() => QueueAsync(() => repository.EmergencyDisableTunAsync());
    [RelayCommand(CanExecute = nameof(CanToggleTun))]
    private Task ToggleTunAsync() => IsTunEnabled ? DisableTunAsync() : EnableTunAsync();
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SaveSettingsAsync()
    {
        try
        {
            IsBusy = true;
            await repository.UpdateSettingsAsync(new UpdateProxySettingsRequest(SystemProxyEnabled, AllowLan, DnsEnabled, Ipv6Enabled, UnifiedDelay, LogLevel, MixedPort, AllowInsecureSubscriptionSources, SystemProxyHost, TunSettings, SystemProxyOptions));
            StatusText = LocalizedText.Ref("proxy.status.settings_saved");
            await RefreshAsync();
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureGeoDataFromServerFile))]
    private async Task ConfigureGeoDataFromServerFileAsync()
    {
        if (RequestServerGeoDataFileAsync is not { } requestFile) return;
        var filePath = await requestFile();
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            IsBusy = true;
            await repository.ConfigureGeoDataFromServerFileAsync(filePath);
            GeoData = await repository.GetGeoDataAsync();
            StatusText = LocalizedText.Ref("proxy.status.geodata_configured");
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private async Task CreateProfileAsync()
    {
        var name = ProfileName.Trim();
        try
        {
            IsBusy = true;
            var profile = await repository.CreateProfileAsync(name, Overview?.EngineId ?? "mihomo");
            Profiles.Add(profile);
            ProfileName = string.Empty;
            StatusText = LocalizedText.Ref("proxy.status.profile_created", profile.Name);
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task UpdateAllSubscriptionsAsync()
    {
        try
        {
            IsSubscriptionRefreshActive = true;
            await QueueAsync(() => repository.RefreshAllSubscriptionsAsync());
        }
        finally { IsSubscriptionRefreshActive = false; }
    }

    [RelayCommand(CanExecute = nameof(CanViewRuntimeSubscription))]
    private async Task ViewRuntimeSubscriptionsAsync()
    {
        SelectedSubscription ??= Subscriptions.FirstOrDefault(subscription => subscription.IsActive) ?? Subscriptions.FirstOrDefault();
        if (SelectedSubscription is null) { StatusText = LocalizedText.Ref("proxy.status.subscription_none"); return; }
        try
        {
            IsBusy = true;
            var content = await repository.GetSubscriptionContentAsync(SelectedSubscription.Id);
            RuntimeSubscriptionText = content.Content;
            StatusText = LocalizedText.Ref("proxy.status.runtime_subscriptions_shown");
            ShowRuntimeSubscriptionWindow?.Invoke();
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanActivateSubscription))]
    private async Task ActivateSubscriptionAsync(ProxySubscriptionDto? subscription)
    {
        if (subscription is null || subscription.IsActive) return;
        SelectedSubscription = subscription;
        await QueueAsync(() => repository.ActivateSubscriptionAsync(subscription.Id));
    }

    [RelayCommand(CanExecute = nameof(CanImportSubscription))]
    private async Task ImportSubscriptionAsync()
    {
        try
        {
            IsBusy = true;
            var downloadRoute = ProxySubscriptionDownloadRoute.Direct;
            var options = await repository.GetSubscriptionDownloadOptionsAsync();
            if (options.SystemProxyAvailable && RequestSystemProxySubscriptionDownloadAsync is not null &&
                await RequestSystemProxySubscriptionDownloadAsync())
                downloadRoute = ProxySubscriptionDownloadRoute.SystemProxy;
            var subscription = await repository.ImportSubscriptionAsync(new ImportProxySubscriptionRequest(SubscriptionLink.Trim(), DownloadRoute: downloadRoute));
            Subscriptions.Add(subscription);
            SubscriptionLink = string.Empty;
            StatusText = LocalizedText.Ref("proxy.status.subscription_imported");
            OnPropertyChanged(nameof(VisibleSubscriptions));
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageProfile))]
    private async Task ActivateProfileAsync()
    {
        var profile = SelectedProfile!;
        try
        {
            IsBusy = true;
            var activated = await repository.ActivateProfileAsync(profile.Id);
            Overview = Overview is null ? null : Overview with { ActiveProfile = activated };
            for (var index = 0; index < Profiles.Count; index++) Profiles[index] = Profiles[index] with { IsActive = Profiles[index].Id == activated.Id };
            SelectedProfile = activated;
            StatusText = LocalizedText.Ref("proxy.status.profile_activated", activated.Name);
            RaiseSummaryProperties();
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageProfile))]
    private async Task DeleteProfileAsync()
    {
        var profile = SelectedProfile!;
        try
        {
            IsBusy = true;
            await repository.DeleteProfileAsync(profile.Id);
            Profiles.Remove(profile);
            SelectedProfile = null;
            StatusText = LocalizedText.Ref("proxy.status.profile_deleted", profile.Name);
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSelectGroup))]
    private async Task ApplyGroupSelectionAsync()
    {
        try
        {
            IsBusy = true;
            await repository.SelectGroupAsync(SelectedGroup!.Name, SelectedProxy);
            SelectedGroup.SetSelected(SelectedProxy);
            StatusText = LocalizedText.Ref("proxy.status.node_selected", SelectedProxy);
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ToggleProxyGroup(ProxyGroupItem? group)
    {
        if (group is not null) group.IsExpanded = !group.IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(CanSelectProxyNode))]
    private async Task SelectProxyNodeAsync(ProxyNodeItem? node)
    {
        if (node is null) return;
        var group = Groups.FirstOrDefault(candidate => string.Equals(candidate.Name, node.GroupName, StringComparison.Ordinal));
        if (group is null || node.IsSelected) return;
        SelectedGroup = group;
        SelectedProxy = node.Name;
        await ApplyGroupSelectionAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SetRoutingModeAsync(ProxyRoutingMode mode)
    {
        if (!Enum.IsDefined(mode) || RoutingMode == mode) return;
        try
        {
            IsBusy = true;
            await repository.SetRoutingModeAsync(mode);
            RoutingMode = mode;
            StatusText = LocalizedText.Ref("proxy.status.routing_updated");
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void CycleProxyNodeSort()
    {
        ProxyNodeSortMode = ProxyNodeSortMode switch
        {
            ProxyNodeSortMode.Default => ProxyNodeSortMode.Name,
            ProxyNodeSortMode.Name => ProxyNodeSortMode.Delay,
            _ => ProxyNodeSortMode.Default,
        };
        foreach (var group in Groups) group.SortNodes(ProxyNodeSortMode);
    }

    [RelayCommand]
    private void ToggleLatencyTestTarget() => IsLatencyTestTargetVisible = !IsLatencyTestTargetVisible;

    [RelayCommand]
    private void Navigate(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section)) NavigateRequested?.Invoke(section);
    }

    /// <summary>The overview switch is deliberately immediate, unlike the editable form which has an explicit Save button.</summary>
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ToggleSystemProxyAsync()
    {
        SystemProxyEnabled = !SystemProxyEnabled;
        await SaveSettingsAsync();
    }

    [RelayCommand]
    private Task ShowSystemProxySettingsDialogAsync() => OpenNetworkSettingsDialogAsync?.Invoke(false) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ShowTunSettingsDialogAsync() => OpenNetworkSettingsDialogAsync?.Invoke(true) ?? Task.CompletedTask;

    [RelayCommand]
    private void SelectNetworkSettingsPage(bool isTun) => IsTunSettingsSelected = isTun;

    public void SelectNetworkSettingsPageForDialog(bool isTun) => IsTunSettingsSelected = isTun;

    [RelayCommand(CanExecute = nameof(CanTestGroupSelectedProxyLatency))]
    private async Task TestGroupSelectedProxyLatencyAsync(ProxyGroupItem? group)
    {
        var node = group?.Nodes.FirstOrDefault(candidate => candidate.IsSelected);
        if (node is null) return;
        await TestLatencyAsync([node]);
    }

    [RelayCommand(CanExecute = nameof(CanTestGroupProxyLatencies))]
    private Task TestGroupProxyLatenciesAsync(ProxyGroupItem? group) => group is null
        ? Task.CompletedTask
        : TestLatencyAsync(group.Nodes.ToArray());

    [RelayCommand(CanExecute = nameof(CanManageConnection))]
    private async Task CloseConnectionAsync()
    {
        var connection = SelectedConnection!;
        try
        {
            IsBusy = true;
            await repository.CloseConnectionAsync(connection.Id);
            Connections.Remove(connection);
            SelectedConnection = null;
            OnPropertyChanged(nameof(RunningConnectionCount));
            StatusText = LocalizedText.Ref("proxy.status.connection_closed");
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    private async Task LifecycleAsync(ProxyLifecycleAction action) => await QueueAsync(() => repository.LifecycleAsync(action));
    private async Task QueueAsync(Func<Task<ProxyOperationAcceptedDto>> operation)
    {
        try
        {
            IsBusy = true;
            var accepted = await operation();
            StatusText = LocalizedText.Ref("proxy.status.operation_queued");
            await TrackOperationAsync(accepted.OperationId);
        }
        catch (Exception exception) { SetFailureStatus(exception); }
        finally { IsBusy = false; }
    }

    private async Task TrackOperationAsync(Guid operationId)
    {
        while (true)
        {
            var operation = await repository.GetOperationAsync(operationId);
            if (operation is null) { StatusText = LocalizedText.Ref("proxy.status.operation_unavailable"); return; }
            StatusText = FormatOperation(operation);
            if (operation.State is ProxyOperationState.Succeeded or ProxyOperationState.Failed or ProxyOperationState.Cancelled or ProxyOperationState.Interrupted)
            {
                if (operation.State is ProxyOperationState.Failed or ProxyOperationState.Interrupted)
                    _ = ShowPrivilegedHelperUnavailableAsyncIfNeeded(operation.ProblemCode);
                if (operation.State == ProxyOperationState.Succeeded) await RefreshAsync();
                else await LoadLogsAsync();
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private bool CanRefresh => !IsBusy;
    private bool CanManage => HasManagePermission && !IsBusy;
    private bool CanInstallRuntime => CanManage && RuntimeIsNotInstalled;
    private bool CanInstallRuntimeFromServerFile => CanInstallRuntime && RequestServerRuntimePackageAsync is not null;
    private bool CanInstalledRuntime => CanManage && RuntimeIsInstalled;
    private bool CanStartProxy => CanManage && Runtime?.State == ProxyRuntimeState.Stopped;
    private bool CanStopProxy => CanManage && Runtime?.State == ProxyRuntimeState.Running;
    private bool CanRestartProxy => CanManage && Runtime?.State is ProxyRuntimeState.Stopped or ProxyRuntimeState.Running;
    private bool CanToggleProxy => CanManage && ProxyCanToggle;
    private bool CanConfigureGeoDataFromServerFile => CanManage && RequestServerGeoDataFileAsync is not null;
    private bool CanTun => HasTunManagePermission && !IsBusy && IsTunAvailable;
    private bool CanEnableTun => CanTun && (SelectedProfile ?? Overview?.ActiveProfile) is not null;
    private bool CanToggleTun => CanTun && Overview?.Health.TunState is ProxyTunState.Disabled or ProxyTunState.Enabled
        && (IsTunEnabled || (SelectedProfile ?? Overview?.ActiveProfile) is not null);
    private bool CanCreateProfile => CanManage && !IsBusy && !string.IsNullOrWhiteSpace(ProfileName);
    private bool CanManageProfile => CanManage && SelectedProfile is not null;
    private bool CanViewRuntimeSubscription => !IsBusy && Subscriptions.Count > 0;
    private bool CanActivateSubscription(ProxySubscriptionDto? subscription) => CanManage && !IsBusy && subscription is { IsActive: false };
    private bool CanImportSubscription => CanManage && !IsBusy && !string.IsNullOrWhiteSpace(SubscriptionLink);
    private bool CanSelectGroup => CanManage && !IsBusy && SelectedGroup is not null && !string.IsNullOrWhiteSpace(SelectedProxy);
    private bool CanSelectProxyNode(ProxyNodeItem? node) => CanManage && !IsBusy && node is { IsSelected: false };
    private bool CanTestGroupSelectedProxyLatency(ProxyGroupItem? group) => CanManage && !IsLatencyTesting && group?.Selected is not null && IsValidLatencyTestTarget;
    private bool CanTestGroupProxyLatencies(ProxyGroupItem? group) => CanManage && !IsLatencyTesting && group?.Nodes.Count > 0 && IsValidLatencyTestTarget;
    private bool CanManageConnection => CanManage && !IsBusy && SelectedConnection is not null;
    private bool IsValidLatencyTestTarget => Uri.TryCreate(LatencyTestTarget, UriKind.Absolute, out var target)
        && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) && !target.IsLoopback;

    private void SetSettings(bool? systemProxyEnabled = null, bool? allowLan = null, bool? dnsEnabled = null, bool? ipv6Enabled = null,
        bool? unifiedDelay = null, string? logLevel = null, int? mixedPort = null, bool? allowInsecureSubscriptionSources = null,
        string? systemProxyHost = null, ProxyTunSettingsDto? tun = null, ProxySystemProxyOptionsDto? systemProxy = null)
    {
        var current = Settings ?? new ProxySettingsDto(false, false, true, true, false, "warning", 7890);
        Settings = current with
        {
            SystemProxyEnabled = systemProxyEnabled ?? current.SystemProxyEnabled,
            AllowLan = allowLan ?? current.AllowLan,
            DnsEnabled = dnsEnabled ?? current.DnsEnabled,
            Ipv6Enabled = ipv6Enabled ?? current.Ipv6Enabled,
            UnifiedDelay = unifiedDelay ?? current.UnifiedDelay,
            LogLevel = logLevel ?? current.LogLevel,
            MixedPort = mixedPort ?? current.MixedPort,
            AllowInsecureSubscriptionSources = allowInsecureSubscriptionSources ?? current.AllowInsecureSubscriptionSources,
            SystemProxyHost = string.IsNullOrWhiteSpace(systemProxyHost) ? current.SystemProxyHost : systemProxyHost.Trim(),
            Tun = tun ?? current.Tun ?? ProxyTunSettingsDto.Default,
            SystemProxy = systemProxy ?? current.SystemProxy ?? ProxySystemProxyOptionsDto.Default,
        };
    }

    private void SetTunSettings(ProxyTunSettingsDto tun) => SetSettings(tun: tun);
    private void SetSystemProxyOptions(ProxySystemProxyOptionsDto options) => SetSettings(systemProxy: options);

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsLatencyTestingChanged(bool value)
    {
        TestGroupSelectedProxyLatencyCommand.NotifyCanExecuteChanged();
        TestGroupProxyLatenciesCommand.NotifyCanExecuteChanged();
    }
    partial void OnHasManagePermissionChanged(bool value) => NotifyCommands();
    partial void OnHasTunManagePermissionChanged(bool value) => NotifyCommands();
    partial void OnSelectedProfileChanged(ProxyProfileDto? value) { EnableTunCommand.NotifyCanExecuteChanged(); ActivateProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedSubscriptionChanged(ProxySubscriptionDto? value) => ViewRuntimeSubscriptionsCommand.NotifyCanExecuteChanged();
    partial void OnSubscriptionLinkChanged(string value) => ImportSubscriptionCommand.NotifyCanExecuteChanged();
    partial void OnRuntimeChanged(ProxyRuntimeDto? value)
    {
        OnPropertyChanged(nameof(ProxyIsRunning)); OnPropertyChanged(nameof(ProxyCanToggle));
        OnPropertyChanged(nameof(ProxyActionText)); OnPropertyChanged(nameof(ProxyStateLabel));
        OnPropertyChanged(nameof(RuntimeIsInstalled)); OnPropertyChanged(nameof(RuntimeIsNotInstalled));
        OnPropertyChanged(nameof(RuntimeInstalledVersion)); OnPropertyChanged(nameof(RuntimeAvailableVersion));
        OnPropertyChanged(nameof(RuntimeServiceMode));
        StartProxyCommand.NotifyCanExecuteChanged(); StopProxyCommand.NotifyCanExecuteChanged(); RestartProxyCommand.NotifyCanExecuteChanged();
        ToggleProxyCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged(); ShowRuntimeDownloadCommand.NotifyCanExecuteChanged();
        RollbackRuntimeCommand.NotifyCanExecuteChanged(); UninstallRuntimeCommand.NotifyCanExecuteChanged();
    }
    partial void OnGeoDataChanged(ProxyGeoDataDto? value)
    {
        OnPropertyChanged(nameof(GeoDataIsConfigured));
        OnPropertyChanged(nameof(GeoDataState));
    }
    partial void OnSettingsChanged(ProxySettingsDto? value)
    {
        OnPropertyChanged(nameof(SystemProxyEnabled)); OnPropertyChanged(nameof(AllowLan)); OnPropertyChanged(nameof(DnsEnabled));
        OnPropertyChanged(nameof(Ipv6Enabled)); OnPropertyChanged(nameof(UnifiedDelay)); OnPropertyChanged(nameof(LogLevel)); OnPropertyChanged(nameof(MixedPort));
        OnPropertyChanged(nameof(AllowInsecureSubscriptionSources));
        OnPropertyChanged(nameof(SystemProxyHost));
        OnPropertyChanged(nameof(SystemProxyUsePac)); OnPropertyChanged(nameof(SystemProxyGuardEnabled));
        OnPropertyChanged(nameof(SystemProxyGuardIntervalSeconds)); OnPropertyChanged(nameof(SystemProxyUseDefaultBypass));
        OnPropertyChanged(nameof(SystemProxyBypassList));
        OnPropertyChanged(nameof(SystemProxyState));
        OnPropertyChanged(nameof(SystemProxyEndpoint));
        OnPropertyChanged(nameof(SystemProxyModeHint));
        OnPropertyChanged(nameof(TunStack)); OnPropertyChanged(nameof(TunDeviceName)); OnPropertyChanged(nameof(TunAutoRoute));
        OnPropertyChanged(nameof(TunStrictRoute)); OnPropertyChanged(nameof(TunAutoDetectInterface)); OnPropertyChanged(nameof(TunDnsHijack)); OnPropertyChanged(nameof(TunMtu));
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsTunSettingsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSystemProxySettingsSelected));
        OnPropertyChanged(nameof(NetworkSettingsDialogTitle));
    }
    partial void OnTrafficChanged(ProxyTrafficDto? value)
    {
        OnPropertyChanged(nameof(UploadRate)); OnPropertyChanged(nameof(DownloadRate));
        OnPropertyChanged(nameof(UploadTotal)); OnPropertyChanged(nameof(DownloadTotal)); OnPropertyChanged(nameof(MemoryUsage));
    }
    partial void OnSelectedGroupChanged(ProxyGroupItem? value) { SelectedProxy = value?.Selected ?? string.Empty; ApplyGroupSelectionCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedProxyChanged(string value) => ApplyGroupSelectionCommand.NotifyCanExecuteChanged();
    partial void OnRoutingModeChanged(ProxyRoutingMode value)
    {
        OnPropertyChanged(nameof(IsRuleRouting));
        OnPropertyChanged(nameof(IsGlobalRouting));
        OnPropertyChanged(nameof(IsDirectRouting));
    }
    partial void OnProxyNodeSortModeChanged(ProxyNodeSortMode value) => OnPropertyChanged(nameof(ProxyNodeSortLabel));
    partial void OnLatencyTestTargetChanged(string value)
    {
        TestGroupSelectedProxyLatencyCommand.NotifyCanExecuteChanged();
        TestGroupProxyLatenciesCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedConnectionChanged(ProxyConnectionDto? value) => CloseConnectionCommand.NotifyCanExecuteChanged();
    partial void OnProfileNameChanged(string value) => CreateProfileCommand.NotifyCanExecuteChanged();
    partial void OnSelectedOverviewProxyNodeChanged(ProxyNodeItem? value)
    {
        if (!_updatingOverviewProxySelection && value is { IsSelected: false }) _ = SelectProxyNodeAsync(value);
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged(); StartProxyCommand.NotifyCanExecuteChanged(); StopProxyCommand.NotifyCanExecuteChanged(); RestartProxyCommand.NotifyCanExecuteChanged(); ToggleProxyCommand.NotifyCanExecuteChanged();
        InstallRuntimeCommand.NotifyCanExecuteChanged(); InstallRuntimeFromServerFileCommand.NotifyCanExecuteChanged(); ShowRuntimeDownloadCommand.NotifyCanExecuteChanged(); RollbackRuntimeCommand.NotifyCanExecuteChanged(); UninstallRuntimeCommand.NotifyCanExecuteChanged();
        ConfigureGeoDataFromServerFileCommand.NotifyCanExecuteChanged();
        EnableTunCommand.NotifyCanExecuteChanged(); DisableTunCommand.NotifyCanExecuteChanged(); EmergencyDisableCommand.NotifyCanExecuteChanged(); ToggleTunCommand.NotifyCanExecuteChanged();
        ToggleSystemProxyCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged(); ActivateProfileCommand.NotifyCanExecuteChanged(); DeleteProfileCommand.NotifyCanExecuteChanged();
        UpdateAllSubscriptionsCommand.NotifyCanExecuteChanged();
        ImportSubscriptionCommand.NotifyCanExecuteChanged(); ViewRuntimeSubscriptionsCommand.NotifyCanExecuteChanged(); ActivateSubscriptionCommand.NotifyCanExecuteChanged();
        ApplyGroupSelectionCommand.NotifyCanExecuteChanged(); SelectProxyNodeCommand.NotifyCanExecuteChanged(); SetRoutingModeCommand.NotifyCanExecuteChanged();
        TestGroupSelectedProxyLatencyCommand.NotifyCanExecuteChanged(); TestGroupProxyLatenciesCommand.NotifyCanExecuteChanged(); CloseConnectionCommand.NotifyCanExecuteChanged(); SaveSettingsCommand.NotifyCanExecuteChanged();
    }
    private void RaiseSummaryProperties()
    {
        OnPropertyChanged(nameof(RuntimeVersion)); OnPropertyChanged(nameof(RuntimeState)); OnPropertyChanged(nameof(RuntimeInstalledVersion)); OnPropertyChanged(nameof(RuntimeAvailableVersion));
        OnPropertyChanged(nameof(HealthState)); OnPropertyChanged(nameof(TunState));
        OnPropertyChanged(nameof(DnsState)); OnPropertyChanged(nameof(ActiveProfileName)); OnPropertyChanged(nameof(IsTunAvailable)); OnPropertyChanged(nameof(IsRecoveryRequired)); OnPropertyChanged(nameof(RunningConnectionCount));
        OnPropertyChanged(nameof(HasControllerAuthenticationFailure)); OnPropertyChanged(nameof(ControllerAuthenticationTitle)); OnPropertyChanged(nameof(ControllerAuthenticationHint));
        OnPropertyChanged(nameof(ActiveSubscription)); OnPropertyChanged(nameof(ActiveSubscriptionName)); OnPropertyChanged(nameof(ActiveSubscriptionUpdatedAt));
        OnPropertyChanged(nameof(CurrentProxyGroupItem)); OnPropertyChanged(nameof(CurrentProxyGroupName)); OnPropertyChanged(nameof(CurrentProxyNodeName));
        OnPropertyChanged(nameof(OverviewProxyNodes));
        OnPropertyChanged(nameof(SystemProxyState)); OnPropertyChanged(nameof(TunOverviewState));
        OnPropertyChanged(nameof(IsTunEnabled)); OnPropertyChanged(nameof(SystemProxyModeHint)); OnPropertyChanged(nameof(TunModeHint));
        OnPropertyChanged(nameof(ServerOperatingSystem)); OnPropertyChanged(nameof(StartupState));
        OnPropertyChanged(nameof(ConfigurationUpdatedAt)); OnPropertyChanged(nameof(ClientVersion));
        EnableTunCommand.NotifyCanExecuteChanged(); DisableTunCommand.NotifyCanExecuteChanged(); EmergencyDisableCommand.NotifyCanExecuteChanged(); ToggleTunCommand.NotifyCanExecuteChanged();
    }
    private static async Task LoadOptionalAsync<T>(Func<Task<T>> load, Action<T> apply)
    {
        try { apply(await load()); }
        // Controller-specific pages simply remain empty until a runtime is healthy.
        catch (ProxyRequestException) { }
        catch (HttpRequestException) { }
    }
    private async Task LoadLogsAsync()
    {
        try { Replace(Logs, await repository.ListLogsAsync()); }
        catch (ProxyRequestException) { }
        catch (HttpRequestException) { }
    }
    public async Task LoadSystemProxyHostOptionsAsync()
    {
        if (systemMonitor is null) return;
        try
        {
            var addresses = await systemMonitor.GetNetworkAddressesAsync();
            var values = new[] { "127.0.0.1" }
                .Concat(addresses.Where(address => string.Equals(address.Family, "IPv4", StringComparison.OrdinalIgnoreCase)).Select(address => address.Address))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            SystemProxyHostOptions.Clear();
            foreach (var value in values) SystemProxyHostOptions.Add(value);
            if (!SystemProxyHostOptions.Contains(SystemProxyHost, StringComparer.Ordinal)) SystemProxyHostOptions.Add(SystemProxyHost);
        }
        catch (Exception) { }
    }
    private static LocalizedStatus FormatOperation(ProxyOperationDto operation)
    {
        if (operation.State == ProxyOperationState.Succeeded)
            return LocalizedText.Ref(operation.Kind switch
            {
                "runtime.install" or "runtime.install_from_file" => "proxy.operation.completed.runtime_install",
                "runtime.rollback" => "proxy.operation.completed.runtime_rollback",
                "runtime.uninstall" => "proxy.operation.completed.runtime_uninstall",
                "lifecycle.start" => "proxy.operation.completed.lifecycle_start",
                "lifecycle.stop" => "proxy.operation.completed.lifecycle_stop",
                "lifecycle.restart" => "proxy.operation.completed.lifecycle_restart",
                "tun.enable" => "proxy.operation.completed.tun_enable",
                "tun.disable" or "tun.emergency-disable" => "proxy.operation.completed.tun_disable",
                "subscription.refresh" or "subscription.refresh_all" => "proxy.operation.completed.subscription_refresh",
                "subscription.activate" => "proxy.operation.completed.subscription_activate",
                _ => "proxy.operation.completed.generic",
            });
        if (operation.State is ProxyOperationState.Failed or ProxyOperationState.Interrupted)
            return LocalizedText.Ref("proxy.status.failed", FormatProblemCode(operation.ProblemCode));
        if (operation.State == ProxyOperationState.Cancelled)
            return LocalizedText.Ref("proxy.operation.cancelled");
        var key = "proxy.operation." + operation.Stage;
        var stage = LocalizedText.Get(key);
        return stage == key ? LocalizedText.Ref("proxy.operation.running") : LocalizedStatus.Literal(stage);
    }
    private void SetFailureStatus(Exception exception)
    {
        var problemCode = exception is ProxyRequestException request ? request.ProblemCode : exception.Message;
        StatusText = LocalizedText.Ref("proxy.status.failed", FormatProblemCode(problemCode));
        _ = ShowPrivilegedHelperUnavailableAsyncIfNeeded(problemCode);
    }
    private static string FormatProblemCode(string? problemCode)
    {
        if (PrivilegedHelperProblemText.TryFormat(problemCode, out var helperMessage)) return helperMessage;
        if (string.IsNullOrWhiteSpace(problemCode)) return LocalizedText.Get("proxy.problem.unknown");
        var key = "proxy.problem." + problemCode.Replace("proxy.", string.Empty, StringComparison.Ordinal).Replace('.', '_');
        var localized = LocalizedText.Get(key);
        return localized == key ? problemCode : localized;
    }
    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        PrivilegedHelperProblemText.TryFormat(problemCode, out _)
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;
    private static string LocalizeEnum(string prefix, Enum value)
    {
        var name = value.ToString();
        var suffix = string.Concat(name.Select((character, index) => index > 0 && char.IsUpper(character)
            ? "_" + character.ToString().ToLowerInvariant()
            : character.ToString().ToLowerInvariant()));
        var key = prefix + "." + suffix;
        var localized = LocalizedText.Get(key);
        return localized == key ? name : localized;
    }
    private void ReplaceGroups(IReadOnlyList<ProxyGroupDto> groups)
    {
        var expansion = Groups.ToDictionary(group => group.Name, group => group.IsExpanded, StringComparer.Ordinal);
        var selectedGroupName = SelectedGroup?.Name;
        var selectedProxyName = SelectedProxy;
        Groups.Clear();
        SelectedGroup = null;
        SelectedProxy = string.Empty;
        var visibleGroups = groups.Where(group => !string.Equals(group.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var group in visibleGroups)
        {
            // Expansion is a view-only preference for the current session. New groups must
            // never open automatically, so a refreshed proxy page stays compact by default.
            var isExpanded = expansion.TryGetValue(group.Name, out var expanded) && expanded;
            var item = new ProxyGroupItem(group.Name, group.Type, group.Selected, group.Proxies, isExpanded);
            item.SortNodes(ProxyNodeSortMode);
            Groups.Add(item);
        }
        if (selectedGroupName is not null && Groups.FirstOrDefault(group => string.Equals(group.Name, selectedGroupName, StringComparison.Ordinal)) is { } selectedGroup)
        {
            SelectedGroup = selectedGroup;
            SelectedProxy = selectedGroup.Nodes.Any(node => string.Equals(node.Name, selectedProxyName, StringComparison.Ordinal))
                ? selectedProxyName
                : selectedGroup.Selected ?? string.Empty;
        }
        _updatingOverviewProxySelection = true;
        SelectedOverviewProxyNode = CurrentProxyGroupItem?.Nodes.FirstOrDefault(node => node.IsSelected)
            ?? CurrentProxyGroupItem?.Nodes.FirstOrDefault();
        _updatingOverviewProxySelection = false;
        TestGroupSelectedProxyLatencyCommand.NotifyCanExecuteChanged();
        TestGroupProxyLatenciesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentProxyGroupItem));
        OnPropertyChanged(nameof(CurrentProxyGroupName));
        OnPropertyChanged(nameof(CurrentProxyNodeName));
        OnPropertyChanged(nameof(OverviewProxyNodes));
    }

    private async Task TestLatencyAsync(IReadOnlyList<ProxyNodeItem> nodes)
    {
        if (nodes.Count == 0 || IsLatencyTesting) return;
        try
        {
            IsLatencyTesting = true;
            foreach (var node in nodes) node.IsTesting = true;
            using var concurrency = new SemaphoreSlim(Math.Min(6, nodes.Count));
            await Task.WhenAll(nodes.Select(node => TestNodeLatencyAsync(node, LatencyTestTarget, concurrency)));
            foreach (var group in Groups) group.SortNodes(ProxyNodeSortMode);
            StatusText = LocalizedText.Ref("proxy.status.latency_tested");
        }
        finally { IsLatencyTesting = false; }
    }

    private async Task TestNodeLatencyAsync(ProxyNodeItem node, string target, SemaphoreSlim concurrency)
    {
        await concurrency.WaitAsync();
        try
        {
            var delay = await repository.TestProxyDelayAsync(node.GroupName, node.Name, new TestProxyDelayRequest(target));
            node.SetDelay(delay.DelayMilliseconds, delay.TimedOut);
        }
        catch { node.SetDelay(null, true); }
        finally
        {
            concurrency.Release();
            node.IsTesting = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private static string FormatTraffic(long? bytes, string suffix = "")
    {
        if (bytes is null) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes.Value);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        var digits = unit == 0 ? "0" : value >= 100 ? "0" : "0.0";
        return value.ToString(digits, System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit] + suffix;
    }
}
