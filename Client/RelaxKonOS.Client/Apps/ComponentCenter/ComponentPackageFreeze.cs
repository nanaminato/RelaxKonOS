using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Client.Services.Installation;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// Reads one component's normalized state and freezes one change through the shared component route.
/// It never executes: the plan it receives names the exact candidate and whether this host can
/// install it at all.
/// </summary>
public interface IComponentLifecycleClient
{
    Task<ComponentStateView> StateAsync(string componentId, CancellationToken ct);
    Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct);
}

public sealed class ComponentLifecycleClient(HttpClient http, IAuthSession session) : IComponentLifecycleClient
{
    private readonly object? _login = session.CurrentSession;
    private readonly Guid? _actor = session.CurrentUser?.Id;

    public async Task<ComponentStateView> StateAsync(string componentId, CancellationToken ct) =>
        await SendAsync<ComponentStateView>(HttpMethod.Get, ComponentRoutes.State(componentId), null, ct);

    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct) =>
        await SendAsync<ComponentPlanResult>(HttpMethod.Post, ComponentRoutes.Plan(request.ComponentId), request, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object? body, CancellationToken ct) where T : class
    {
        Verify();
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        Verify();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(session.EffectiveBaseUrl!), route.TrimStart('/')));
        if (body is not null)
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, RelaxKonOSJsonOptions.Default))
            { Headers = { ContentType = new("application/json") } };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Verify();
        if (!response.IsSuccessStatusCode)
        {
            // The status is the only thing a client may act on; server text is never surfaced.
            var code = response.StatusCode is System.Net.HttpStatusCode.NotFound ? "component.unknown"
                : response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                    ? "component.permission_denied"
                : response.StatusCode is System.Net.HttpStatusCode.Conflict ? "component.platform_unsupported"
                : "component.state_unavailable";
            throw new InvalidOperationException(code);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > 262144) throw new InvalidDataException("component.response_invalid");
            buffer.Write(chunk, 0, read);
        }
        Verify();
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), RelaxKonOSJsonOptions.Default)
            ?? throw new InvalidDataException("component.response_invalid");
    }

    private void Verify()
    {
        if (!ReferenceEquals(session.CurrentSession, _login) || session.CurrentUser?.Id != _actor)
            throw new InvalidOperationException("component.session_changed");
    }
}

