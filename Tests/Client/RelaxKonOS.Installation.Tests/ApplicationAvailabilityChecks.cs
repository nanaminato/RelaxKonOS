using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;
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
        // A catalog entry without an availability adapter must not gate its application, otherwise
        // listing Docker or the Web Server here would make those applications unopenable.
        Check(ComponentCatalog.LaunchGate("relaxkonos.proxy-server")?.ComponentId == "sing-box"
            && ComponentCatalog.LaunchGate("relaxkonos.docker") is null
            && ComponentCatalog.LaunchGate("relaxkonos.webservers") is null
            && ComponentCatalog.LaunchGate("relaxkonos.proxy") is null,
            "Only the component with an availability adapter may gate application launch");
        var nginx = ComponentCatalog.Find("nginx")!;
        Check(nginx.Sources.Any(source => source.Platforms.Contains("linux") && source.Kind == ComponentSourceKind.SystemPackage)
            && nginx.Sources.Any(source => source.Platforms.Contains("windows") && source.Kind == ComponentSourceKind.ManagedArchive)
            && nginx.Sources.Single(source => source.Kind == ComponentSourceKind.SystemPackage).AdapterAvailable
            && nginx.Sources.Single(source => source.Kind == ComponentSourceKind.ManagedArchive).AdapterAvailable
            && !nginx.Sources.Single(source => source.Kind == ComponentSourceKind.OfficialSource).AdapterAvailable,
            "A component with different platform implementations declares one source set per platform");
        var app = new AppId("relaxkonos.proxy-server");
        var target = new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd");
        var installed = new RuntimeInstallationSnapshot(new(Guid.NewGuid(), "host-a", "sing-box", 1,
            RuntimeOwnership.Managed, target, "release-a", false), "1.14.3", null, null, null, null, null);
        var status = new ProxyServerStatus(true, true, false, false, "1.14.3", null, false, null);
        ApplicationAvailability Classify(RuntimeInstallationSnapshot observation, ProxyServerStatus fact) =>
            ApplicationAvailabilityService.Classify(app, "server-a", "sing-box", observation, fact);
        Check(Classify(installed, status) is { State: ComponentAvailabilityKind.Stopped, CanOpen: true },
            "Stopping a service never makes its application missing");
        Check(Classify(installed, status with { Running = true }).State == ComponentAvailabilityKind.Running,
            "Running is a separate measured state");
        Check(Classify(installed, status with { ProblemCode = "health.failed" }) is { State: ComponentAvailabilityKind.Faulted, CanOpen: true },
            "Installed faults can still open the business console");
        Check(Classify(installed with { Installation = installed.Installation with { Ownership = RuntimeOwnership.External } }, status)
            is { State: ComponentAvailabilityKind.External, CanOpen: false }, "External ownership never becomes automatic takeover");
        Check(Classify(installed with { Installation = installed.Installation with { Ownership = RuntimeOwnership.Absent } }, status).State
            == ComponentAvailabilityKind.Unknown, "Contradictory installed facts are unknown rather than an install prompt");
        Check(Classify(installed with { Installation = installed.Installation with { RecoveryRequired = true } }, status).State
            == ComponentAvailabilityKind.Recovery, "Recovery blocks normal activation");
        Check(Classify(installed with { PendingOperationId = Guid.NewGuid() }, status).State == ComponentAvailabilityKind.Busy,
            "An active transaction is separate from a missing component");
        Check(Classify(installed, status with { Supported = false }).State == ComponentAvailabilityKind.Unsupported,
            "An unsupported target never appears missing");

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
        var observed = installed;
        var fact = status;
        var reads = 0;
        var fail = false;
        TaskCompletionSource<RuntimeInstallationSnapshot>? held = null;
        var runtime = Probe<IRuntimeCatalogClient>((method, _) => method.Name switch
        {
            "InstallationAsync" => Read(),
            _ => throw new InvalidOperationException("Availability must never mutate: " + method.Name)
        });
        Task<RuntimeInstallationSnapshot> Read()
        {
            reads++;
            return held?.Task ?? (fail ? Task.FromException<RuntimeInstallationSnapshot>(new HttpRequestException("offline")) : Task.FromResult(observed));
        }
        var proxy = Probe<IProxyServerClient>((method, _) => method.Name == "StatusAsync"
            ? Task.FromResult(fact) : throw new InvalidOperationException(method.Name));
        var activations = new List<Uri>();
        var activation = Probe<IAppActivationService>((method, args) =>
        {
            activations.Add(((AppActivationRequest)args![0]!).Uri);
            return new AppActivationResult(AppActivationStatus.Activated);
        });
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        using var services = new ServiceCollection().AddSingleton<IWindowManager>(windows).AddSingleton(runtime).AddSingleton(proxy)
            .AddSingleton(permissionApi).AddSingleton(activation).BuildServiceProvider();
        var ui = new AvailabilityUiProbe();
        using var guard = new ApplicationAvailabilityService(session, services, ui);
        var context = new AppContext(app, windows, services);
        var observationChanges = 0;
        guard.Changed += (_, _) => observationChanges++;
        var manifest = new RelaxKonOS.Client.Apps.ProxyServer.ProxyServerApp().Manifest;
        permission = AppPermissionStatus.Undecided;
        var passiveDenied = await guard.RefreshAsync(manifest);
        Check(passiveDenied?.State == ComponentAvailabilityKind.NoPermission && reads == 0 && permissionPrompts == 0
            && ui.Shown.Count == 0 && activations.Count == 0,
            "Passive status refresh never requests permission, observes without permission, redirects or opens UI");
        permission = AppPermissionStatus.Granted;
        var passive = await guard.RefreshAsync(manifest);
        Check(passive is { State: ComponentAvailabilityKind.Stopped, CanOpen: true } && ui.Shown.Count == 0,
            "Passive refresh reports stopped readiness without opening the application");
        var unchangedBefore = observationChanges;
        await guard.RefreshAsync(manifest);
        Check(observationChanges == unchangedBefore,
            "Unchanged periodic observations do not rebuild application lists or disturb keyboard focus");
        Check(await guard.CheckAsync(context, null) && ui.Shown.Count == 0, "A stopped installation opens after fresh read checks");
        Check(guard.GetCached(app)?.ActualHostId == "host-a", "Cached observations retain the actual installation identity");
        observed = installed with { Installation = installed.Installation with { Ownership = RuntimeOwnership.Absent } };
        fact = status with { Installed = false };
        ui.OpenCenter = true;
        Check(!await guard.CheckAsync(context, null) && ui.Shown.Last().State == ComponentAvailabilityKind.Missing
            && activations.Count == 1 && ComponentCenterNavigation.TryParse(activations[0], out _),
            "Missing dependencies route only to a controlled display link");
        fail = true;
        Check(!await guard.CheckAsync(context, null) && ui.Shown.Last().State == ComponentAvailabilityKind.Unknown
            && activations.Count == 1, "Read failure never becomes an install redirect");
        fail = false;
        permission = AppPermissionStatus.Denied;
        var beforeDenied = reads;
        Check(!await guard.CheckAsync(context, null) && reads == beforeDenied
            && ui.Shown.Last() is { State: ComponentAvailabilityKind.NoPermission, ServerIdentity: null },
            "Denied read permission sends no observation and exposes no host detail");
        permission = AppPermissionStatus.Granted;
        held = new();
        var staleAfterLifecycle = guard.CheckAsync(context, null);
        guard.Clear();
        held.SetResult(installed);
        Check(!await staleAfterLifecycle && guard.GetCached(app) is null,
            "Lifecycle invalidation rejects a response started before the installation changed");
        held = new();
        var checking = guard.CheckAsync(context, null);
        var dialogsBeforeSwitch = ui.Shown.Count;
        server = "server-b"; login = Login(); changed?.Invoke(session, new(AuthSessionState.Authenticated));
        held.SetResult(installed);
        Check(!await checking && ui.Shown.Count == dialogsBeforeSwitch && guard.GetCached(app) is null,
            "Switching servers discards the old response and clears readiness without opening anything");
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
