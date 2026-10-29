using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// The one answer a launch check may act on. <see cref="NotRequired"/> exists because a component the
/// connected host cannot provide (a Linux-only distribution package on Windows) is not a missing
/// dependency: refusing to open the application would invent a prerequisite nobody can satisfy.
/// </summary>
public enum ComponentAvailabilityKind { Unknown, Missing, Installed, External, Recovery, Busy, NoPermission, NotRequired }

public sealed record ApplicationAvailability(AppId ApplicationId, string? ServerIdentity, string ComponentId,
    ComponentAvailabilityKind State, string? ActualHostId = null, Guid? InstallationId = null)
{
    /// <summary>An installed service being stopped or unhealthy is still a reason to open its business console.</summary>
    public bool CanOpen => State is ComponentAvailabilityKind.Installed or ComponentAvailabilityKind.NotRequired;
}

public interface IApplicationAvailabilityUi
{
    Task<bool> ShowAsync(ApplicationAvailability availability);
}

/// <summary>Read-only application-list observations; refresh cannot launch or authorize anything.</summary>
public interface IApplicationAvailabilityProvider
{
    event EventHandler? Changed;
    bool RequiresCheck(ApplicationManifest manifest);
    ApplicationAvailability? GetCached(AppId app);
    Task<ApplicationAvailability?> RefreshAsync(ApplicationManifest manifest);
}

/// <summary>One component's contribution to a launch decision, before the alternatives are combined.</summary>
public sealed record ComponentAvailabilityReading(string ComponentId, ComponentAvailabilityKind State,
    string? ActualHostId = null, Guid? InstallationId = null);

/// <summary>Fresh, permission-gated observation on every launch, including existing-window reuse.</summary>
public sealed class ApplicationAvailabilityService : IApplicationLaunchGuard, IApplicationAvailabilityProvider, IDisposable
{
    private readonly IAuthSession session;
    private readonly IServiceProvider services;
    private readonly IApplicationAvailabilityUi ui;
    public ApplicationAvailabilityService(IAuthSession session, IServiceProvider services, IApplicationAvailabilityUi ui)
    {
        this.session = session; this.services = services; this.ui = ui;
        session.StateChanged += SessionChanged;
    }
    private void SessionChanged(object? sender, AuthSessionStateChangedEventArgs e) => Clear();
    private long _generation;
    private readonly object _cacheLock = new();
    private readonly Dictionary<(string Server, Guid User, string Application), ApplicationAvailability> _observations = [];
    public event EventHandler? Changed;
    /// <summary>Every component-backed application is checked through the same generic state route.</summary>
    public bool RequiresCheck(ApplicationManifest manifest) => ComponentCatalog.LaunchGate(manifest.Id.Value) is not null;
    public ApplicationAvailability? GetCached(AppId app)
    {
        if (ComponentCatalog.LaunchGate(app.Value) is null || session.State != AuthSessionState.Authenticated
            || session.ServiceId is not { } server || session.CurrentUser is not { } user) return null;
        lock (_cacheLock) return _observations.GetValueOrDefault((server, user.Id, app.Value));
    }

    public async Task<bool> CheckAsync(AppContext context, ManagedWindow? existingWindow)
    {
        var login = session.CurrentSession;
        var server = session.ServiceId;
        var user = session.CurrentUser?.Id;
        bool Current() => ReferenceEquals(login, session.CurrentSession) && session.State == AuthSessionState.Authenticated
            && session.ServiceId == server && session.CurrentUser?.Id == user;
        var result = await ObserveAsync(context, requestPermission: true);
        if (result is null || !Current()) return false;
        if (result.CanOpen) return true;
        var openCenter = await ui.ShowAsync(result);
        if (openCenter && Current() && result.State is ComponentAvailabilityKind.Missing or ComponentAvailabilityKind.External
            or ComponentAvailabilityKind.Recovery or ComponentAvailabilityKind.Busy)
            context.Activations.Activate(ComponentCenterNavigation.Component(server!, context.AppId.Value, result.ComponentId,
                result.State is ComponentAvailabilityKind.Recovery or ComponentAvailabilityKind.Busy ? ComponentCenterPage.Tasks : ComponentCenterPage.All));
        return false;
    }

    /// <summary>Passive display refresh never prompts for permissions, opens an app, or redirects to installation.</summary>
    public Task<ApplicationAvailability?> RefreshAsync(ApplicationManifest manifest)
    {
        if (!RequiresCheck(manifest)) return Task.FromResult<ApplicationAvailability?>(null);
        return ObserveAsync(new AppContext(manifest.Id, services.GetRequiredService<IWindowManager>(), services, manifest), requestPermission: false);
    }