public partial class ComponentCenterViewModel
{
    private readonly IComponentLifecycleClient? _lifecycle;
    [ObservableProperty] private ComponentPinnedArchive? selectedPinnedArchive;
    public IReadOnlyList<ComponentPinnedArchive> PinnedArchiveCandidates =>
        SelectedComponentState?.PinnedArchives ?? Array.Empty<ComponentPinnedArchive>();
    public bool HasPinnedArchiveCandidates => PinnedArchiveCandidates.Count > 0;
    partial void OnSelectedPinnedArchiveChanged(ComponentPinnedArchive? value)
    {
        if (SelectedDefinition is { } definition) _componentFreezes.Remove(definition.ComponentId);
        NotifyPackage();
    }
    private readonly Dictionary<string, InstallationTaskViewModel> _packageInstallations = new(StringComparer.Ordinal);
    public InstallationTaskViewModel? PackageInstallation => SelectedDefinition?.ComponentId is { } id
        ? _packageInstallations.GetValueOrDefault(id) : null;
    public Func<ComponentPlanResult, Task<bool>>? ConfirmPackageInstallAsync { get; set; }
    public void AttachPackageInstallation(string componentId, InstallationTaskViewModel installation)
    {
        _packageInstallations.Add(componentId, installation);
        installation.PropertyChanged += PackageInstallationChanged;
        NotifyPackage();
    }
    private void PackageInstallationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { NotifyPackage(); NotifyGit(); }
    public bool CanApplyPackagePlan => CanResolvePackage && Runtime.CanManage && PackageInstallation is not null
        && !_packageInstallations.Values.Any(task => task.IsActive) && SelectedComponentFreeze is { Allowed: true } freeze
        && freeze.Request?.Change is RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Uninstall
        && freeze.State?.Target.Os == HostPlatform
        && (freeze.Target?.Package is not null
                && (SelectedPackageManager is null || PackageManagers.Contains(SelectedPackageManager))
            || freeze.Target?.Pin is not null);
    public bool ShowPackageInstall => IsSystemPackage || CanInstallPackage;
    public bool CanInstallPackage => CanApplyPackagePlan && SelectedComponentFreeze?.Request?.Change == RuntimeChange.Install;
    public bool CanChangeInstalledPackage => CanResolvePackage && Runtime.CanManage && PackageInstallation is not null
        && !_packageInstallations.Values.Any(task => task.IsActive)
        && SelectedComponentState?.State is { Mode: ComponentSourceMode.SystemPackage, Ownership: RuntimeOwnership.Managed, RecoveryRequired: false };
    /// <summary>This host installs the component from its distribution's own package manager.</summary>
    public bool IsSystemPackage => HostPlatform == "linux" && DeclaresSourceOnHost(ComponentSourceKind.SystemPackage);
    /// <summary>This host installs the component by downloading one pinned, verified archive.</summary>
    public bool IsDirectDownload => HostPlatform == "windows" && DeclaresSourceOnHost(ComponentSourceKind.ManagedArchive);
    private bool DeclaresSourceOnHost(ComponentSourceKind kind) => SelectedDefinition?.Sources.Any(source =>
        source.Kind == kind && source.Platforms.Contains(HostPlatform)) == true;
    /// <summary>
    /// Stable instance per (component, platform, contents): the combo box rebinds its source on every
    /// notification, and a fresh array each time would make the control write its selection back in
    /// the middle of its own update.
    /// </summary>
    public IReadOnlyList<string> PackageManagers
    {
        get
        {
            var managers = SelectedComponentState is { State: not null, Problems.Count: 0 } view
                ? view.Modes.Where(mode => mode.Available && mode.Platform == HostPlatform && mode.PackageManager is not null)
                    .Select(mode => mode.PackageManager!).Distinct().ToArray()
                : [];
            var stamp = (SelectedDefinition?.ComponentId ?? "") + "|" + HostPlatform + "|" + string.Join(',', managers);
            if (_packageManagers is not null && _packageManagersStamp == stamp) return _packageManagers;
            _packageManagersStamp = stamp; _packageManagers = managers;
            return managers;
        }
    }
    private IReadOnlyList<string>? _packageManagers;
    private string? _packageManagersStamp;
    /// <summary>Only a host that reports more than one connected manager needs a selector.</summary>
    public bool HasPackageManagerChoice => PackageManagers.Count > 1;
    public LocalizedStatus SelectedPackageManagerText => LocalizedStatus.Literal(
        SelectedPackageManager ?? LocalizedText.Get("component_center.package.unresolved"));
    [ObservableProperty] private string? selectedPackageManager;
    partial void OnSelectedPackageManagerChanged(string? value)
    {
        // The control clears its own selection while its source is being replaced; that is not a user
        // choice and must not discard a frozen plan.
        if (_retargetingPackageManager || value is null) return;
        if (SelectedDefinition is { } definition) _componentFreezes.Remove(definition.ComponentId);
        NotifyPackage();
    }
    private bool _retargetingPackageManager;
    public LocalizedStatus PackageMethodTitle => LocalizedStatus.Key(IsSystemPackage ? "component_center.native.package_title" : "component_center.native.download_title");
    /// <summary>The method is platform-specific, so its explanation must be too.</summary>
    public LocalizedStatus PackageMethodHint => LocalizedStatus.Key(IsSystemPackage
        ? "component_center.native.hint.package" : "component_center.native.hint.download");
    /// <summary>Names this host's own method once, instead of describing both platforms at once.</summary>
    public LocalizedStatus PlatformMethodText => !HostPlatformKnown
        ? LocalizedStatus.Key("component_center.native.platform_unknown")
        : IsSystemPackage
            ? LocalizedStatus.Format("component_center.native.platform_method.package",
                LocalizedStatus.Literal(SelectedPackageManager ?? LocalizedText.Get("component_center.package.unresolved")))
            : IsDirectDownload
                ? LocalizedStatus.Key("component_center.native.platform_method.download")
                : LocalizedStatus.Key("component_center.native.platform_method.none");
    /// <summary>Update and uninstall are only frozen for distribution packages in this build.</summary>
    public bool ShowPackageChangeActions => IsSystemPackage;
    /// <summary>A direct download can only be installed here; saying so is part of the answer.</summary>
    public bool ShowInstallOnlyNote => IsDirectDownload;
    [RelayCommand] private async Task UpdatePackageAsync() => await PreparePackageChangeAsync(RuntimeChange.Update);
    [RelayCommand] private async Task UninstallPackageAsync() => await PreparePackageChangeAsync(RuntimeChange.Uninstall);
    private async Task PreparePackageChangeAsync(RuntimeChange change)
    {
        if (!CanChangeInstalledPackage || SelectedDefinition is not { } component) return;
        await ResolvePackageChangeAsync(change);
        if (SelectedDefinition?.ComponentId == component.ComponentId && SelectedComponentFreeze?.Request?.Change == change)
            await ApplyPackagePlanAsync();
    }
    public async Task RefreshPackageInstallationAsync(string componentId)
    {
        _componentFreezes.Clear(); // A completed host mutation can change dependencies of another frozen component.
        if (_lifecycle is not null && !Runtime.IsInvalidated)
        {
            var affected = componentId is "frpc" or "frps" ? new[] { "frpc", "frps" } : new[] { componentId };
            foreach (var id in affected)
                _componentStates[id] = await _lifecycle.StateAsync(id, _inspectionLifetime.Token);
        }
        if (componentId == "nginx" && NginxSelection is not null) await NginxSelection.RefreshCommand.ExecuteAsync(null);
        if (componentId == "git" && GitEngine is not null) await GitEngine.RefreshEngineStatusCommand.ExecuteAsync(null);
        // A fresh host reading can be the first time the target platform is known, and that answer
        // decides which components and which install method apply at all.
        RefreshTree();
    }
    [RelayCommand] private async Task InstallPackageAsync()
    {
        if (CanInstallPackage) await ApplyPackagePlanAsync();
    }
    private async Task ApplyPackagePlanAsync()
    {
        if (!CanApplyPackagePlan || SelectedComponentFreeze is not { } freeze || ConfirmPackageInstallAsync is null) return;
        if (!await ConfirmPackageInstallAsync(freeze) || !CanApplyPackagePlan
            || !ReferenceEquals(freeze, SelectedComponentFreeze)) return;
        var execution = new ComponentExecuteRequest(freeze.Request!.ComponentId, freeze.Request, freeze.Target);
        object request = execution.ComponentId switch
        {
            "nginx" => new NginxInstallationRequest(true, FrozenPackage: execution),
            "squid" => new SquidInstallationRequest(true, execution),
            "samba" => new SmbInstallationRequest(true, execution),
            "docker" => new DockerInstallationRequest(true, execution),
            "git" => new GitInstallationRequest(true, execution),
            "frpc" or "frps" => new FrpInstallationRequest(true, FrozenArchive: execution),
            _ => throw new InvalidOperationException("component.unknown")
        };
        await PackageInstallation!.SubmitAsync(freeze.Request!.Change switch { RuntimeChange.Update => InstallationOperationKind.Upgrade, RuntimeChange.Uninstall => InstallationOperationKind.Uninstall, _ => InstallationOperationKind.Install }, request);
        NotifyPackage();
    }

