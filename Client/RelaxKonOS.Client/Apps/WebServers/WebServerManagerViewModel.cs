using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;
using System.Collections.ObjectModel;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Client.Apps.WebServers;

/// <summary>
/// Window-local web server manager state. Discovery probes the host for Nginx (and future
/// providers); integrate/reload are explicitly confirmed, marker-owned operations polled to
/// a terminal state. The server never accepts shell text or elevation credentials over HTTP.
/// </summary>
public sealed partial class WebServerManagerViewModel : LocalizedObservableObject
{
    public InstallationTaskViewModel Installation { get; set; } = null!;

    private readonly IRemoteWebServerClient _client;
    private readonly IRemoteCertificateClient _certificates;
    private readonly IAuthSession _session;
    private readonly IAppPermissionScope _permissions;
    private CancellationTokenSource? _operationCts;

    public WebServerManagerViewModel(IRemoteWebServerClient client, IRemoteCertificateClient certificates, IAuthSession session, IAppPermissionScope permissions)
    {
        _client = client;
        _certificates = certificates;
        _session = session;
        _permissions = permissions;
        InstallVersion = string.Empty;
        SelectedSiteCertificateSource = SiteCertificateSources[0];
    }

    public ObservableCollection<WebServerDto> Servers { get; } = [];
    public ObservableCollection<WebServerStatusDto> Statuses { get; } = [];
    public ObservableCollection<WebServerSiteDto> Sites { get; } = [];
    public ObservableCollection<string> AvailableWindowsVersions { get; } = [];
    public ObservableCollection<CertificateDto> Certificates { get; } = [];
    public ObservableCollection<WebServerSiteBindingEditor> SiteBindings { get; } = [];
    public IReadOnlyList<WebServerSiteKind> SiteKinds { get; } = [WebServerSiteKind.ReverseProxy, WebServerSiteKind.Static];
    public IReadOnlyList<SiteCertificateSourceOption> SiteCertificateSources { get; } =
    [
        new(SiteCertificateSource.Managed, LocalizedText.Get("webservers.site.certificate_source.managed")),
        new(SiteCertificateSource.ServerFiles, LocalizedText.Get("webservers.site.certificate_source.files")),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IntegrateCommand), nameof(EnableAcmeHttp01Command), nameof(StartManagedCommand), nameof(StopCommand), nameof(ToggleManagedCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    [NotifyPropertyChangedFor(nameof(IsExternalServer), nameof(IsIntegratedServer), nameof(IsManagedServer), nameof(IsIntegratedOrManagedServer), nameof(IsManagedServerRunning), nameof(ManagementHint), nameof(ManagedLifecycleActionText))]
    private WebServerDto? _selectedServer;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(EditSiteCommand))]
    private WebServerSiteDto? _selectedSite;
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.Ref("webservers.status.loading");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private LocalizedStatus _operationText = string.Empty;
    [ObservableProperty] private LocalizedStatus _testResultText = string.Empty;
    [ObservableProperty] private LocalizedStatus _selectedStatusText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleManagedCommand))]
    [NotifyPropertyChangedFor(nameof(IsManagedServerRunning), nameof(ManagedLifecycleActionText), nameof(ManagedRuntimeStateLabel), nameof(ManagedRuntimeStateDescription))]
    private WebServerRuntimeState _selectedRuntimeState = WebServerRuntimeState.Unknown;
    [ObservableProperty] private string _installVersion = string.Empty;
    [ObservableProperty] private string _localPackageName = string.Empty;
    [ObservableProperty] private string _siteName = string.Empty;
    [ObservableProperty] private string _siteBindingsBatch = string.Empty;
    [ObservableProperty] private string _siteUpstream = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsReverseProxySite), nameof(IsStaticSite))] private WebServerSiteKind _selectedSiteKind = WebServerSiteKind.ReverseProxy;
    [ObservableProperty] private bool _siteHttpsEnabled;
    [ObservableProperty] private CertificateDto? _selectedSiteCertificate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsManagedCertificateSource), nameof(IsServerFileCertificateSource))]
    private SiteCertificateSourceOption? _selectedSiteCertificateSource;
    [ObservableProperty] private string _siteCertificatePath = string.Empty;
    [ObservableProperty] private string _sitePrivateKeyPath = string.Empty;
    [ObservableProperty] private LocalizedStatus _siteStatusText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(InstallManagedCommand), nameof(IntegrateCommand), nameof(EnableAcmeHttp01Command), nameof(StartManagedCommand), nameof(StopCommand), nameof(ToggleManagedCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand))]
    private bool _isLoading;
    // Every action uses IsOperationRunning in its CanExecute predicate. Keep the command state
    // in sync before and after polling, otherwise controls can retain a stale disabled state.
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(DiscoverCommand), nameof(InstallManagedCommand), nameof(IntegrateCommand), nameof(EnableAcmeHttp01Command), nameof(StartManagedCommand), nameof(StopCommand), nameof(ToggleManagedCommand), nameof(RestartCommand), nameof(ReloadCommand), nameof(UninstallManagedCommand), nameof(TestConfigurationCommand), nameof(RefreshStatusCommand), nameof(SaveSiteCommand), nameof(DeleteSiteCommand), nameof(NewSiteCommand), nameof(EditSiteCommand), nameof(CancelOperationCommand))]
    private bool _isOperationRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsManagedInstallAvailable))]
    private bool _hasManagedInstallation;

    private Guid? _currentOperationId;
    private string? localPackageReference;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    public bool IsWindowsServer => _session.CurrentServer?.Platform == PlatformKind.Windows;
    public bool IsLinuxServer => _session.CurrentServer?.Platform == PlatformKind.Linux;
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationText);
    public bool IsReverseProxySite => SelectedSiteKind == WebServerSiteKind.ReverseProxy;
    public bool IsStaticSite => SelectedSiteKind == WebServerSiteKind.Static;
    public bool IsManagedCertificateSource => SelectedSiteCertificateSource?.Value != SiteCertificateSource.ServerFiles;
    public bool IsServerFileCertificateSource => SelectedSiteCertificateSource?.Value == SiteCertificateSource.ServerFiles;
    public bool IsExternalServer => SelectedServer?.ManagementMode == WebServerManagementMode.External;
    public bool IsIntegratedServer => SelectedServer?.ManagementMode == WebServerManagementMode.Integrated;
    public bool IsManagedServer => SelectedServer?.ManagementMode == WebServerManagementMode.Managed;
    public bool IsIntegratedOrManagedServer => IsIntegratedServer || IsManagedServer;
    public bool IsManagedServerRunning => IsManagedServer && SelectedRuntimeState == WebServerRuntimeState.Running;
    public bool IsManagedInstallAvailable => !HasManagedInstallation;
    public string ManagedLifecycleActionText => IsManagedServerRunning
        ? LocalizedText.Get("webservers.action.stop")
        : LocalizedText.Get("webservers.action.start");
    public string ManagedRuntimeStateLabel => RuntimeStateText(SelectedRuntimeState);
    public string ManagedRuntimeStateDescription => SelectedRuntimeState switch
    {
        WebServerRuntimeState.Running => LocalizedText.Get("webservers.runtime.running_hint"),
        WebServerRuntimeState.Stopped => LocalizedText.Get("webservers.runtime.stopped_hint"),
        _ => LocalizedText.Get("webservers.runtime.unknown_hint"),
    };
    public string ManagementHint => SelectedServer?.ManagementMode switch
    {
        WebServerManagementMode.Integrated => LocalizedText.Get("webservers.management_hint.integrated"),
        WebServerManagementMode.Managed => LocalizedText.Get("webservers.management_hint.managed"),
        WebServerManagementMode.External => LocalizedText.Get("webservers.management_hint.external"),
        _ => LocalizedText.Get("webservers.management_hint.none"),
    };

    /// <summary>Supplied by the application shell so the view model never constructs UI directly.</summary>
    public Func<Task<bool>>? RequestIntegrationConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedInstallConfirmationAsync { get; set; }
    public Func<Task<bool>>? RequestManagedUninstallConfirmationAsync { get; set; }
    public Func<Task<string?>>? RequestLocalNginxPackageAsync { get; set; }
    public Func<string, Task>? ShowManagedDownloadUrlAsync { get; set; }
    /// <summary>Routes a known static-site directory into RemoteExplorer.</summary>
    public Func<string, Task>? OpenFileBrowserAtPathAsync { get; set; }
    /// <summary>Opens the RemoteExplorer picker for certificate or key files on the server.</summary>
    public Func<bool, Task<string?>>? RequestServerCertificateFileAsync { get; set; }
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
        if (IsWindowsServer) await LoadWindowsVersionsAsync();
    }

    private async Task LoadWindowsVersionsAsync()
    {
        try
        {
            var catalog = await _client.GetManagedInstallCatalogAsync();
            AvailableWindowsVersions.Clear();
            foreach (var version in catalog?.Versions ?? []) AvailableWindowsVersions.Add(version);
            if (catalog is null || catalog.Versions.Count == 0 || !string.IsNullOrWhiteSpace(catalog.ProblemCode))
            {
                StatusText = LocalizedText.Ref("webservers.version_catalog.unavailable");
                return;
            }
            if (string.IsNullOrWhiteSpace(InstallVersion)) InstallVersion = catalog.MainlineVersion ?? catalog.StableVersion ?? string.Empty;
        }
        catch { StatusText = LocalizedText.Ref("webservers.version_catalog.unavailable"); }
    }
    [RelayCommand(CanExecute = nameof(CanInstallManaged))] private Task RefreshWindowsVersionsAsync() => LoadWindowsVersionsAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            HasManagedInstallation = false;
            StatusText = LocalizedText.Ref("webservers.permission.read_required");
            return;
        }

        IsLoading = true;
        try
        {
            var servers = await _client.ListAsync() ?? [];
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            SelectedStatusText = string.Empty;
            foreach (var server in servers) Servers.Add(server);
            HasManagedInstallation = servers.Any(server => server.ManagementMode == WebServerManagementMode.Managed);
            StatusText = LocalizedText.Ref("webservers.status.ready", servers.Count);
        }
        catch (Exception)
        {
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            HasManagedInstallation = false;
            StatusText = LocalizedText.Ref("webservers.status.failed", LocalizedText.Get("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDiscover))]
    private async Task DiscoverAsync()
    {
        IsLoading = true;
        StatusText = LocalizedText.Ref("webservers.discover.running");
        try
        {
            var servers = await _client.DiscoverAsync() ?? [];
            Servers.Clear();
            Statuses.Clear();
            SelectedServer = null;
            foreach (var server in servers) Servers.Add(server);
            HasManagedInstallation = servers.Any(server => server.ManagementMode == WebServerManagementMode.Managed);
            StatusText = LocalizedText.Ref(servers.Count > 0 ? "webservers.discover.found" : "webservers.discover.empty", servers.Count);
        }
        catch (Exception)
        {
            StatusText = LocalizedText.Ref("webservers.discover.failed", LocalizedText.Get("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshStatus))]
    private async Task RefreshStatusAsync()
    {
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
                ? LocalizedText.Ref("webservers.status.unavailable")
                : string.IsNullOrWhiteSpace(status.ProblemCode)
                    ? LocalizedStatus.Literal(RuntimeStateText(status.RuntimeState))
                    : LocalizedText.Ref("webservers.status.detail", RuntimeStateText(status.RuntimeState), ProblemText(status.ProblemCode));
            if (status is not null) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
        }
        catch (Exception)
        {
            if (SelectedServer?.Id != server.Id) return;
            SelectedRuntimeState = WebServerRuntimeState.Unknown;
            SelectedStatusText = LocalizedText.Ref("webservers.status.failed", LocalizedText.Get("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestConfiguration))]
    private async Task TestConfigurationAsync()
    {
        if (SelectedServer is null) return;
        IsLoading = true;
        TestResultText = LocalizedText.Ref("webservers.test.running");
        try
        {
            var result = await _client.TestConfigurationAsync(SelectedServer.Id);
            TestResultText = result is null
                ? LocalizedText.Ref("webservers.test.unavailable")
                : result.Valid
                    ? LocalizedText.Ref("webservers.test.valid")
                    : LocalizedText.Ref("webservers.test.invalid", ProblemText(result.ProblemCode));
            if (result is not null && !result.Valid) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
        }
        catch (Exception)
        {
            TestResultText = LocalizedText.Ref("webservers.test.failed", LocalizedText.Get("webservers.error.request_failed"));
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanIntegrate))]
    private async Task IntegrateAsync()
    {
        if (RequestIntegrationConfirmationAsync is null || !await RequestIntegrationConfirmationAsync()) return;
        await RunOperationAsync("integrate", SelectedServer!,
            (id, ct) => _client.IntegrateAsync(id, new IntegrateWebServerRequest(true), ct));
    }

    [RelayCommand(CanExecute = nameof(CanEnableAcmeHttp01))]
    private Task EnableAcmeHttp01Async() => RunOperationAsync("enable-acme-http01", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.EnableAcmeHttp01, ct));

    [RelayCommand(CanExecute = nameof(CanInstallManaged))]
    private async Task InstallManagedAsync()
    {
        if (RequestManagedInstallConfirmationAsync is null || !await RequestManagedInstallConfirmationAsync()) return;
        await Installation.SubmitAsync(InstallationOperationKind.Install, new NginxInstallationRequest(true,
            string.IsNullOrWhiteSpace(InstallVersion) ? null : InstallVersion.Trim(), localPackageReference));
    }

    [RelayCommand(CanExecute = nameof(CanInstallManaged))]
    private async Task ShowManagedDownloadAsync()
    {
        if (string.IsNullOrWhiteSpace(InstallVersion)) { StatusText = LocalizedText.Ref("webservers.problem.version_required"); return; }
        try
        {
            var download = await _client.GetManagedInstallDownloadAsync(InstallVersion.Trim());
            if (download is null) { StatusText = LocalizedText.Ref("webservers.managed.download_unavailable"); return; }
            await (ShowManagedDownloadUrlAsync?.Invoke(download.Url) ?? Task.CompletedTask);
        }
        catch (Exception exception) { StatusText = ProblemText(exception is WebServerApiException request ? request.ProblemCode : "webservers.error.request_failed"); }
    }

    [RelayCommand(CanExecute = nameof(CanInstallManaged))]
    private async Task SelectLocalPackageAsync()
    {
        if (RequestLocalNginxPackageAsync is not { } select) return;
        var path = await select();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await using var package = File.OpenRead(path);
            var reference = await _client.UploadManagedPackageAsync(Path.GetFileName(path), package);
            if (reference is null) { StatusText = LocalizedText.Ref("webservers.package.invalid"); return; }
            localPackageReference = reference.Id;
            LocalPackageName = reference.FileName;
        }
        catch (Exception exception) { StatusText = ProblemText(exception is WebServerApiException request ? request.ProblemCode : "webservers.error.request_failed"); }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartManagedAsync() => RunOperationAsync("start", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Start, ct));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunOperationAsync("stop", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Stop, ct));

    /// <summary>Uses the observed runtime state so the primary control always performs the
    /// inverse action: start when stopped or unknown, stop when running.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleManaged))]
    private Task ToggleManagedAsync() => IsManagedServerRunning ? StopAsync() : StartManagedAsync();

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => RunOperationAsync("restart", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Restart, ct));

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => RunOperationAsync("reload", ct => _client.ApplyLifecycleAsync(SelectedServer!.Id, WebServerLifecycleAction.Reload, ct));

    [RelayCommand(CanExecute = nameof(CanUninstallManaged))]
    private async Task UninstallManagedAsync()
    {
        if (RequestManagedUninstallConfirmationAsync is null || !await RequestManagedUninstallConfirmationAsync()) return;
        await Installation.SubmitAsync(InstallationOperationKind.Uninstall, new NginxInstallationRequest(true));
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSiteEditor))]
    private async Task NewSiteAsync()
    {
        ResetSiteEditor();
        await LoadCertificatesAsync();
        SiteStatusText = LocalizedText.Ref("webservers.site.new_status");
        if (ShowSiteEditorAsync is not null) await ShowSiteEditorAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanEditSite))]
    private async Task EditSiteAsync()
    {
        if (SelectedSite is null) return;
        await LoadCertificatesAsync();
        if (ShowSiteEditorAsync is not null) await ShowSiteEditorAsync(true);
    }

    [RelayCommand(CanExecute = nameof(CanSaveSite))]
    private async Task SaveSiteAsync()
    {
        if (SelectedServer is null || !HasManagePermission) return;
        var bindings = SiteBindings
            .Select(binding => new WebServerSiteBindingDto(binding.Domain.Trim(), binding.Port))
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Domain))
            .ToArray();
        if (bindings.Length == 0 || string.IsNullOrWhiteSpace(SiteName) || (IsReverseProxySite && string.IsNullOrWhiteSpace(SiteUpstream))
            || (SiteHttpsEnabled && (SelectedSiteCertificateSource?.Value == SiteCertificateSource.Managed
                ? SelectedSiteCertificate is null : string.IsNullOrWhiteSpace(SiteCertificatePath))))
        {
            await ReportSiteSaveErrorAsync(LocalizedText.Ref("webservers.site.validation_required"));
            return;
        }
        try
        {
            var domains = bindings.Select(binding => binding.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var saved = await _client.UpsertSiteAsync(SelectedServer.Id, new UpsertWebServerSiteRequest(SelectedSite?.Id, SiteName.Trim(), SelectedSiteKind, domains, bindings[0].Port,
                IsReverseProxySite ? SiteUpstream.Trim() : null, null,
                SelectedSiteCertificateSource?.Value == SiteCertificateSource.Managed ? SelectedSiteCertificate?.Id : null, SiteHttpsEnabled, bindings,
                SelectedSiteCertificateSource?.Value == SiteCertificateSource.ServerFiles && !string.IsNullOrWhiteSpace(SiteCertificatePath) ? SiteCertificatePath : null,
                SelectedSiteCertificateSource?.Value == SiteCertificateSource.ServerFiles && !string.IsNullOrWhiteSpace(SitePrivateKeyPath) ? SitePrivateKeyPath : null));
            if (saved is null) { await ReportSiteSaveErrorAsync(LocalizedText.Ref("webservers.site.save_failed")); return; }
            await LoadSitesAsync();
            SelectedSite = Sites.FirstOrDefault(site => site.Id == saved.Id);
            SiteStatusText = LocalizedText.Ref("webservers.site.save_succeeded");
            if (CloseSiteEditorAsync is not null) await CloseSiteEditorAsync();
        }
        catch (WebServerApiException exception) { await ReportSiteSaveErrorAsync(LocalizedStatus.Literal(SiteSaveProblemText(exception.ProblemCode))); }
        catch (Exception) { await ReportSiteSaveErrorAsync(LocalizedText.Ref("webservers.site.save_failed")); }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSite))]
    private async Task DeleteSiteAsync()
    {
        if (SelectedServer is null || SelectedSite is null || !HasManagePermission) return;
        try
        {
            await _client.DeleteSiteAsync(SelectedServer.Id, SelectedSite.Id);
            ResetSiteEditor();
            await LoadSitesAsync();
            SiteStatusText = LocalizedText.Ref("webservers.site.delete_succeeded");
        }
        catch (Exception) { SiteStatusText = LocalizedText.Ref("webservers.site.delete_failed"); }
    }

    private void ResetSiteEditor()
    {
        SelectedSite = null;
        SiteName = string.Empty;
        SiteBindingsBatch = string.Empty;
        SiteBindings.Clear();
        SiteBindings.Add(new WebServerSiteBindingEditor());
        SiteUpstream = string.Empty;
        SelectedSiteKind = WebServerSiteKind.ReverseProxy;
        SiteHttpsEnabled = false;
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
                await _client.CancelOperationAsync(operationId);
            await _operationCts.CancelAsync();
        }
        catch (Exception) { /* cancellation is best-effort; the poll loop observes the token */ }
    }

    partial void OnSelectedServerChanged(WebServerDto? value)
    {
        SelectedStatusText = LocalizedStatus.Literal(ManagementHint);
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
        if (value is null) return;
        SiteName = value.Name;
        SiteBindingsBatch = string.Empty;
        SiteBindings.Clear();
        foreach (var binding in value.EffectiveBindings)
            SiteBindings.Add(new WebServerSiteBindingEditor(binding.Domain, binding.Port));
        SiteUpstream = value.Upstream ?? string.Empty;
        SelectedSiteKind = value.Kind;
        SiteHttpsEnabled = value.HttpsEnabled;
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

    private async Task LoadCertificatesAsync()
    {
        try
        {
            var certificates = await _certificates.ListAsync();
            var selectedId = SelectedSiteCertificate?.Id ?? SelectedSite?.CertificateId;
            Certificates.Clear();
            foreach (var certificate in certificates.Where(certificate => certificate.Status is CertificateStatus.Active or CertificateStatus.Issued)) Certificates.Add(certificate);
            if (selectedId is { } id)
                SelectedSiteCertificate = Certificates.FirstOrDefault(certificate => certificate.Id == id);
        }
        catch { Certificates.Clear(); }
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

    private async Task LoadSitesAsync()
    {
        Sites.Clear();
        if (SelectedServer is null) return;
        try
        {
            foreach (var site in await _client.ListSitesAsync(SelectedServer.Id) ?? []) Sites.Add(site);
            SiteStatusText = Sites.Count == 0
                ? LocalizedText.Ref("webservers.site.list_empty")
                : LocalizedText.Ref("webservers.site.list_ready", Sites.Count);
        }
        catch (Exception) { SiteStatusText = LocalizedText.Ref("webservers.site.list_failed"); }
    }

    private async Task ReportSiteSaveErrorAsync(LocalizedStatus message)
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
            StatusText = LocalizedText.Ref("webservers.permission.manage_required");
            return;
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        IsOperationRunning = true;
        OperationText = LocalizedText.Ref("webservers.operation.starting", OperationName(kindKey));
        try
        {
            var operation = await start(token);
            if (operation is null)
            {
                OperationText = LocalizedText.Ref("webservers.operation.not_found");
                return;
            }
            if (operation.OperationId == Guid.Empty)
            {
                OperationText = LocalizedText.Ref("webservers.operation.rejected", ProblemText(operation.ProblemCode));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(operation.ProblemCode);
                return;
            }
            _currentOperationId = operation.OperationId;
            operation = await PollOperationAsync(operation, token);
            if (operation.State == WebServerOperationState.Succeeded)
            {
                OperationText = LocalizedText.Ref("webservers.operation.succeeded", OperationName(kindKey));
                await RefreshStatusAsync();
            }
            else if (operation.State == WebServerOperationState.Cancelled)
                OperationText = LocalizedText.Ref("webservers.operation.cancelled");
            else
            {
                OperationText = LocalizedText.Ref("webservers.operation.failed", OperationName(kindKey), ProblemText(operation.ProblemCode));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(operation.ProblemCode);
            }
        }
        catch (OperationCanceledException)
        {
            OperationText = LocalizedText.Ref("webservers.operation.cancelled");
        }
        catch (WebServerApiException exception) when (exception.ProblemCode == rethrowApiProblemCode)
        {
            throw;
        }
        catch (WebServerApiException exception)
        {
            OperationText = LocalizedText.Ref("webservers.operation.failed", OperationName(kindKey), ProblemText(exception.ProblemCode));
            await ShowPrivilegedHelperUnavailableAsyncIfNeeded(exception.ProblemCode);
        }
        catch (Exception)
        {
            OperationText = LocalizedText.Ref("webservers.operation.exception", OperationName(kindKey), LocalizedText.Get("webservers.error.request_failed"));
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
            OperationText = LocalizedText.Ref("webservers.operation.progress", OperationName(operation.Kind), OperationStage(operation.Kind, operation.Stage));
            try { await Task.Delay(PollInterval, cancellationToken); }
            catch (OperationCanceledException) { return operation; }
            var updated = await _client.GetOperationAsync(operation.OperationId, cancellationToken);
            if (updated is null) break;
            operation = updated;
        }
        return operation;
    }

    private bool HasReadPermission => _permissions.IsGranted(AppPermissions.ServerWebServersRead);
    private bool HasManagePermission => HasReadPermission && _permissions.IsGranted(AppPermissions.ServerWebServersManage);
    private bool CanRefresh => HasReadPermission && !IsLoading && !IsOperationRunning;
    private bool CanDiscover => HasReadPermission && !IsLoading && !IsOperationRunning;
    // A server from an older deployment can omit the capabilities object. Treat that response as
    // read-only instead of letting command re-evaluation crash while the DataGrid selects it.
    private bool CanRefreshStatus => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanRead == true;
    private bool CanTestConfiguration => HasReadPermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanTestConfiguration == true;
    private bool CanInstallManaged => HasManagePermission && !IsLoading && !IsOperationRunning;
    private bool CanIntegrate => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanIntegrate == true;
    private bool CanEnableAcmeHttp01 => CanSaveSite;
    private bool CanStart => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanStart == true;
    private bool CanStop => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanStop == true;
    private bool CanToggleManaged => IsManagedServerRunning ? CanStop : CanStart;
    private bool CanRestart => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanRestart == true;
    private bool CanReload => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanReload == true;
    private bool CanUninstallManaged => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.Capabilities?.CanUninstall == true;
    private bool CanSaveSite => HasManagePermission && !IsLoading && !IsOperationRunning && SelectedServer?.ManagementMode is WebServerManagementMode.Integrated or WebServerManagementMode.Managed;
    private bool CanOpenSiteEditor => CanSaveSite;
    private bool CanEditSite => CanSaveSite && SelectedSite is not null;
    private bool CanDeleteSite => CanSaveSite && SelectedSite is not null;
    private bool CanCancelOperation => IsOperationRunning;

    private static string OperationName(string kind) => kind switch
    {
        "install" => LocalizedText.Get("webservers.operation.kind.install"),
        "integrate" => LocalizedText.Get("webservers.operation.kind.integrate"),
        "uninstall" => LocalizedText.Get("webservers.operation.kind.uninstall"),
        "start" => LocalizedText.Get("webservers.operation.kind.start"),
        "stop" => LocalizedText.Get("webservers.operation.kind.stop"),
        "restart" => LocalizedText.Get("webservers.operation.kind.restart"),
        "reload" => LocalizedText.Get("webservers.operation.kind.reload"),
        "enable-acme-http01" => LocalizedText.Get("webservers.operation.kind.enable_acme_http01"),
        _ => LocalizedText.Get("webservers.operation.kind.unknown"),
    };

    private static string RuntimeStateText(WebServerRuntimeState state) => state switch
    {
        WebServerRuntimeState.Running => LocalizedText.Get("webservers.enum.runtime.running"),
        WebServerRuntimeState.Stopped => LocalizedText.Get("webservers.enum.runtime.stopped"),
        _ => LocalizedText.Get("webservers.enum.runtime.unknown"),
    };

    private static string OperationStage(string kind, string stage) => (kind, stage) switch
    {
        (_, "queued") => LocalizedText.Get("webservers.operation.stage.queued"),
        (_, "running") => LocalizedText.Get("webservers.operation.stage.running"),
        ("install", "installer_running") => LocalizedText.Get("webservers.operation.stage.installer_running"),
        ("install", "installing_package") => LocalizedText.Get("webservers.operation.stage.installing_package"),
        ("install", "downloading") => LocalizedText.Get("webservers.operation.stage.downloading"),
        ("install", "extracting") => LocalizedText.Get("webservers.operation.stage.extracting"),
        ("install", "removing_existing_installation") => LocalizedText.Get("webservers.operation.stage.removing_existing_installation"),
        ("install", "verifying_layout") => LocalizedText.Get("webservers.operation.stage.verifying_layout"),
        ("install", "validating_configuration") => LocalizedText.Get("webservers.operation.stage.validating_configuration"),
        ("install", "finalizing") => LocalizedText.Get("webservers.operation.stage.finalizing"),
        _ => LocalizedText.Get("webservers.operation.stage.unknown"),
    };

    private static string ProblemText(string? problemCode)
    {
        if (PrivilegedHelperProblemText.TryFormat(problemCode, out var helperMessage)) return helperMessage;
        if (string.IsNullOrWhiteSpace(problemCode) || !problemCode.StartsWith("webserver.", StringComparison.Ordinal))
            return LocalizedText.Get("webservers.problem.unknown");
        return LocalizedText.Get($"webservers.problem.{problemCode["webserver.".Length..]}", LocalizedText.Get("webservers.problem.unknown"));
    }

    private static string SiteSaveProblemText(string problemCode) => ProblemText(problemCode);

    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        PrivilegedHelperProblemText.TryFormat(problemCode, out _)
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;

    // nginx -t + reload is fast; a tighter poll keeps the UI responsive without spamming the host.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
}

/// <summary>Editable, UI-local representation of one domain and HTTP-port pair.</summary>
public sealed partial class WebServerSiteBindingEditor : ObservableObject
{
    public WebServerSiteBindingEditor(string domain = "", int port = 80)
    {
        Domain = domain;
        Port = port;
    }

    [ObservableProperty] private string _domain = string.Empty;
    [ObservableProperty] private int _port = 80;
}

/// <summary>Explicitly selects one HTTPS material source, so managed certificates and raw host
/// files cannot be accidentally submitted together.</summary>
public enum SiteCertificateSource { Managed, ServerFiles }
public sealed record SiteCertificateSourceOption(SiteCertificateSource Value, string Label);
