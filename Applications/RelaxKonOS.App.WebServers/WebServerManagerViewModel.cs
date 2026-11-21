using System.Globalization;
using Avalonia.Utilities;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.App.WebServers;

/// <summary>
/// Window-local web server manager state. Discovery probes the host for Nginx (and future
/// providers); integrate/reload are explicitly confirmed, marker-owned operations polled to
/// a terminal state. The server never accepts shell text or elevation credentials over HTTP.
/// </summary>
public sealed partial class WebServerManagerViewModel : ObservableObject
{
    public string InstancesPageTitle => Text("webservers.current.server_management");
    public string CurrentLanguage => _language.CurrentLanguage;
    public string ActionsTitle => Text("webservers.current.server_management");
    public bool IsComponentCenter { get; set; }
    public bool ShowInstanceSelection => IsComponentCenter;
    /// <summary>Nginx installation lives in Component Center; settings only observes and navigates.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectCurrentNginx))] private async Task SelectCurrentNginxAsync()
    {
        if (!CanSelectCurrentNginx || SelectedServer is not { } server) return;
        IsLoading = true;
        try
        {
            await _client.SelectCurrentNginxAsync(new(server.Id));
            if (_windowConnection.IsCurrent)
                StatusText = Status("webservers.current.saved");
        }
        catch { StatusText = Status("webservers.current.failed"); }
        finally { IsLoading = false; }
    }

    public bool CanSelectCurrentNginx => IsComponentCenter && HasManagePermission && SelectedServer is not null && !IsLoading && !IsOperationRunning;

    public async Task SynchronizeCurrentNginxAsync()
    {
        if (IsComponentCenter || !CanRefresh) return;
        try
        {
            var current = await _client.GetCurrentNginxAsync();
            if (!HasReadPermission) return;
            if (SelectedServer?.Id == current.ServerId) return;
            // Invalidating the target clears the old site's facts before resolving the new instance.
            SelectedServer = null;
            await RefreshAsync();
        }
        catch { SelectedServer = null; StatusText = Status("webservers.current.failed"); }
    }

    private Action? _dismissRuntimeFeedback;
    private Func<Task>? _uninstallRuntimeAsync;

    public void AttachManagedRuntimeActions(Action dismissFeedback, Func<Task> uninstallAsync)
    {
        _dismissRuntimeFeedback = dismissFeedback;
        _uninstallRuntimeAsync = uninstallAsync;
        UninstallManagedCommand.NotifyCanExecuteChanged();
    }

    private readonly IServerWebServers _client;
    private readonly IServerCertificates _certificates;
    private readonly HostPlatformKind? _serverPlatform;
    private readonly IAppPermissionScope _permissions;
    private readonly IWebServerAuthorization _authorization;
    private readonly ISystemLanguage _language;

    public Func<string?, string?>? FormatHostProblem { get; set; }
    private readonly IExternalServerConnection _windowConnection;
    private readonly IDisposable _connectionObservation;
    private long _siteTargetVersion;
    private long _siteSelectionVersion;
    private bool _siteEditorOpen;
    private long _siteEditorTargetVersion;
    private string? _siteEditorSiteId;
    private WebServerSiteDto? _siteEditorSnapshot;
    private CancellationTokenSource? _siteSaveCancellation;
    public bool SiteEditorTargetChanged => _siteEditorOpen && (_siteEditorTargetVersion != _siteTargetVersion
        || SelectedSite?.Id != _siteEditorSiteId || !_windowConnection.IsCurrent);
    public string SiteEditorTargetChangeText => Text(_windowConnection.IsCurrent
        ? "webservers.site.editor_target_changed" : "webservers.site.editor_connection_changed");

    public void BeginSiteEditing()
    {
        _siteEditorOpen = true;
        _siteEditorTargetVersion = _siteTargetVersion;
        _siteEditorSiteId = SelectedSite?.Id;
        _siteEditorSnapshot = SelectedSite;
        OnPropertyChanged(nameof(SiteEditorTargetChanged));
        OnPropertyChanged(nameof(SiteEditorTargetChangeText));
        SaveSiteCommand.NotifyCanExecuteChanged();
    }

    public void EndSiteEditing()
    {
        _siteSaveCancellation?.Cancel();
        _siteEditorOpen = false;
        _siteEditorSnapshot = null;
        OnPropertyChanged(nameof(SiteEditorTargetChanged));
        OnPropertyChanged(nameof(SiteEditorTargetChangeText));
        SaveSiteCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty] private bool _isOperationLogExpanded;
    [ObservableProperty] private string _operationLog = string.Empty;
    partial void OnOperationTextChanged(WebServerStatusText value)
    {
        var text = value.Resolve();
        if (string.IsNullOrWhiteSpace(text) || text == _lastOperationLogText) return;
        _lastOperationLogText = text;
        OperationLog = string.Join(Environment.NewLine,
            OperationLog.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Append($"[{DateTime.Now:HH:mm:ss}] {text}").TakeLast(200));
    }
    private string _lastOperationLogText = string.Empty;
    private CancellationTokenSource? _operationCts;

    public WebServerManagerViewModel(IServerWebServers client, IServerCertificates certificates, IExternalServerConnection connection, HostPlatformKind? serverPlatform,
        IAppPermissionScope permissions, IWebServerAuthorization authorization, ISystemLanguage language)
    {
        _client = client;
        _language = language;
        _statusText = Status("webservers.status.loading");
        WeakEventHandlerManager.Subscribe<ISystemLanguage, SystemLanguageChangedEventArgs, WebServerManagerViewModel>(
            language, nameof(ISystemLanguage.LanguageChanged), OnLanguageChanged);
        _certificates = certificates;
        _serverPlatform = serverPlatform;
        _permissions = permissions;
        _authorization = authorization;
        _windowConnection = connection;
        SiteCertificateSources =
        [
            new(SiteCertificateSource.Managed, Text("webservers.site.certificate_source.managed")),
            new(SiteCertificateSource.ServerFiles, Text("webservers.site.certificate_source.files")),
        ];
        SelectedSiteCertificateSource = SiteCertificateSources[0];
        _connectionObservation = connection.ObserveInvalidation(OnConnectionInvalidated);
    }