    public bool CanRefreshPackageSources => SelectedDefinition?.ComponentId is "nginx" or "squid" or "samba" or "docker" or "git"
        && SelectedComponentState?.Modes.Any(mode => mode.Platform == "linux" && mode.Mode == ComponentSourceMode.SystemPackage && mode.Available) == true
        && !SelectedComponentState.Problems.Contains(ComponentProblemCodes.PlatformUnsupported) && Runtime.CanManage && !Runtime.IsInvalidated
        && PackageInstallation is not null && !_packageInstallations.Values.Any(task => task.IsActive);
    [RelayCommand] private async Task RefreshPackageSourcesAsync()
    {
        if (!CanRefreshPackageSources || SelectedDefinition is not { } component || ConfirmPackageRecoveryAsync is null
            || !await ConfirmPackageRecoveryAsync(component.ComponentId, Guid.Empty, ComponentRecoveryAction.RefreshSources)) return;
        if (!CanRefreshPackageSources || SelectedDefinition.ComponentId != component.ComponentId) return;
        var maintenance = new ComponentRecoveryRequest(component.ComponentId, Guid.Empty, ComponentRecoveryAction.RefreshSources);
        object request = component.ComponentId switch {
            "nginx" => new NginxInstallationRequest(true, Recovery: maintenance),
            "squid" => new SquidInstallationRequest(true, Recovery: maintenance),
            "samba" => new SmbInstallationRequest(true, Recovery: maintenance),
            "docker" => new DockerInstallationRequest(true, Recovery: maintenance),
            "git" => new GitInstallationRequest(true, null, maintenance),
            _ => throw new InvalidOperationException("component.unknown") };
        await PackageInstallation!.SubmitAsync(InstallationOperationKind.Repair, request);
    }

