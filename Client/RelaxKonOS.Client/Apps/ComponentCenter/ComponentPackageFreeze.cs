using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;

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
    private readonly Dictionary<string, ComponentStateView> _componentStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComponentPlanResult> _componentFreezes = new(StringComparer.Ordinal);
    [ObservableProperty] private bool isResolvingPackage;
    [ObservableProperty] private LocalizedStatus packageFreezeNotice = LocalizedStatus.Literal("");

    private ComponentStateView? SelectedComponentState => SelectedDefinition?.ComponentId is { } id
        ? _componentStates.GetValueOrDefault(id) : null;
    private ComponentPlanResult? SelectedComponentFreeze => SelectedDefinition?.ComponentId is { } id
        ? _componentFreezes.GetValueOrDefault(id) : null;

    /// <summary>
    /// Only components the center does not install itself use this panel: the platform decides the
    /// install mode, so the honest answer is the host's own candidate, not a synthetic release list.
    /// </summary>
    public bool ShowPackageFreeze => _lifecycle is not null && SelectedDefinition is { Integration: ComponentIntegration.Application };
    public bool CanResolvePackage => ShowPackageFreeze && !IsResolvingPackage && !Runtime.IsInvalidated;
    public bool HasPackageFreeze => SelectedComponentFreeze is not null;
    public LocalizedStatus PackageModeText => SelectedComponentState is { } state
        ? LocalizedStatus.Join(" · ", state.Modes.Select(mode => LocalizedStatus.Format("component_center.package.mode",
            LocalizedStatus.Key("component_center.package.mode." + mode.Mode.ToString().ToLowerInvariant()),
            LocalizedStatus.Key("component_center.package.mode." + (mode.Available ? "available" : "planned")), mode.Platform)))
        : LocalizedStatus.Key("component_center.package.unresolved");
    public LocalizedStatus PackageInstalledText => LocalizedStatus.Format("component_center.package.installed",
        LocalizedStatus.Literal(SelectedComponentState?.State?.ActiveVersion ?? "—"));
    public LocalizedStatus PackageCandidateText => LocalizedStatus.Format("component_center.package.candidate",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package?.Version ?? "—"));
    public LocalizedStatus PackageRepositoryText => LocalizedStatus.Format("component_center.package.repository",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package is { } package
            ? package.BaseUri + " " + package.Suite + "/" + package.Area + " " + package.Architecture : "—"));
    public LocalizedStatus PackageDigestText => LocalizedStatus.Format("component_center.package.digest",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package?.Sha256 ?? "—"));
    public LocalizedStatus PackageAlsoInstallsText => LocalizedStatus.Format("component_center.package.also",
        LocalizedStatus.Literal(SelectedComponentFreeze?.Target?.Package is { AlsoInstalls.Count: > 0 } package
            ? string.Join(", ", package.AlsoInstalls) : "—"));
    /// <summary>The refusal is part of the answer: a frozen candidate nobody can install must say so.</summary>
    public LocalizedStatus PackageVerdictText => SelectedComponentFreeze is not { } freeze || freeze.Problems.Count == 0
        ? LocalizedStatus.Literal("")
        : LocalizedStatus.Format("component_center.package.blocked",
            LocalizedStatus.Join(" · ", freeze.Problems.Select(ProblemText)));

    private static LocalizedStatus ProblemText(string code) => LocalizedStatus.Key(code switch
    {
        ComponentProblemCodes.ExecutionUnavailable => "component_center.package.problem.execution_unavailable",
        ComponentProblemCodes.PackageUnresolved => "component_center.package.problem.unresolved",
        ComponentProblemCodes.PackageIndexUnavailable => "component_center.package.problem.index_unavailable",
        ComponentProblemCodes.PackageVersionMismatch => "component_center.package.problem.version_mismatch",
        ComponentProblemCodes.PackageAlreadyInstalled => "component_center.package.problem.already_installed",
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
    [RelayCommand] private async Task ResolvePackageAsync()
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
            if (view.State is { } state)
            {
                var request = new ComponentPlanRequest(componentId, state.HostId,
                    state.Ownership == RuntimeOwnership.Absent ? RuntimeChange.Install : RuntimeChange.Repair,
                    state.Revision, state.Mode, null, 0, 0, null, null, KeepData: true, "component-center-freeze");
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
        finally { IsResolvingPackage = false; NotifyPackage(); }
    }

    private void NotifyPackage()
    {
        foreach (var property in new[] { nameof(ShowPackageFreeze), nameof(CanResolvePackage), nameof(HasPackageFreeze),
            nameof(PackageModeText), nameof(PackageInstalledText), nameof(PackageCandidateText), nameof(PackageRepositoryText),
            nameof(PackageDigestText), nameof(PackageAlsoInstallsText), nameof(PackageVerdictText) })
            OnPropertyChanged(property);
    }
}