    public ObservableCollection<WebServerDto> Servers { get; } = [];
    public ObservableCollection<WebServerIntegrationCandidateDto> IntegrationCandidates { get; } = [];
    public ObservableCollection<WebServerStatusDto> Statuses { get; } = [];
    public ObservableCollection<WebServerSiteDto> Sites { get; } = [];
    public ObservableCollection<CertificateDto> Certificates { get; } = [];
    public ObservableCollection<WebServerSiteBindingEditor> SiteBindings { get; } = [];
    public ObservableCollection<WebServerProxyRouteEditor> SiteRoutes { get; } = [];
    public IReadOnlyList<SiteCertificateSourceOption> SiteCertificateSources { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableAcmeHttp01Command), nameof(StartManagedCommand), nameof(StopCommand), nameof(ToggleManagedCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    [NotifyPropertyChangedFor(nameof(IsIntegratedServer), nameof(IsManagedServer), nameof(IsIntegratedOrManagedServer), nameof(IsManagedServerRunning), nameof(ManagementHint), nameof(ManagedLifecycleActionText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSitesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectCurrentNginxCommand))]
    private WebServerDto? _selectedServer;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(IntegrateCommand))]
    private WebServerIntegrationCandidateDto? _selectedIntegrationCandidate;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(EditSiteCommand))]
    private WebServerSiteDto? _selectedSite;
    [ObservableProperty] private WebServerStatusText _statusText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private WebServerStatusText _operationText = string.Empty;
    [ObservableProperty] private WebServerStatusText _testResultText = string.Empty;
    [ObservableProperty] private WebServerStatusText _selectedStatusText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleManagedCommand))]
    [NotifyPropertyChangedFor(nameof(IsManagedServerRunning), nameof(ManagedLifecycleActionText), nameof(ManagedRuntimeStateLabel), nameof(ManagedRuntimeStateDescription))]
    private WebServerRuntimeState _selectedRuntimeState = WebServerRuntimeState.Unknown;
    [ObservableProperty] private string _siteName = string.Empty;
    [ObservableProperty] private string _siteBindingsBatch = string.Empty;
    [ObservableProperty] private string _siteRootPath = string.Empty;
    [ObservableProperty] private bool _siteGrantNginxReadAccess;
    [ObservableProperty] private bool _siteSpaFallback = true;
    [ObservableProperty] private bool _siteHttpsEnabled;
    [ObservableProperty] private bool _siteRedirectHttpToHttps = true;
    [ObservableProperty] private bool _siteIpv6Enabled;
    [ObservableProperty] private CertificateDto? _selectedSiteCertificate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsManagedCertificateSource), nameof(IsServerFileCertificateSource))]
    private SiteCertificateSourceOption? _selectedSiteCertificateSource;
    [ObservableProperty] private string _siteCertificatePath = string.Empty;
    [ObservableProperty] private string _sitePrivateKeyPath = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSitesCommand), nameof(RefreshCommand), nameof(DiscoverCommand))]
    private bool _isSavingSite;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    private bool _siteFactsAvailable;
    private long _siteReadVersion;
    [ObservableProperty] private WebServerStatusText _siteStatusText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(IntegrateCommand), nameof(EnableAcmeHttp01Command), nameof(StartManagedCommand), nameof(StopCommand), nameof(ToggleManagedCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSitesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectCurrentNginxCommand))]
    private bool _isLoading;
    // Every action uses IsOperationRunning in its CanExecute predicate. Keep the command state
    // in sync before and after polling, otherwise controls can retain a stale disabled state.
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(IntegrateCommand), nameof(EnableAcmeHttp01Command), nameof(StartManagedCommand), nameof(StopCommand), nameof(ToggleManagedCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand), nameof(CancelOperationCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshSitesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectCurrentNginxCommand))]
    private bool _isOperationRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsManagedInstallAvailable))]
    private bool _hasManagedInstallation;

    private Guid? _currentOperationId;

    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    public bool IsWindowsServer => _serverPlatform == HostPlatformKind.Windows;
    public bool IsLinuxServer => _serverPlatform == HostPlatformKind.Linux;
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationText);
    public bool IsManagedCertificateSource => SelectedSiteCertificateSource?.Value != SiteCertificateSource.ServerFiles;
    public bool IsServerFileCertificateSource => SelectedSiteCertificateSource?.Value == SiteCertificateSource.ServerFiles;
    public bool IsIntegratedServer => SelectedServer?.ManagementMode == WebServerManagementMode.Integrated;
    public bool IsManagedServer => SelectedServer?.ManagementMode == WebServerManagementMode.Managed;
    public bool IsIntegratedOrManagedServer => IsIntegratedServer || IsManagedServer;
    public bool IsManagedServerRunning => IsManagedServer && SelectedRuntimeState == WebServerRuntimeState.Running;
    public bool IsManagedInstallAvailable => !HasManagedInstallation;
    public string ManagedLifecycleActionText => IsManagedServerRunning
        ? Text("webservers.action.stop")
        : Text("webservers.action.start");
    public string ManagedRuntimeStateLabel => RuntimeStateText(SelectedRuntimeState);
    public string ManagedRuntimeStateDescription => SelectedRuntimeState switch
    {
        WebServerRuntimeState.Running => Text("webservers.runtime.running_hint"),
        WebServerRuntimeState.Stopped => Text("webservers.runtime.stopped_hint"),
        _ => Text("webservers.runtime.unknown_hint"),
    };
    public string ManagementHint => !IsComponentCenter ? Text("webservers.current.management_hint") : SelectedServer?.ManagementMode switch
    {
        WebServerManagementMode.Integrated => Text("webservers.management_hint.integrated"),
        WebServerManagementMode.Managed => Text("webservers.management_hint.managed"),
        _ => Text("webservers.management_hint.none"),
    };

    /// <summary>Supplied by the application shell so the view model never constructs UI directly.</summary>
    public Func<Task<bool>>? RequestIntegrationConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedUninstallConfirmationAsync { get; set; }
    /// <summary>Routes a known static-site directory into RemoteExplorer.</summary>
    public Func<string, Task>? OpenFileBrowserAtPathAsync { get; set; }
    /// <summary>Opens the RemoteExplorer picker for certificate or key files on the server.</summary>
    public Func<bool, Task<string?>>? RequestServerCertificateFileAsync { get; set; }
    /// <summary>Opens the RemoteExplorer folder picker for a static site's server-side root.</summary>
    public Func<Task<string?>>? RequestSiteRootDirectoryAsync { get; set; }
    /// <summary>Provided by the application shell to keep the editor in a modal dialog.</summary>
    public Func<bool, Task>? ShowSiteEditorAsync { get; set; }
    /// <summary>Set only while the site editor dialog is open.</summary>
    public Func<Task>? CloseSiteEditorAsync { get; set; }
    /// <summary>Set while the editor is open so validation errors can appear above the modal form.</summary>
    public Func<string, Task>? ShowSiteSaveErrorAsync { get; set; }

    public async Task StartAsync()
    {
        await RefreshAsync();
        await LoadCertificatesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            Servers.Clear();
            IntegrationCandidates.Clear();
            Statuses.Clear();
            SelectedServer = null;
            SelectedIntegrationCandidate = null;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            HasManagedInstallation = false;
            StatusText = Status("webservers.permission.read_required");
            return;
        }

        IsLoading = true;
        try
        {
            var servers = await _client.ListAsync() ?? [];
            var candidates = await _client.ListIntegrationCandidatesAsync() ?? [];
            var current = await _client.GetCurrentNginxAsync();
            if (!HasReadPermission) return;
            ApplyDiscoveryResults(servers, candidates);
            if (!IsComponentCenter || current is not null)
            {
                SelectedServer = Servers.FirstOrDefault(server => server.Id == current?.ServerId);
                if (!IsComponentCenter) StatusText = Status(SelectedServer is null ? "webservers.current.unavailable" : "webservers.current.ready");
            }
        }
        catch (Exception)
        {
            Servers.Clear();
            IntegrationCandidates.Clear();
            Statuses.Clear();
            SelectedServer = null;
            SelectedIntegrationCandidate = null;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            HasManagedInstallation = false;
            StatusText = Status("webservers.status.failed", Text("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        IsLoading = true;
        StatusText = Status("webservers.discover.running");
        try
        {
            var servers = await _client.DiscoverAsync() ?? [];
            var candidates = await _client.ListIntegrationCandidatesAsync() ?? [];
            var current = await _client.GetCurrentNginxAsync();
            if (!HasReadPermission) return;
            ApplyDiscoveryResults(servers, candidates);
            if (!IsComponentCenter || current is not null)
            {
                SelectedServer = Servers.FirstOrDefault(server => server.Id == current?.ServerId);
                if (!IsComponentCenter) StatusText = Status(SelectedServer is null ? "webservers.current.unavailable" : "webservers.current.ready");
            }
        }
        catch (Exception)
        {
            StatusText = Status("webservers.discover.failed", Text("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    private void ApplyDiscoveryResults(
        IReadOnlyList<WebServerDto> servers,
        IReadOnlyList<WebServerIntegrationCandidateDto> candidates)
    {
        Servers.Clear();
        IntegrationCandidates.Clear();
        Statuses.Clear();
        SelectedServer = null;
        SelectedIntegrationCandidate = null;
        SelectedRuntimeState = WebServerRuntimeState.Unknown;
        SelectedStatusText = string.Empty;

        foreach (var server in servers) Servers.Add(server);
        foreach (var candidate in candidates) IntegrationCandidates.Add(candidate);

        var managedCount = servers.Count(server => server.ManagementMode == WebServerManagementMode.Managed);
        var integratedCount = servers.Count(server => server.ManagementMode == WebServerManagementMode.Integrated);
        HasManagedInstallation = managedCount > 0;
        StatusText = IsLinuxServer && !IsComponentCenter
            ? Status(SelectedServer is null ? "webservers.current.unavailable" : "webservers.current.ready")
            : Status(
            "webservers.discovery.summary",
            servers.Count + candidates.Count,
            managedCount,
            integratedCount,
            candidates.Count);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshStatus))]
    private async Task RefreshStatusAsync()
    {
        if (!HasReadPermission) return;
        var server = SelectedServer;
        if (server is null) return;
        IsLoading = true;
        try
        {
            var status = await _client.GetStatusAsync(server.Id);
            // A selection can change while the host request is in flight. Do not paint the
            // previous instance's process state onto the newly selected instance.
            if (SelectedServer?.Id != server.Id) return;
            SelectedRuntimeState = status?.RuntimeState ?? WebServerRuntimeState.Unknown;
            SelectedStatusText = status is null
                ? Status("webservers.status.unavailable")
                : string.IsNullOrWhiteSpace(status.ProblemCode)
                    ? WebServerStatusText.Literal(RuntimeStateText(status.RuntimeState))
                    : Status("webservers.status.detail", RuntimeStateText(status.RuntimeState), ProblemText(status.ProblemCode));
            if (status is not null) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
        }
        catch (Exception)
        {
            if (SelectedServer?.Id != server.Id) return;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            SelectedStatusText = Status("webservers.status.failed", Text("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestConfiguration))]
    private async Task TestConfigurationAsync()
    {
        if (SelectedServer is null) return;
        IsLoading = true;
        TestResultText = Status("webservers.test.running");
        try
        {
            var result = await _client.TestConfigurationAsync(SelectedServer.Id);
            TestResultText = result is null
                ? Status("webservers.test.unavailable")
                : result.Valid
                    ? Status("webservers.test.valid")
                    : Status("webservers.test.invalid", ProblemText(result.ProblemCode));
            if (result is not null && !result.Valid) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
        }
        catch (Exception)
        {
            TestResultText = Status("webservers.test.failed", Text("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanIntegrate))]
    private async Task IntegrateAsync()
    {
        var candidate = SelectedIntegrationCandidate;
        if (candidate is null) return;
        if (RequestIntegrationConfirmationAsync is null || !await RequestIntegrationConfirmationAsync()) return;
        _dismissRuntimeFeedback?.Invoke();
        await RunOperationAsync("integrate", ct => _authorization.ExecuteAsync(HostElevationCapability.NginxConfigurationWrite, candidate.Id,
            () => _client.IntegrateCandidateAsync(candidate.Id, new IntegrateWebServerRequest(true), ct), ct));
    }

    [RelayCommand(CanExecute = nameof(CanEnableAcmeHttp01))]
    private Task EnableAcmeHttp01Async() => RunElevatedLifecycleAsync("enable-acme-http01", WebServerLifecycleAction.EnableAcmeHttp01,
        HostElevationCapability.NginxConfigurationWrite);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartManagedAsync() => RunElevatedLifecycleAsync("start", WebServerLifecycleAction.Start, HostElevationCapability.NginxLifecycle);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunElevatedLifecycleAsync("stop", WebServerLifecycleAction.Stop, HostElevationCapability.NginxLifecycle);

    /// <summary>Uses the observed runtime state so the primary control always performs the
    /// inverse action: start when stopped or unknown, stop when running.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleManaged))]
    private Task ToggleManagedAsync() => IsManagedServerRunning ? StopAsync() : StartManagedAsync();

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => RunElevatedLifecycleAsync("restart", WebServerLifecycleAction.Restart, HostElevationCapability.NginxLifecycle);

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunElevatedLifecycleAsync("reload", WebServerLifecycleAction.Reload, HostElevationCapability.NginxLifecycle);

    private Task RunElevatedLifecycleAsync(string kind, WebServerLifecycleAction action, HostElevationCapability capability)
    {
        var server = SelectedServer;
        return server is null
            ? Task.CompletedTask
            : RunOperationAsync(kind, ct => _authorization.ExecuteAsync(capability, server.Id,
                () => _client.ApplyLifecycleAsync(server.Id, action, ct), ct));
    }

    [RelayCommand(CanExecute = nameof(CanUninstallManaged))]
    private async Task UninstallManagedAsync()
    {
        if (_uninstallRuntimeAsync is not { } uninstall || !CanUninstallManaged
            || RequestManagedUninstallConfirmationAsync is null) return;
        var original = SelectedServer?.Id;
        if (!await RequestManagedUninstallConfirmationAsync() || !CanUninstallManaged || SelectedServer?.Id != original) return;
        await uninstall();
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSiteEditor))]
    private async Task NewSiteAsync()
    {
        if (!CanOpenSiteEditor) return;
        ResetSiteEditor();
        var targetVersion = _siteTargetVersion;
        var selectionVersion = _siteSelectionVersion;
        await LoadCertificatesAsync();
        if (!CanOpenSiteEditor || targetVersion != _siteTargetVersion || selectionVersion != _siteSelectionVersion) return;
        SiteStatusText = Status("webservers.site.new_status");
        if (ShowSiteEditorAsync is not null) await ShowSiteEditorAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanEditSite))]
    private async Task EditSiteAsync()
    {
        if (!CanEditSite) return;
        if (SelectedSite is null) return;
        var targetVersion = _siteTargetVersion;
        var selectionVersion = _siteSelectionVersion;
        await LoadCertificatesAsync();
        if (!CanEditSite || targetVersion != _siteTargetVersion || selectionVersion != _siteSelectionVersion) return;
        if (ShowSiteEditorAsync is not null) await ShowSiteEditorAsync(true);
    }

    [RelayCommand(CanExecute = nameof(CanSaveSite))]
    private async Task SaveSiteAsync()
    {
        if (!CanSaveSite) return;
        using var cancellation = new CancellationTokenSource();
        _siteSaveCancellation = cancellation;
        IsSavingSite = true;
        try { await SaveSiteCoreAsync(cancellation.Token); }
        finally
        {
            _siteSaveCancellation = null;
            IsSavingSite = false;
        }
    }

    private async Task SaveSiteCoreAsync(CancellationToken cancellationToken)
    {
        var server = SelectedServer;
        var site = _siteEditorSnapshot;
        var siteId = site?.Id;
        var targetVersion = _siteTargetVersion;
        bool IsCurrentConnection() => _windowConnection.IsCurrent
            && targetVersion == _siteTargetVersion && SelectedServer?.Id == server?.Id;
        bool IsCurrent() => IsCurrentConnection() && SelectedSite?.Id == siteId;
        if (server is null || !HasManagePermission) return;
        var bindings = SiteBindings
            .Select(binding => new WebServerSiteBindingDto(binding.Domain.Trim(), binding.Port))
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Domain))
            .ToArray();
        var routes = SiteRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route.Path) || !string.IsNullOrWhiteSpace(route.Upstream))
            .Select(route => new WebServerProxyRouteDto(route.Path.Trim(), route.Upstream.Trim(), route.DisableBuffering))
            .ToArray();
        if (bindings.Length == 0 || string.IsNullOrWhiteSpace(SiteName) || (string.IsNullOrWhiteSpace(SiteRootPath) && routes.Length == 0)
            || (SiteHttpsEnabled && (SelectedSiteCertificateSource?.Value == SiteCertificateSource.Managed
                ? SelectedSiteCertificate is null : string.IsNullOrWhiteSpace(SiteCertificatePath))))
        {
            await ReportSiteSaveErrorAsync(Status("webservers.site.validation_required"));
            return;
        }
        try
        {
            var request = new UpsertWebServerSiteRequest(site?.Id, SiteName.Trim(), bindings,
                string.IsNullOrWhiteSpace(SiteRootPath) ? null : SiteRootPath.Trim(), SiteGrantNginxReadAccess, SiteSpaFallback, routes,
                SelectedSiteCertificateSource?.Value == SiteCertificateSource.Managed ? SelectedSiteCertificate?.Id : null, SiteHttpsEnabled, SiteRedirectHttpToHttps, SiteIpv6Enabled,
                SelectedSiteCertificateSource?.Value == SiteCertificateSource.ServerFiles && !string.IsNullOrWhiteSpace(SiteCertificatePath) ? SiteCertificatePath : null,
                SelectedSiteCertificateSource?.Value == SiteCertificateSource.ServerFiles && !string.IsNullOrWhiteSpace(SitePrivateKeyPath) ? SitePrivateKeyPath : null, site?.UpdatedAt);
            var saved = await _authorization.ExecuteAsync(HostElevationCapability.NginxConfigurationWrite, server.Id,
                () => !cancellationToken.IsCancellationRequested && IsCurrent() && HasManagePermission
                    ? _client.UpsertSiteAsync(server.Id, request, cancellationToken)
                    : Task.FromException<WebServerSiteDto?>(new OperationCanceledException("The site editor target changed.")),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsCurrent()) return;
            if (saved is null) { await ReportSiteSaveErrorAsync(Status("webservers.site.save_failed")); return; }
            var refreshed = await LoadSitesAsync();
            if (!IsCurrentConnection()) return;
            if (!refreshed)
            {
                var previous = Sites.FirstOrDefault(site => site.Id == saved.Id);
                if (previous is not null) Sites.Remove(previous);
                Sites.Add(saved);
            }
            SelectedSite = Sites.FirstOrDefault(site => site.Id == saved.Id);
            SiteStatusText = Status(refreshed ? "webservers.site.save_succeeded" : "webservers.site.save_refresh_failed");
            if (CloseSiteEditorAsync is not null) await CloseSiteEditorAsync();
        }
        catch (WebServerApiException exception)
        {
            if (!IsCurrent()) return;
            var message = IsConfigurationElevationRequired(exception)
                ? Text("webservers.problem.site_elevation_required")
                : SiteSaveProblemText(exception.ProblemCode);
            await ReportSiteSaveErrorAsync(WebServerStatusText.Literal(message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (IsCurrent()) await ReportSiteSaveErrorAsync(Status("webservers.site.save_failed"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSite))]
    private async Task DeleteSiteAsync()
    {
        if (!CanDeleteSite) return;
        var server = SelectedServer;
        var site = SelectedSite;
        if (server is null || site is null || !HasManagePermission) return;
        try
        {
            await _authorization.ExecuteAsync(HostElevationCapability.NginxConfigurationWrite, server.Id, async () =>
            {
                await _client.DeleteSiteAsync(server.Id, site.Id, new DeleteWebServerSiteRequest(site.UpdatedAt));
                return true;
            });
            ResetSiteEditor();
            await LoadSitesAsync();
            SiteStatusText = Status("webservers.site.delete_succeeded");
        }
        catch (WebServerApiException exception) when (IsConfigurationElevationRequired(exception))
        {
            SiteStatusText = Status("webservers.problem.site_elevation_required");
        }
        catch (WebServerApiException exception) { SiteStatusText = WebServerStatusText.Literal(ProblemText(exception.ProblemCode)); }
        catch (Exception) { SiteStatusText = Status("webservers.site.delete_failed"); }
    }

    private static bool IsConfigurationElevationRequired(WebServerApiException exception) =>
        exception.StatusCode == System.Net.HttpStatusCode.Forbidden
        && string.Equals(exception.ProblemCode, "webserver.elevation_required", StringComparison.Ordinal);

    private void ResetSiteEditor()
    {
        SelectedSite = null;
        SiteName = string.Empty;
        SiteBindingsBatch = string.Empty;
        SiteBindings.Clear();
        SiteBindings.Add(new WebServerSiteBindingEditor());
        SiteRootPath = string.Empty;
        SiteGrantNginxReadAccess = false;
        SiteSpaFallback = true;
        SiteRoutes.Clear();
        SiteRoutes.Add(new WebServerProxyRouteEditor());
        SiteHttpsEnabled = false;
        SiteRedirectHttpToHttps = false;
        SiteIpv6Enabled = false;
        SelectedSiteCertificate = null;
        SelectedSiteCertificateSource = SiteCertificateSources[0];
        SiteCertificatePath = string.Empty;
        SitePrivateKeyPath = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private async Task CancelOperationAsync()
    {
        if (_operationCts is null) return;
        try
        {
            if (_currentOperationId is { } operationId)
            {
                await _client.CancelOperationAsync(operationId);
                return;
            }
            await _operationCts.CancelAsync();
        }
        catch (Exception) { /* cancellation is best-effort; the poll loop observes the token */ }
    }

    partial void OnSelectedServerChanged(WebServerDto? value)
    {
        _siteSaveCancellation?.Cancel();
        _siteTargetVersion++;
        OnPropertyChanged(nameof(SiteEditorTargetChanged));
        OnPropertyChanged(nameof(SiteEditorTargetChangeText));
        SiteFactsAvailable = false;
        _siteReadVersion++;
        SelectedStatusText = WebServerStatusText.Literal(ManagementHint);
        SelectedRuntimeState = WebServerRuntimeState.Unknown;
        TestResultText = string.Empty;
        SelectedSite = null;
        Sites.Clear();
        if (value is not null)
        {
            _ = RefreshStatusAsync();
            _ = LoadSitesAsync();
        }
    }

    partial void OnSelectedSiteChanged(WebServerSiteDto? value)
    {
        if (_siteEditorOpen && value?.Id != _siteEditorSiteId)
            _siteSaveCancellation?.Cancel();
        _siteSelectionVersion++;
        OnPropertyChanged(nameof(SiteEditorTargetChanged));
        OnPropertyChanged(nameof(SiteEditorTargetChangeText));
        if (_siteEditorOpen && !IsSavingSite) return;
        if (value is null) return;
        SiteName = value.Name;
        SiteBindingsBatch = string.Empty;
        SiteBindings.Clear();
        foreach (var binding in value.Bindings)
            SiteBindings.Add(new WebServerSiteBindingEditor(binding.Domain, binding.Port));
        SiteRootPath = value.RootPath ?? string.Empty;
        SiteGrantNginxReadAccess = false;
        SiteSpaFallback = value.SpaFallback;
        SiteRoutes.Clear();
        foreach (var route in value.Routes)
            SiteRoutes.Add(new WebServerProxyRouteEditor(route.Path, route.Upstream, route.DisableBuffering));
        SiteHttpsEnabled = value.HttpsEnabled;
        SiteRedirectHttpToHttps = value.RedirectHttpToHttps;
        SiteIpv6Enabled = value.Ipv6Enabled;
        SelectedSiteCertificate = Certificates.FirstOrDefault(certificate => certificate.Id == value.CertificateId);
        SiteCertificatePath = value.CertificatePath ?? string.Empty;
        SitePrivateKeyPath = value.PrivateKeyPath ?? string.Empty;
        SelectedSiteCertificateSource = SiteCertificateSources[value.CertificatePath is null ? 0 : 1];
    }

    partial void OnSelectedSiteCertificateChanged(CertificateDto? value)
    {
        if (value is not null)
            SelectedSiteCertificateSource = SiteCertificateSources[0];
    }

    partial void OnSiteHttpsEnabledChanged(bool value) => SiteRedirectHttpToHttps = value;

    private async Task LoadCertificatesAsync()
    {
        if (!HasReadPermission) return;
        try
        {
            var certificates = await _certificates.ListAsync();
            if (!HasReadPermission) return;
            var selectedId = SelectedSiteCertificate?.Id ?? SelectedSite?.CertificateId;
            Certificates.Clear();
            foreach (var certificate in certificates.Where(certificate => certificate.Status is CertificateStatus.Active or CertificateStatus.Issued)) Certificates.Add(certificate);
            if (selectedId is { } id)
                SelectedSiteCertificate = Certificates.FirstOrDefault(certificate => certificate.Id == id);
        }
        catch { if (HasReadPermission) Certificates.Clear(); }
    }

    [RelayCommand]
    private Task RefreshCertificatesAsync() => LoadCertificatesAsync();

    [RelayCommand]
    private async Task ChooseSiteCertificateFileAsync()
    {
        var path = await (RequestServerCertificateFileAsync?.Invoke(false) ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(path)) return;
        SiteCertificatePath = path;
        SelectedSiteCertificateSource = SiteCertificateSources[1];
    }

    [RelayCommand]
    private async Task ChooseSitePrivateKeyFileAsync()
    {
        var path = await (RequestServerCertificateFileAsync?.Invoke(true) ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path))
        {
            SitePrivateKeyPath = path;
            SelectedSiteCertificateSource = SiteCertificateSources[1];
        }
    }

    [RelayCommand]
    private async Task ChooseSiteRootDirectoryAsync()
    {
        var path = await (RequestSiteRootDirectoryAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path))
        {
            SiteRootPath = path;
            SiteGrantNginxReadAccess = true;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshSites))]
    private async Task RefreshSitesAsync()
    {
        if (!CanRefreshSites) return;
        IsLoading = true;
        try { await LoadSitesAsync(); }
        finally { IsLoading = false; }
    }

    private async Task<bool> LoadSitesAsync()
    {
        var server = SelectedServer;
        var version = ++_siteReadVersion;
        SiteFactsAvailable = false;
        if (server is null || !HasReadPermission) return false;
        try
        {
            var sites = await _client.ListSitesAsync(server.Id);
            if (!HasReadPermission || version != _siteReadVersion || SelectedServer?.Id != server.Id) return false;
            if (sites is null)
            {
                SiteStatusText = Status("webservers.site.list_failed");
                return false;
            }
            Sites.Clear();
            foreach (var site in sites) Sites.Add(site);
            SiteFactsAvailable = true;
            SiteStatusText = Sites.Count == 0
                ? Status("webservers.site.list_empty")
                : Status("webservers.site.list_ready", Sites.Count);
            return true;
        }
        catch (Exception)
        {
            if (HasReadPermission && version == _siteReadVersion && SelectedServer?.Id == server.Id)
                SiteStatusText = Status("webservers.site.list_failed");
            return false;
        }
    }

    private async Task ReportSiteSaveErrorAsync(WebServerStatusText message)
    {
        SiteStatusText = message;
        if (ShowSiteSaveErrorAsync is not null) await ShowSiteSaveErrorAsync(message.Resolve());
    }

    [RelayCommand]
    private void AddSiteBinding() => SiteBindings.Add(new WebServerSiteBindingEditor());

    [RelayCommand]
    private void RemoveSiteBinding(WebServerSiteBindingEditor? binding)
    {
        if (binding is not null && SiteBindings.Count > 1) SiteBindings.Remove(binding);
    }

    [RelayCommand]
    private void AddSiteRoute() => SiteRoutes.Add(new WebServerProxyRouteEditor());

    [RelayCommand]
    private void RemoveSiteRoute(WebServerProxyRouteEditor? route)
    {
        if (route is not null) SiteRoutes.Remove(route);
    }

    /// <summary>Converts pasted <c>domain:port</c> rows into independently editable binding pairs.</summary>
    [RelayCommand]
    private void GenerateSiteBindings()
    {
        var generated = SiteBindingsBatch
            .Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseSiteBinding)
            .ToArray();
        if (generated.Length == 0) return;
        SiteBindings.Clear();
        foreach (var binding in generated) SiteBindings.Add(binding);
    }

    public async Task OpenSiteDirectoryAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && OpenFileBrowserAtPathAsync is not null)
            await OpenFileBrowserAtPathAsync(path);
    }

    private static WebServerSiteBindingEditor ParseSiteBinding(string value)
    {
        var domain = value;
        var port = 80;
        var separator = value.LastIndexOf(':');
        // Bracketed IPv6 can be pasted as [2001:db8::1]:5000. Bare IPv6 remains a domain
        // value and is subsequently validated by the server without guessing a port.
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var parsedPort)
            && (value[0] != '[' || value.IndexOf(']') < separator))
        {
            domain = value[..separator].Trim('[', ']');
            port = parsedPort;
        }
        return new WebServerSiteBindingEditor(domain, port);
    }

    private async Task RunOperationAsync(string kindKey, WebServerDto server, Func<string, CancellationToken, Task<WebServerOperationDto?>> start)
        => await RunOperationAsync(kindKey, ct => start(server.Id, ct));

    /// <summary>
    /// Starts and monitors an operation. <paramref name="rethrowApiProblemCode"/> is reserved for
    /// the one install response that requires a follow-up UI choice; all other API errors remain
    /// user-facing operation failures and must never escape an async command.
    /// </summary>
    private async Task RunOperationAsync(string kindKey, Func<CancellationToken, Task<WebServerOperationDto?>> start, string? rethrowApiProblemCode = null)
    {
        if (!HasManagePermission)
        {
            StatusText = Status("webservers.permission.manage_required");
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        OperationLog = string.Empty;
        _lastOperationLogText = string.Empty;
        IsOperationRunning = true;
        OperationText = Status("webservers.operation.starting", OperationName(kindKey));
        try
        {
            var operation = await start(token);
            if (operation is null)
            {
                OperationText = Status("webservers.operation.not_found");
                return;
            }
            if (operation.OperationId == Guid.Empty)
            {
                OperationText = Status("webservers.operation.rejected", ProblemText(operation.ProblemCode));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(operation.ProblemCode);
                return;
            }
            _currentOperationId = operation.OperationId;
            operation = await PollOperationAsync(operation, token);
            if (operation.State == WebServerOperationState.Succeeded)
            {
                OperationText = Status("webservers.operation.succeeded", OperationName(kindKey));
                if (kindKey == "integrate") await RefreshAsync();
                else await RefreshStatusAsync();
            }
            else if (operation.State == WebServerOperationState.Cancelled)
                OperationText = Status("webservers.operation.cancelled");
            else
            {
                OperationText = Status("webservers.operation.failed", OperationName(kindKey), ProblemText(operation.ProblemCode));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(operation.ProblemCode);
            }
        }
        catch (OperationCanceledException)
        {
            OperationText = Status("webservers.operation.cancelled");
        }
        catch (WebServerApiException exception) when (exception.ProblemCode == rethrowApiProblemCode)
        {
            throw;
        }
        catch (WebServerApiException exception)
        {
            OperationText = Status("webservers.operation.failed", OperationName(kindKey), ProblemText(exception.ProblemCode));
            await ShowPrivilegedHelperUnavailableAsyncIfNeeded(exception.ProblemCode);
        }
        catch (Exception)
        {
            OperationText = Status("webservers.operation.exception", OperationName(kindKey), Text("webservers.error.request_failed"));
        }
        finally
        {
            _currentOperationId = null;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task<WebServerOperationDto> PollOperationAsync(WebServerOperationDto operation, CancellationToken cancellationToken)
    {
        while (operation.State is WebServerOperationState.Queued or WebServerOperationState.Running)
        {
            OperationText = Status("webservers.operation.progress", OperationName(operation.Kind), OperationStage(operation.Kind, operation.Stage));
            try { await Task.Delay(PollInterval, cancellationToken); }
            catch (OperationCanceledException) { return operation; }
            var updated = await _client.GetOperationAsync(operation.OperationId, cancellationToken);
            if (updated is null) break;
            operation = updated;
        }
        return operation;
    }

    private bool HasReadPermission => _windowConnection.IsCurrent && _permissions.IsGranted(AppPermissions.ServerWebServersRead);
    private bool HasManagePermission => HasReadPermission && _permissions.IsGranted(AppPermissions.ServerWebServersManage);
    private bool CanRefresh => HasReadPermission && !IsSavingSite && !IsLoading && !IsOperationRunning;
    private bool CanRefreshSites => HasReadPermission && !IsSavingSite && !IsLoading && !IsOperationRunning && SelectedServer is not null;
    private bool CanDiscover => HasReadPermission && !IsSavingSite && !IsLoading && !IsOperationRunning;
    // A server from an older deployment can omit the capabilities object. Treat that response as
    // read-only instead of letting command re-evaluation crash while the DataGrid selects it.
    private bool CanRefreshStatus => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanRead == true;
    private bool CanTestConfiguration => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanTestConfiguration == true;
    private bool CanIntegrate => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedIntegrationCandidate is not null;
    private bool CanEnableAcmeHttp01 => CanManageSites;
    private bool CanStart => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanStart == true;
    private bool CanStop => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanStop == true;
    private bool CanToggleManaged => IsManagedServerRunning ? CanStop : CanStart;
    private bool CanRestart => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanRestart == true;
    private bool CanReload => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanReload == true;
    private bool CanUninstallManaged => _uninstallRuntimeAsync is not null && HasManagePermission
        && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanUninstall == true;
    private bool CanManageSites => HasManagePermission && SiteFactsAvailable && !IsSavingSite && !IsLoading && !IsOperationRunning && SelectedServer?.ManagementMode is WebServerManagementMode.Integrated or WebServerManagementMode.Managed;
    private bool CanSaveSite => CanManageSites && _siteEditorOpen && _siteEditorTargetVersion == _siteTargetVersion && SelectedSite?.Id == _siteEditorSiteId;
    private bool CanOpenSiteEditor => CanManageSites;
    private bool CanEditSite => CanManageSites && SelectedSite is not null;
    private bool CanDeleteSite => CanManageSites && SelectedSite is not null;
    private bool CanCancelOperation => IsOperationRunning;

    private string OperationName(string kind) => kind switch
    {
        "install" => Text("webservers.operation.kind.install"),
        "integrate" => Text("webservers.operation.kind.integrate"),
        "uninstall" => Text("webservers.operation.kind.uninstall"),
        "start" => Text("webservers.operation.kind.start"),
        "stop" => Text("webservers.operation.kind.stop"),
        "restart" => Text("webservers.operation.kind.restart"),
        "reload" => Text("webservers.operation.kind.reload"),
        "enable-acme-http01" => Text("webservers.operation.kind.enable_acme_http01"),
        _ => Text("webservers.operation.kind.unknown"),
    };

    private string RuntimeStateText(WebServerRuntimeState state) => state switch
    {
        WebServerRuntimeState.Running => Text("webservers.enum.runtime.running"),
        WebServerRuntimeState.Stopped => Text("webservers.enum.runtime.stopped"),
        _ => Text("webservers.enum.runtime.unknown"),
    };

    private string OperationStage(string kind, string stage) => (kind, stage) switch
    {
        (_, "queued") => Text("webservers.operation.stage.queued"),
        (_, "running" or "native-reload-dispatch") => Text("webservers.operation.stage.running"),
        (_, "native-recovery-required") => ProblemText("webserver.native_recovery_required"),
        ("install", "installer_running") => Text("webservers.operation.stage.installer_running"),
        ("install", "installing_package") => Text("webservers.operation.stage.installing_package"),
        ("install", "downloading") => Text("webservers.operation.stage.downloading"),
        ("install", "extracting") => Text("webservers.operation.stage.extracting"),
        ("install", "removing_existing_installation") => Text("webservers.operation.stage.removing_existing_installation"),
        ("install", "verifying_layout") => Text("webservers.operation.stage.verifying_layout"),
        ("install", "validating_configuration") => Text("webservers.operation.stage.validating_configuration"),
        ("install", "finalizing") => Text("webservers.operation.stage.finalizing"),
        _ => Text("webservers.operation.stage.unknown"),
    };

    private string ProblemText(string? problemCode)
    {
        if (FormatHostProblem?.Invoke(problemCode) is { } helperMessage) return helperMessage;
        if (string.IsNullOrWhiteSpace(problemCode) || !problemCode.StartsWith("webserver.", StringComparison.Ordinal))
            return Text("webservers.problem.unknown");
        return Text($"webservers.problem.{problemCode["webserver.".Length..]}", Text("webservers.problem.unknown"));
    }

    private string SiteSaveProblemText(string problemCode) => ProblemText(problemCode);

    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        FormatHostProblem?.Invoke(problemCode) is not null
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;

    private string Text(string key, string? fallback = null) => WebServerLocalization.Resolve(_language, key, fallback);

    private WebServerStatusText Status(string key, params object?[] arguments) =>
        new(() => arguments.Length == 0 ? Text(key) : string.Format(CultureInfo.CurrentCulture, Text(key), arguments));

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => OnPropertyChanged(string.Empty);

    // nginx -t + reload is fast; a tighter poll keeps the UI responsive without spamming the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    private void OnConnectionInvalidated()
    {
        void Invalidate()
        {
            if (_windowConnection.IsCurrent) return;
            _siteSaveCancellation?.Cancel();
            _siteReadVersion++;
            SiteFactsAvailable = false;
            SelectedServer = null;
            Servers.Clear();
            Sites.Clear();
            Certificates.Clear();
            ResetSiteEditor();
            SiteStatusText = string.Empty;
            OnPropertyChanged(nameof(SiteEditorTargetChanged));
            OnPropertyChanged(nameof(SiteEditorTargetChangeText));
            try { _operationCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (Dispatcher.UIThread.CheckAccess()) Invalidate();
        else Dispatcher.UIThread.Post(Invalidate);
    }
}