    public bool CanRecoverPackage => SelectedDefinition?.ComponentId is "nginx" or "squid" or "samba" or "docker" or "git"
        && Runtime.CanManage && !Runtime.IsInvalidated && PackageInstallation is not null
        && !_packageInstallations.Values.Any(task => task.IsActive)
        && SelectedComponentState is { State.RecoveryRequired: true, PendingOperationId: not null };
    public LocalizedStatus PackageRecoveryText => SelectedComponentState?.PendingOperationId is { } id
        ? LocalizedStatus.Format("component_center.package.recovery_target", id) : LocalizedStatus.Literal("");
    public Func<string, Guid, ComponentRecoveryAction, Task<bool>>? ConfirmPackageRecoveryAsync { get; set; }
    [RelayCommand] private Task StopPackageRecoveryAsync() => RecoverPackageAsync(ComponentRecoveryAction.Stop);
    [RelayCommand] private Task ClosePackageRecoveryAsync() => RecoverPackageAsync(ComponentRecoveryAction.Close);
    private async Task RecoverPackageAsync(ComponentRecoveryAction action)
    {
        if (!CanRecoverPackage || SelectedDefinition is not { } component || SelectedComponentState?.PendingOperationId is not { } original
            || ConfirmPackageRecoveryAsync is null || !await ConfirmPackageRecoveryAsync(component.ComponentId, original, action)) return;
        if (!CanRecoverPackage || SelectedDefinition.ComponentId != component.ComponentId || SelectedComponentState?.PendingOperationId != original) return;
        var recovery = new ComponentRecoveryRequest(component.ComponentId, original, action);
        object request = component.ComponentId switch {
            "nginx" => new NginxInstallationRequest(true, Recovery: recovery),
            "squid" => new SquidInstallationRequest(true, Recovery: recovery),
            "samba" => new SmbInstallationRequest(true, Recovery: recovery),
            "docker" => new DockerInstallationRequest(true, Recovery: recovery),
            "git" => new GitInstallationRequest(true, null, recovery),
            _ => throw new InvalidOperationException("component.unknown") };
        await PackageInstallation!.SubmitAsync(InstallationOperationKind.Repair, request);
    }

    private readonly Dictionary<string, ComponentStateView> _componentStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComponentPlanResult> _componentFreezes = new(StringComparer.Ordinal);
    [ObservableProperty] private bool isResolvingPackage;
    [ObservableProperty] private LocalizedStatus packageFreezeNotice = LocalizedStatus.Literal("");

    private ComponentStateView? SelectedComponentState => SelectedDefinition?.ComponentId is { } id
        ? _componentStates.GetValueOrDefault(id) : null;
    private ComponentPlanResult? SelectedComponentFreeze => SelectedDefinition?.ComponentId is { } id
        ? _componentFreezes.GetValueOrDefault(id) : null;

