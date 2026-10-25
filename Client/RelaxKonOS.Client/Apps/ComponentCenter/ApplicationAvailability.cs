using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public enum ComponentAvailabilityKind { Unknown, Missing, Running, Stopped, Faulted, External, Recovery, Busy, Unsupported, NoPermission }

public sealed record ApplicationAvailability(AppId ApplicationId, string? ServerIdentity, string ComponentId,
    ComponentAvailabilityKind State, string? ActualHostId = null, Guid? InstallationId = null)
{
    // An installed service being stopped or unhealthy is still a reason to open its business console.
    public bool CanOpen => State is ComponentAvailabilityKind.Running or ComponentAvailabilityKind.Stopped or ComponentAvailabilityKind.Faulted;
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
    private readonly Dictionary<(string Server, Guid User, string Component), ApplicationAvailability> _observations = [];
    public event EventHandler? Changed;
    /// <summary>Only components with a registered availability adapter may gate an application.</summary>
    public bool RequiresCheck(ApplicationManifest manifest) => ComponentCatalog.LaunchGate(manifest.Id.Value) is not null;
    public ApplicationAvailability? GetCached(AppId app)
    {
        var component = ComponentCatalog.LaunchGate(app.Value);
        if (component is null || session.State != AuthSessionState.Authenticated || session.ServiceId is not { } server || session.CurrentUser is not { } user)
            return null;
        lock (_cacheLock) return _observations.GetValueOrDefault((server, user.Id, component.ComponentId));
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
        if (openCenter && Current() && result.State is ComponentAvailabilityKind.Missing or ComponentAvailabilityKind.External or ComponentAvailabilityKind.Recovery or ComponentAvailabilityKind.Busy)
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
        var component = ComponentCatalog.LaunchGate(context.AppId.Value);
        if (component is null) return null;
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
            var permission = context.Permissions.GetStatus(AppPermissions.ServerProxyRead);
            if (requestPermission && permission == AppPermissionStatus.Undecided)
                permission = await context.Permissions.RequestAsync(AppPermissions.ServerProxyRead, lifetime.Token);
            if (!Current()) return null;
            if (permission != AppPermissionStatus.Granted)
                result = new(context.AppId, null, component.ComponentId, ComponentAvailabilityKind.NoPermission);
            else
            {
                // Resolve typed clients after authentication, so they bind this login rather than process startup.
                lifetime.CancelAfter(TimeSpan.FromSeconds(15));
                var runtime = services.GetRequiredService<IRuntimeCatalogClient>();
                var proxy = services.GetRequiredService<IProxyServerClient>();
                var installation = runtime.InstallationAsync(lifetime.Token);
                var status = proxy.StatusAsync(lifetime.Token);
                await Task.WhenAll(installation, status);
                result = Classify(context.AppId, server, component.ComponentId, await installation, await status);
            }
        }
        catch (OperationCanceledException) { result = new(context.AppId, server, component.ComponentId, ComponentAvailabilityKind.Unknown); }
        catch (HttpRequestException ex)
        {
            result = new(context.AppId, ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized ? null : server,
                component.ComponentId, ex.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                    ? ComponentAvailabilityKind.NoPermission : ComponentAvailabilityKind.Unknown);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException)
        { result = new(context.AppId, server, component.ComponentId, ComponentAvailabilityKind.Unknown); }
        finally { session.StateChanged -= changed; }
        if (!Current()) return null;
        bool didChange;
        lock (_cacheLock)
        {
            if (generation != _generation) return null;
            var key = (server, user.Value, component.ComponentId);
            didChange = !_observations.TryGetValue(key, out var previous) || previous != result;
            _observations[key] = result;
        }
        if (didChange) Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public static ApplicationAvailability Classify(AppId app, string server, string component,
        RuntimeInstallationSnapshot installation, ProxyServerStatus status)
    {
        var state = installation.Installation.RuntimeId != component ? ComponentAvailabilityKind.Unknown
            : !status.Supported ? ComponentAvailabilityKind.Unsupported
            : installation.Installation.RecoveryRequired || status.RecoveryRequired ? ComponentAvailabilityKind.Recovery
            : installation.PendingOperationId is not null ? ComponentAvailabilityKind.Busy
            : installation.Installation.Ownership == RuntimeOwnership.Unknown ? ComponentAvailabilityKind.Unknown
            : installation.Installation.Ownership == RuntimeOwnership.External ? ComponentAvailabilityKind.External
            : installation.Installation.Ownership == RuntimeOwnership.Absent
                ? status.Installed ? ComponentAvailabilityKind.Unknown : ComponentAvailabilityKind.Missing
            : !status.Installed ? ComponentAvailabilityKind.Unknown
            : status.ProblemCode is not null || installation.ProblemCode is not null ? ComponentAvailabilityKind.Faulted
            : status.Running ? ComponentAvailabilityKind.Running : ComponentAvailabilityKind.Stopped;
        return new(app, server, component, state, installation.Installation.HostId, installation.Installation.InstallationId);
    }
    public void Clear()
    {
        lock (_cacheLock) { _generation++; _observations.Clear(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() { session.StateChanged -= SessionChanged; Clear(); }
}
