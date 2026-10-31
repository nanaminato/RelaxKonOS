using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

internal static class ApplicationAvailabilityChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: Availability: " + message);
    }
    public static async Task RunAsync()
    {
        // The gate is derived from the catalog's own application association, so every component-backed
        // application is gated and no application gains a dependency it does not declare.
        Check(ComponentCatalog.LaunchGate("relaxkonos.proxy-server")?.ComponentIds.SequenceEqual(new[] { "sing-box" }) == true
            && ComponentCatalog.LaunchGate("relaxkonos.proxy")?.ComponentIds.SequenceEqual(new[] { "mihomo" }) == true
            && ComponentCatalog.LaunchGate("relaxkonos.docker")?.ComponentIds.SequenceEqual(new[] { "docker" }) == true
            && ComponentCatalog.LaunchGate("relaxkonos.webservers")?.ComponentIds.SequenceEqual(new[] { "nginx" }) == true
            && ComponentCatalog.LaunchGate("relaxkonos.file-services")?.ComponentIds.SequenceEqual(new[] { "samba" }) == true
            && ComponentCatalog.LaunchGate("relaxkonos.squid")?.ComponentIds.SequenceEqual(new[] { "squid" }) == true
            && ComponentCatalog.LaunchGate("relaxkonos.git")?.ComponentIds.SequenceEqual(new[] { "git" }) == true,
            "Every component-backed application is gated through the catalog association");
        Check(ComponentCatalog.LaunchGate("relaxkonos.tunnels")?.ComponentIds.Order().SequenceEqual(new[] { "frpc", "frps" }) == true,
            "Tunnel Manager lists frpc and frps as alternatives instead of requiring both");
        Check(ComponentCatalog.LaunchGate("relaxkonos.settings") is null
            && ComponentCatalog.LaunchGate("relaxkonos.component-center") is null,
            "An application without a component keeps no fake dependency");

        var target = new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.SystemPackage, "systemd");
        static ComponentStateView View(string componentId, RuntimeOwnership ownership,
            bool recovery = false, Guid? pending = null, RuntimeTarget? target = null) => new(componentId,
            new(componentId, "host-a", 3, ownership, ComponentSourceMode.SystemPackage,
                target ?? new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.SystemPackage, "systemd"),
                "1.24.0", recovery),
            [new(ComponentSourceMode.SystemPackage, "linux", true, null, "apt")], [], null, 0, null, pending);
        Check(ApplicationAvailabilityService.Classify(View("nginx", RuntimeOwnership.Managed)) == ComponentAvailabilityKind.Installed,
            "An installed component is a measured state even while its service is stopped");
        Check(ApplicationAvailabilityService.Classify(View("nginx", RuntimeOwnership.Managed, recovery: true))
            == ComponentAvailabilityKind.Recovery, "Recovery blocks normal activation");
        Check(ApplicationAvailabilityService.Classify(View("nginx", RuntimeOwnership.Managed, pending: Guid.NewGuid()))
            == ComponentAvailabilityKind.Busy, "An active transaction is separate from a missing component");
        Check(ApplicationAvailabilityService.Classify(View("nginx", RuntimeOwnership.External)) == ComponentAvailabilityKind.External,
            "External ownership never becomes automatic takeover");
        // The launch gate is not the ownership report: an application that resolves the executable
        // itself can open on an external copy, while one that needs the ledger still cannot.
        Check(ComponentCatalog.AllowsExternalLaunch("git") && !ComponentCatalog.AllowsExternalLaunch("nginx")
            && !ComponentCatalog.AllowsExternalLaunch("docker") && !ComponentCatalog.AllowsExternalLaunch("frpc"),
            "Only components whose application resolves the executable are openable when installed externally");
        Check(new ApplicationAvailability(new AppId("relaxkonos.git"), "server-a", "git", ComponentAvailabilityKind.External).CanOpen,
            "An externally installed git opens the project editor instead of redirecting to Component Center");
        Check(!new ApplicationAvailability(new AppId("relaxkonos.webservers"), "server-a", "nginx", ComponentAvailabilityKind.External).CanOpen,
            "A component the ledger has to own still blocks its application when only an external copy exists");
        Check(!new ApplicationAvailability(new AppId("relaxkonos.git"), "server-a", "git", ComponentAvailabilityKind.Missing).CanOpen
            && !new ApplicationAvailability(new AppId("relaxkonos.git"), "server-a", "git", ComponentAvailabilityKind.Recovery).CanOpen,
            "The waiver covers external ownership only, never a package that is absent or mid-recovery");
        Check(ApplicationAvailabilityService.Classify(View("nginx", RuntimeOwnership.Absent)) == ComponentAvailabilityKind.Missing,
            "An absent component is the only state that reports a missing dependency");
        Check(ApplicationAvailabilityService.Classify(View("nginx", RuntimeOwnership.Unknown)) == ComponentAvailabilityKind.Unknown,
            "Unknown ownership is never treated as not installed");
        Check(ApplicationAvailabilityService.Classify(new("nginx", null,
                [new(ComponentSourceMode.SystemPackage, "linux", false, ComponentProblemCodes.PlatformUnsupported, "apt")],
                [ComponentProblemCodes.PlatformUnsupported])) == ComponentAvailabilityKind.NotRequired,
            "A component the host platform cannot provide is not a missing dependency");
        Check(ApplicationAvailabilityService.Classify(new("nginx", null,
                [new(ComponentSourceMode.SystemPackage, "linux", true, null, "apt")], [ComponentProblemCodes.StateUnavailable]))
            == ComponentAvailabilityKind.Unknown, "An unreadable state stays unknown rather than missing");
        Check(ApplicationAvailabilityService.Classify(new("other", null, [], [])) == ComponentAvailabilityKind.Unknown,
            "A component answer for a different identity is never accepted");

        var tunnels = new AppId("relaxkonos.tunnels");
        var installedAlternative = ApplicationAvailabilityService.Combine(tunnels, "server-a",
        [
            new("frpc", ComponentAvailabilityKind.Missing), new("frps", ComponentAvailabilityKind.Installed, "host-a", Guid.NewGuid())
        ]);
        Check(installedAlternative.CanOpen && installedAlternative.ComponentId == "frps",
            "One installed alternative opens the application and names the component it observed");
        Check(ApplicationAvailabilityService.Combine(tunnels, "server-a",
        [
            new("frpc", ComponentAvailabilityKind.Missing), new("frps", ComponentAvailabilityKind.Recovery)
        ]).State == ComponentAvailabilityKind.Recovery,
            "A missing alternative never hides a pending recovery on the other");
        Check(ApplicationAvailabilityService.Combine(tunnels, "server-a",
        [
            new("frpc", ComponentAvailabilityKind.NotRequired), new("frps", ComponentAvailabilityKind.NotRequired)
        ]).CanOpen, "Alternatives the host cannot provide never block the application");

        var app = new AppId("relaxkonos.proxy-server");
        var server = "server-a";
        var user = new UserDto(Guid.NewGuid(), "operator", HostPlatformKind.Linux, "operator", DateTimeOffset.UtcNow, null);
        SessionDto Login() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, SessionStatus.Active, user.Id, "password", DateTimeOffset.UtcNow);
        var login = Login();
        EventHandler<AuthSessionStateChangedEventArgs>? changed = null;
        var session = Probe<IAuthSession>((method, args) => method.Name switch
        {
            "get_State" => AuthSessionState.Authenticated, "get_CurrentSession" => login,
            "get_CurrentUser" => user, "get_ServiceId" => server,
            "add_StateChanged" => Add((EventHandler<AuthSessionStateChangedEventArgs>)args![0]!),
            "remove_StateChanged" => Remove((EventHandler<AuthSessionStateChangedEventArgs>)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        });
        object? Add(EventHandler<AuthSessionStateChangedEventArgs> handler) { changed += handler; return null; }
        object? Remove(EventHandler<AuthSessionStateChangedEventArgs> handler) { changed -= handler; return null; }
        var permission = AppPermissionStatus.Granted;
        var permissionPrompts = 0;
        var permissionApi = Probe<IAppPermissionRequestService>((method, _) => method.Name switch
        {
            "GetStatus" => permission, "RequestAsync" => AskPermission(),
            _ => throw new NotSupportedException(method.Name)
        });
        Task<AppPermissionStatus> AskPermission() { permissionPrompts++; return Task.FromResult(permission); }
        var observed = View("sing-box", RuntimeOwnership.Managed);
        var reads = 0;
        var fail = false;
        var planned = 0;
        TaskCompletionSource<ComponentStateView>? held = null;
        var lifecycle = Probe<IComponentLifecycleClient>((method, args) => method.Name switch
        {
            "StateAsync" => Read((string)args![0]!),
            "PlanAsync" => Plan(),
            _ => throw new InvalidOperationException("Availability must never mutate: " + method.Name)
        });
        Task<ComponentStateView> Read(string componentId)
        {
            reads++;
            if (componentId != "sing-box") return Task.FromException<ComponentStateView>(new InvalidOperationException("component.unknown"));
            return held?.Task ?? (fail ? Task.FromException<ComponentStateView>(new HttpRequestException("offline"))
                : Task.FromResult(observed));
        }
        object? Plan() { planned++; throw new InvalidOperationException("Availability must never freeze a plan"); }
        var activations = new List<Uri>();
        var activation = Probe<IAppActivationService>((method, args) =>
        {
            activations.Add(((AppActivationRequest)args![0]!).Uri);
            return new AppActivationResult(AppActivationStatus.Activated);
        });
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        using var services = new ServiceCollection().AddSingleton<IWindowManager>(windows)
            .AddSingleton<IComponentLifecycleClient>(lifecycle).AddSingleton(permissionApi).AddSingleton(activation)
            .BuildServiceProvider();
        var ui = new AvailabilityUiProbe();
        using var guard = new ApplicationAvailabilityService(session, services, ui);
        var context = new AppContext(app, windows, services);
        var observationChanges = 0;
        guard.Changed += (_, _) => observationChanges++;
        var manifest = new RelaxKonOS.Client.Apps.ProxyServer.ProxyServerApp().Manifest;
        var dockerManifest = new RelaxKonOS.Client.Apps.Docker.DockerManagerApp().Manifest;
        permission = AppPermissionStatus.Undecided;
        var passiveDenied = await guard.RefreshAsync(manifest);
        Check(passiveDenied?.State == ComponentAvailabilityKind.NoPermission && reads == 0 && permissionPrompts == 0
            && ui.Shown.Count == 0 && activations.Count == 0,
            "Passive status refresh never requests permission, observes without permission, redirects or opens UI");
        permission = AppPermissionStatus.Granted;
        var passive = await guard.RefreshAsync(manifest);
        Check(passive is { State: ComponentAvailabilityKind.Installed, CanOpen: true } && ui.Shown.Count == 0 && reads == 1,
            "Passive refresh reports installed readiness without opening the application");
        var unchangedBefore = observationChanges;
        await guard.RefreshAsync(manifest);
        Check(observationChanges == unchangedBefore,
            "Unchanged periodic observations do not rebuild application lists or disturb keyboard focus");
        Check(await guard.CheckAsync(context, null) && ui.Shown.Count == 0, "An installed component opens after fresh read checks");
        Check(guard.GetCached(app)?.ActualHostId == "host-a", "Cached observations retain the actual installation identity");
        Check(guard.GetCached(new AppId("relaxkonos.docker")) is null,
            "One component-backed application never inherits another application's readiness");
        observed = View("sing-box", RuntimeOwnership.Absent);
        ui.OpenCenter = true;
        Check(!await guard.CheckAsync(context, null) && ui.Shown.Last().State == ComponentAvailabilityKind.Missing
            && activations.Count == 1 && ComponentCenterNavigation.TryParse(activations[0], out var navigation)
            && navigation is { AppId: "relaxkonos.proxy-server", ComponentId: "sing-box" },
            "Missing dependencies route only to a controlled display link");
        fail = true;
        Check(!await guard.CheckAsync(context, null) && ui.Shown.Last().State == ComponentAvailabilityKind.Unknown
            && activations.Count == 1, "Read failure never becomes an install redirect");
        fail = false;
        permission = AppPermissionStatus.Denied;
        var beforeDenied = reads;
        Check(!await guard.CheckAsync(context, null) && reads == beforeDenied
            && ui.Shown.Last() is { State: ComponentAvailabilityKind.NoPermission, ServerIdentity: null },
            "Denied component read permission sends no observation and exposes no host detail");
        permission = AppPermissionStatus.Granted;
        held = new();
        var staleAfterLifecycle = guard.CheckAsync(context, null);
        guard.Clear();
        held.SetResult(observed);
        Check(!await staleAfterLifecycle && guard.GetCached(app) is null,
            "Lifecycle invalidation rejects a response started before the installation changed");
        held = new();
        var checking = guard.CheckAsync(context, null);
        var dialogsBeforeSwitch = ui.Shown.Count;
        server = "server-b"; login = Login(); changed?.Invoke(session, new(AuthSessionState.Authenticated));
        held.SetResult(observed);
        Check(!await checking && ui.Shown.Count == dialogsBeforeSwitch && guard.GetCached(app) is null,
            "Switching servers discards the old response and clears readiness without opening anything");
        server = "server-a"; login = Login();
        held = null;
        observed = new("sing-box", null,
            [new(ComponentSourceMode.ManagedArchive, "linux", true, null)], [ComponentProblemCodes.PlatformUnsupported]);
        Check(await guard.CheckAsync(context, null) && ui.Shown.Count == dialogsBeforeSwitch,
            "A host that cannot provide the component never blocks its application");
        Check(guard.RequiresCheck(manifest) && guard.RequiresCheck(dockerManifest),
            "Every catalog-backed application asks the same host question");
        Check(planned == 0, "Availability never freezes or executes a component change");
        VerifyRuntimeGate();
    }

    private static T Probe<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, AvailabilityApiProbe>();
        ((AvailabilityApiProbe)(object)value).Handler = handler;
        return value;
    }
    private static void VerifyRuntimeGate()
    {
        var guard = new LaunchGuardProbe();
        using var services = new ServiceCollection().AddSingleton<IApplicationLaunchGuard>(guard).BuildServiceProvider();
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        windows.Attach(new Canvas { Width = 1000, Height = 700 });
        var runtime = new ApplicationManager(windows, services);
        var app = new GuardedApp(); runtime.Register(app);
        var uri = new Uri("relaxkonos://guarded/open");
        Check(runtime.Launch(app.Manifest.Id) && app.Opens == 0, "Normal launch waits for availability before creating a window");
        var request = runtime.Activate(new(uri));
        Check(request.IsPendingAvailabilityCheck && !request.Succeeded && request.IsAccepted && guard.Checks == 1,
            "URI activation reports a pending check and coalesces repeated launches");
        guard.Resolve(true);
        Check(app.Opens == 1 && app.Routes == 1 && windows.Windows.Count == 1,
            "A successful check performs only the latest requested navigation once");
        var existing = windows.Windows.Single(); windows.Minimize(existing);
        runtime.Launch(app.Manifest.Id); guard.Resolve(false);
        Check(existing.State == RelaxKonOS.Core.Windows.WindowState.Minimized && app.Opens == 1,
            "An existing window cannot be restored by a rejected prerequisite check");
        windows.ForceClose(existing);
        runtime.OpenFile(app.Manifest.Id, "/document.txt"); guard.Resolve(false);
        runtime.OpenFileAsText(app.Manifest.Id, "/document.no-extension"); guard.Resolve(false);
        runtime.OpenTerminal("/work"); guard.Resolve(false);
        Check(app.Files == 0 && app.Terminals == 0, "File, text fallback and terminal paths cannot bypass the guard");
        runtime.Launch(app.Manifest.Id); runtime.Unregister(app.Manifest.Id); guard.Resolve(true);
        Check(app.Opens == 1, "Removing an application while its check runs prevents delayed activation");
    }
}