    /// <summary>
    /// Package-backed installation uses the host's resolved candidate and the durable installation
    /// worker, rather than treating a distribution package as a managed archive release.
    /// </summary>
    public bool ShowPackageFreeze => _lifecycle is not null && HostPlatformKnown
        && (DeclaresSourceOnHost(ComponentSourceKind.SystemPackage) || DeclaresSourceOnHost(ComponentSourceKind.ManagedArchive))
        && (SelectedDefinition?.ComponentId != "git" || IsSystemPackage)
        && SelectedDefinition?.ComponentId is "nginx" or "squid" or "samba" or "docker" or "git" or "frpc" or "frps";
    public bool CanResolvePackage => ShowPackageFreeze && Runtime.CanRead && !IsResolvingPackage && PackageInstallation?.IsActive != true && !Runtime.IsInvalidated;
    public bool HasPackageFreeze => SelectedComponentFreeze is not null;
    public LocalizedStatus PackageInstalledText => LocalizedStatus.Format("component_center.package.installed",
        LocalizedStatus.Literal(SelectedComponentState?.State?.ActiveVersion ?? "—"));
    public LocalizedStatus PackageCandidateText => LocalizedStatus.Format("component_center.package.candidate",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package?.Version ?? SelectedComponentFreeze?.Target?.Pin?.Version ?? "—"));
    public LocalizedStatus PackageRepositoryText => LocalizedStatus.Format("component_center.package.repository",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package is { } package
            ? package.BaseUri + " " + package.Suite + "/" + package.Area + " " + package.Architecture : "—"));
    public LocalizedStatus PackageDigestText => LocalizedStatus.Format("component_center.package.digest",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package?.Sha256 ?? SelectedComponentFreeze?.Target?.Pin?.Sha256 ?? "—"));
    public LocalizedStatus PackageAlsoInstallsText => LocalizedStatus.Format("component_center.package.also",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package is { AlsoInstalls.Count: > 0 } package
            ? string.Join(", ", package.AlsoInstalls.Select(item => item.FileName)) : "—"));
    /// <summary>The refusal is part of the answer: a frozen candidate nobody can install must say so.</summary>
    public LocalizedStatus PackageVerdictText => SelectedComponentFreeze is not { } freeze || freeze.Problems.Count == 0
        ? LocalizedStatus.Literal("")
        : LocalizedStatus.Format("component_center.package.blocked",
            LocalizedStatus.Join(" · ", freeze.Problems.Select(ProblemText)));

    private static LocalizedStatus ProblemText(string code) => LocalizedStatus.Key(code switch
    {
        ComponentProblemCodes.HostPolicyRequired => "component_center.package.problem.host_policy_required",
        ComponentProblemCodes.ExecutionUnavailable => "component_center.package.problem.execution_unavailable",
        ComponentProblemCodes.PackageUnresolved => "component_center.package.problem.unresolved",
        ComponentProblemCodes.PackageIndexUnavailable => "component_center.package.problem.index_unavailable",
        ComponentProblemCodes.PackageVersionMismatch => "component_center.package.problem.version_mismatch",
        ComponentProblemCodes.PackageAlreadyInstalled => "component_center.package.problem.already_installed",
        ComponentProblemCodes.PackageNoUpdate => "component_center.native.no_update",
        ComponentProblemCodes.PackageOwnershipRequired => "component_center.native.ownership_required",
        ComponentProblemCodes.PackageAbsent => "component_center.package.problem.absent",
        ComponentProblemCodes.StateRevisionConflict => "component_center.package.problem.revision_conflict",
        ComponentProblemCodes.PlatformUnsupported => "component_center.package.problem.platform_unsupported",
        ComponentProblemCodes.SourceModeUnsupported => "component_center.package.problem.source_mode_unsupported",
        ComponentProblemCodes.StateUnavailable => "component_center.package.problem.state_unavailable",
        ComponentProblemCodes.RequestInvalid => "component_center.package.problem.request_invalid",
        ComponentProblemCodes.RecoveryRequired => "component_center.package.problem.recovery_required",
        _ => "component_center.package.problem.unknown"
    }, code);

    /// <summary>Reads the host's own resolution. It freezes a candidate; it never installs one.</summary>
    [RelayCommand] private Task ResolvePackageAsync() => ResolvePackageChangeAsync(null);
    private async Task ResolvePackageChangeAsync(RuntimeChange? change)
    {
        if (!CanResolvePackage || SelectedDefinition is not { } definition) return;
        var componentId = definition.ComponentId;
        IsResolvingPackage = true;
        PackageFreezeNotice = LocalizedStatus.Key("component_center.package.running");
        NotifyPackage();
        try
        {
            var view = await _lifecycle!.StateAsync(componentId, _inspectionLifetime.Token);
            _componentStates[componentId] = view;
            if (SelectedDefinition?.ComponentId == componentId)
            {
                // Fixing the manager for this component is part of preparing the plan, so it must not
                // discard the freeze this same method is about to store.
                _retargetingPackageManager = true;
                try { SelectedPackageManager = PackageManagers.FirstOrDefault(); }
                finally { _retargetingPackageManager = false; }
            }
            if (view.State is { } state)
            {
                var pins = view.PinnedArchives ?? [];
                var pin = pins.FirstOrDefault(item => item.ReleaseId == SelectedPinnedArchive?.ReleaseId) ?? pins.FirstOrDefault();
                if (SelectedDefinition?.ComponentId == componentId && pin is not null) SelectedPinnedArchive = pin;
                var request = new ComponentPlanRequest(componentId, state.HostId,
                    change ?? (state.Ownership == RuntimeOwnership.Absent ? RuntimeChange.Install : RuntimeChange.Update),
                    state.Revision, state.Mode, pin is null ? null : "host-pins", pin is null ? 0 : view.PinRevision,
                    0, pin?.ReleaseId, pin?.Version, KeepData: true, Guid.NewGuid().ToString("N"));
                _componentFreezes[componentId] = await _lifecycle.PlanAsync(request, _inspectionLifetime.Token);
                PackageFreezeNotice = LocalizedStatus.Key("component_center.package.complete");
            }
            else
            {
                _componentFreezes.Remove(componentId);
                PackageFreezeNotice = LocalizedStatus.Format("component_center.package.blocked",
                    LocalizedStatus.Join(" · ", view.Problems.Select(ProblemText)));
            }
        }
        catch (OperationCanceledException) when (_inspectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or OperationCanceledException)
        {
            PackageFreezeNotice = LocalizedStatus.Key(ex.Message switch
            {
                "component.permission_denied" => "component_center.package.denied",
                "component.unknown" => "component_center.package.unsupported",
                _ => "component_center.package.failed"
            });
        }
        finally { IsResolvingPackage = false; RefreshTree(); }
    }

    private void NotifyPackage()
    {
        foreach (var property in new[] { nameof(ShowPackageInstall), nameof(CanApplyPackagePlan), nameof(CanChangeInstalledPackage), nameof(IsSystemPackage), nameof(IsDirectDownload), nameof(PackageManagers), nameof(HasPackageManagerChoice), nameof(SelectedPackageManagerText), nameof(PackageMethodTitle), nameof(PackageMethodHint), nameof(PlatformMethodText), nameof(ShowPackageChangeActions), nameof(ShowInstallOnlyNote), nameof(CanRefreshPackageSources), nameof(CanRecoverPackage), nameof(PackageRecoveryText), nameof(PinnedArchiveCandidates), nameof(HasPinnedArchiveCandidates), nameof(PackageInstallation), nameof(CanInstallPackage), nameof(ShowPackageFreeze), nameof(CanResolvePackage), nameof(HasPackageFreeze),
            nameof(PackageInstalledText), nameof(PackageCandidateText), nameof(PackageRepositoryText),
            nameof(PackageDigestText), nameof(PackageAlsoInstallsText), nameof(PackageVerdictText) })
            OnPropertyChanged(property);
        InstallPackageCommand.NotifyCanExecuteChanged();
        foreach (var property in new[] { nameof(ShowNotIntegrated), nameof(ShowHeaderOpenApplication), nameof(SelectedStatusText), nameof(SelectedVersionText),
            nameof(HasSelectedVersion), nameof(SelectedHostSources), nameof(SelectedOtherSources) })
            OnPropertyChanged(property);
    }
}