    private async Task<ApplicationAvailability?> ObserveAsync(AppContext context, bool requestPermission)
    {
        if (ComponentCatalog.LaunchGate(context.AppId.Value) is not { } gate) return null;
        var login = session.CurrentSession;
        var server = session.ServiceId;
        var user = session.CurrentUser?.Id;
        if (session.State != AuthSessionState.Authenticated || login is null || server is null || user is null) return null;
        bool Current() => ReferenceEquals(login, session.CurrentSession) && session.State == AuthSessionState.Authenticated
            && session.ServiceId == server && session.CurrentUser?.Id == user;
        long generation;
        lock (_cacheLock) generation = _generation;
        using var lifetime = new CancellationTokenSource();
        EventHandler<AuthSessionStateChangedEventArgs> changed = (_, _) => { if (!Current()) lifetime.Cancel(); };
        session.StateChanged += changed;
        ApplicationAvailability result;
        try
        {
            // Reading component state is the only capability this check needs, so it asks for exactly
            // that permission on the application's own scope and for nothing else.
            var permission = context.Permissions.GetStatus(AppPermissions.ServerComponentsRead);
            if (requestPermission && permission == AppPermissionStatus.Undecided)
                permission = await context.Permissions.RequestAsync(AppPermissions.ServerComponentsRead, lifetime.Token);
            if (!Current()) return null;
            if (permission != AppPermissionStatus.Granted)
                result = new(context.AppId, null, gate.ComponentIds[0], ComponentAvailabilityKind.NoPermission);
            else
            {
                // Resolve the typed client after authentication, so it binds this login rather than process startup.
                lifetime.CancelAfter(TimeSpan.FromSeconds(15));
                var lifecycle = services.GetRequiredService<IComponentLifecycleClient>();
                var readings = await Task.WhenAll(gate.ComponentIds.Select(componentId =>
                    ComponentAvailabilityProbe.ReadAsync(lifecycle, componentId, lifetime.Token)));
                if (!Current() || lifetime.IsCancellationRequested) return null;
                result = Combine(context.AppId, server, readings);
            }
        }
        catch (OperationCanceledException)
        {
            return null; // A cancelled observation says nothing about the dependency, so it is not recorded.
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException
            or HttpRequestException or System.Text.Json.JsonException)
        {
            result = new(context.AppId, server, gate.ComponentIds[0], ComponentAvailabilityKind.Unknown);
        }
        finally { session.StateChanged -= changed; }
        if (!Current()) return null;
        bool didChange;
        lock (_cacheLock)
        {
            if (generation != _generation) return null;
            var key = (server, user.Value, context.AppId.Value);
            didChange = !_observations.TryGetValue(key, out var previous) || previous != result;
            _observations[key] = result;
        }
        if (didChange) Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>
    /// Reads the host's normalized component state. Recovery and an active operation are measured
    /// facts and outrank every ownership answer; ownership then decides whether anything is installed.
    /// A host that declares the component unsupported on its platform has no dependency to satisfy.
    /// </summary>
    public static ComponentAvailabilityKind Classify(ComponentStateView view)
    {
        if (view.State is not { } state || state.ComponentId != view.ComponentId)
            return view.Problems.Contains(ComponentProblemCodes.PlatformUnsupported)
                ? ComponentAvailabilityKind.NotRequired : ComponentAvailabilityKind.Unknown;
        if (state.RecoveryRequired) return ComponentAvailabilityKind.Recovery;
        if (view.PendingOperationId is not null) return ComponentAvailabilityKind.Busy;
        return state.Ownership switch
        {
            RuntimeOwnership.Unknown => ComponentAvailabilityKind.Unknown,
            RuntimeOwnership.External => ComponentAvailabilityKind.External,
            RuntimeOwnership.Absent => ComponentAvailabilityKind.Missing,
            _ => ComponentAvailabilityKind.Installed
        };
    }

    /// <summary>
    /// Combines alternatives into the one state a launch acts on. An installed alternative opens the
    /// application; otherwise the most decisive blocker is reported, in an order that never lets a
    /// known absence hide a pending recovery or an unknown answer.
    /// </summary>
    public static ApplicationAvailability Combine(AppId app, string server, IReadOnlyList<ComponentAvailabilityReading> readings)
    {
        var chosen = readings.FirstOrDefault(reading => reading.State == ComponentAvailabilityKind.Installed)
            ?? new[] { ComponentAvailabilityKind.Recovery, ComponentAvailabilityKind.Busy, ComponentAvailabilityKind.External,
                    ComponentAvailabilityKind.NoPermission, ComponentAvailabilityKind.Unknown, ComponentAvailabilityKind.Missing,
                    ComponentAvailabilityKind.NotRequired }
                .Select(kind => readings.FirstOrDefault(reading => reading.State == kind))
                .First(reading => reading is not null)!;
        return new(app, server, chosen.ComponentId, chosen.State, chosen.ActualHostId, chosen.InstallationId);
    }

    public void Clear()
    {
        lock (_cacheLock) { _generation++; _observations.Clear(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() { session.StateChanged -= SessionChanged; Clear(); }
}

/// <summary>
/// The one read every dependency surface uses. It never turns a transport or permission problem into
/// "not installed": an unreadable component is <see cref="ComponentAvailabilityKind.Unknown"/>, so a
/// settings page and a launch check can never disagree about what absence means.
/// </summary>
public static class ComponentAvailabilityProbe
{
    public static async Task<ComponentAvailabilityReading> ReadAsync(IComponentLifecycleClient lifecycle,
        string componentId, CancellationToken ct)
    {
        try
        {
            var view = await lifecycle.StateAsync(componentId, ct);
            return new(componentId, ApplicationAvailabilityService.Classify(view), view.State?.HostId, view.CommittedOperationId);
        }
        catch (HttpRequestException ex)
        {
            return new(componentId, ex.StatusCode is System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.Unauthorized ? ComponentAvailabilityKind.NoPermission : ComponentAvailabilityKind.Unknown);
        }
        catch (InvalidOperationException ex) when (ex.Message == "component.permission_denied")
        {
            return new(componentId, ComponentAvailabilityKind.NoPermission);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException
            or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            return new(componentId, ComponentAvailabilityKind.Unknown);
        }
    }
}