public class AvailabilityApiProbe : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
internal sealed class AvailabilityUiProbe : IApplicationAvailabilityUi
{
    public List<ApplicationAvailability> Shown { get; } = [];
    public bool OpenCenter { get; set; }
    public Task<bool> ShowAsync(ApplicationAvailability result) { Shown.Add(result); return Task.FromResult(OpenCenter); }
}
internal sealed class LaunchGuardProbe : IApplicationLaunchGuard
{
    private TaskCompletionSource<bool> _pending = null!;
    public int Checks { get; private set; }
    public bool RequiresCheck(ApplicationManifest manifest) => true;
    public Task<bool> CheckAsync(AppContext context, ManagedWindow? existingWindow) { Checks++; _pending = new(); return _pending.Task; }
    public void Resolve(bool allowed) => _pending.SetResult(allowed);
}
internal sealed class GuardedApp : IRemoteApplication, IAppActivationHandler, IFileOpenApplication, IOpenTerminalApplication
{
    public int Opens, Routes, Files, Terminals;
    public ApplicationManifest Manifest { get; } = new(new("test.guarded"), "Guarded", SupportedFileExtensions: [".txt"],
        SupportsTextFiles: true, InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public void Activate(AppContext context) { Opens++; context.ShowWindow("Guarded", new TextBlock { Text = "Ready" }); }
    public bool CanHandleActivation(Uri uri) => uri.Host == "guarded";
    public void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? window) => Routes++;
    public void OpenFile(AppContext context, string path) => Files++;
    public void OpenTerminal(AppContext context, string path) => Terminals++;
}
