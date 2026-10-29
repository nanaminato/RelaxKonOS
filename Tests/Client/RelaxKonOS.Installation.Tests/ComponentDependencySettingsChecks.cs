using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Workspace;

/// <summary>
/// The shared dependency area is the only component surface an application keeps. These checks pin
/// that it observes through the same state route as the launch gate, reports absence honestly, and
/// offers navigation without ever carrying an installation command.
/// </summary>
internal static class ComponentDependencySettingsChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: Dependency settings: " + message);
    }

    public static async Task RunAsync()
    {
        var navigations = new List<string>();
        var repository = new DependencyRepository();
        var session = Probe<IAuthSession>((method, args) => method.Name switch
        {
            "get_State" => AuthSessionState.Authenticated,
            "get_ServiceId" => repository.Server,
            "get_CurrentSession" => repository.Session,
            "get_CurrentUser" => repository.User,
            "add_StateChanged" or "remove_StateChanged" => null,
            _ => throw new NotSupportedException(method.Name)
        });
        var permission = AppPermissionStatus.Granted;
        var permissionPrompts = 0;
        var scope = Probe<IAppPermissionScope>((method, _) => method.Name switch
        {
            "GetStatus" => permission,
            "IsGranted" => permission == AppPermissionStatus.Granted,
            "RequestAsync" => AskPermission(),
            _ => throw new NotSupportedException(method.Name)
        });
        Task<AppPermissionStatus> AskPermission() { permissionPrompts++; return Task.FromResult(permission); }
        var lifecycle = Probe<IComponentLifecycleClient>((method, args) => method.Name switch
        {
            "StateAsync" => repository.Read((string)args![0]!),
            _ => throw new InvalidOperationException("The dependency area never freezes or executes a change: " + method.Name)
        });

        using (var settings = new ComponentDependencySettingsViewModel("relaxkonos.tunnels", session, lifecycle, scope,
            navigations.Add))
        {
            Check(settings.HasComponents && settings.Components.Select(item => item.ComponentId).Order()
                    .SequenceEqual(new[] { "frpc", "frps" }),
                "The dependency area lists exactly the catalog's alternatives for the application");
            Check(settings.Components.All(item => !item.Name.Resolve().StartsWith("component_center")),
                "A dependency row names the catalog product instead of a raw resource key");
            Check(typeof(ComponentDependencySettingsViewModel).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .All(method => !method.Name.Contains("Install", StringComparison.OrdinalIgnoreCase)
                        && !method.Name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
                        && !method.Name.Contains("Repair", StringComparison.OrdinalIgnoreCase)
                        && !method.Name.Contains("Rollback", StringComparison.OrdinalIgnoreCase)),
                "The dependency area exposes no installation, repair, rollback or removal command");

            repository.States["frpc"] = ComponentAvailabilityKind.Installed;
            repository.States["frps"] = ComponentAvailabilityKind.Missing;
            await settings.RefreshAsync();
            Check(settings.Components.Single(item => item.ComponentId == "frpc").State == ComponentAvailabilityKind.Installed
                && settings.Components.Single(item => item.ComponentId == "frps").State == ComponentAvailabilityKind.Missing
                && !settings.IsRefreshing && settings.Notice.IsEmpty,
                "Each alternative reports its own measured state without a shared conclusion");
            Check(settings.Components.Single(item => item.ComponentId == "frps").Status.Resolve()
                    == LocalizedText.Get("component_center.dependency.Missing"),
                "A missing component says it is not installed rather than borrowing the installed alternative's label");

            settings.Components[0].ManageCommand.Execute(null);
            settings.Components[1].ManageCommand.Execute(null);
            Check(navigations.SequenceEqual(new[] { "frpc", "frps" }),
                "Each row navigates to its own component, never to a generic installation page");
            Check(repository.Plans == 0, "Reading dependencies never freezes a plan");

            permission = AppPermissionStatus.Denied;
            await settings.RefreshAsync();
            Check(settings.Components.All(item => item.State == ComponentAvailabilityKind.NoPermission)
                && !settings.Notice.IsEmpty,
                "A denied read permission is reported on every row instead of showing a stale state");
            permission = AppPermissionStatus.Denied;
            var promptsBefore = permissionPrompts;
            await settings.RefreshAsync();
            Check(permissionPrompts == promptsBefore,
                "A decided permission is not re-prompted by a settings refresh");
            permission = AppPermissionStatus.Granted;

            repository.Fail = true;
            await settings.RefreshAsync();
            Check(settings.Components.All(item => item.State == ComponentAvailabilityKind.Unknown),
                "An unreadable component stays unknown instead of looking missing");
            repository.Fail = false;

            repository.PlatformUnsupported = true;
            await settings.RefreshAsync();
            Check(settings.Components.All(item => item.State == ComponentAvailabilityKind.NotRequired),
                "A component the host cannot provide is reported as not required");
            repository.PlatformUnsupported = false;
        }

        using (var signedOut = new ComponentDependencySettingsViewModel("relaxkonos.tunnels",
            Probe<IAuthSession>((method, _) => method.Name == "get_State" ? AuthSessionState.Unauthenticated : null),
            null, Probe<IAppPermissionScope>((method, _) => throw new NotSupportedException(method.Name)), _ => { }))
        {
            await signedOut.RefreshAsync();
            Check(!signedOut.Notice.IsEmpty && signedOut.Components.All(item => item.State == ComponentAvailabilityKind.Unknown),
                "Without a signed-in host the area explains itself and claims no state");
        }
    }

    private static T Probe<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var value = DispatchProxy.Create<T, AvailabilityApiProbe>();
        ((AvailabilityApiProbe)(object)value).Handler = handler;
        return value;
    }

    private sealed class DependencyRepository
    {
        public string Server { get; set; } = "service-a";
        public SessionDto Session { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, SessionStatus.Active, Guid.NewGuid(), "password", DateTimeOffset.UtcNow);
        public UserDto User { get; } = new(Guid.NewGuid(), "operator", HostPlatformKind.Linux, "operator", DateTimeOffset.UtcNow, null);
        public Dictionary<string, ComponentAvailabilityKind> States { get; } = new(StringComparer.Ordinal)
        {
            ["frpc"] = ComponentAvailabilityKind.Installed, ["frps"] = ComponentAvailabilityKind.Installed
        };
        public bool Fail { get; set; }
        public bool PlatformUnsupported { get; set; }
        public int Plans { get; private set; }

        public Task<ComponentStateView> Read(string componentId)
        {
            if (Fail) return Task.FromException<ComponentStateView>(new HttpRequestException("offline"));
            var kind = States.GetValueOrDefault(componentId, ComponentAvailabilityKind.Unknown);
            if (PlatformUnsupported)
                return Task.FromResult(new ComponentStateView(componentId, null,
                    [new(ComponentSourceMode.SystemPackage, "linux", false, ComponentProblemCodes.PlatformUnsupported, "apt")],
                    [ComponentProblemCodes.PlatformUnsupported]));
            var ownership = kind switch
            {
                ComponentAvailabilityKind.Installed => RuntimeOwnership.Managed,
                ComponentAvailabilityKind.Missing => RuntimeOwnership.Absent,
                ComponentAvailabilityKind.External => RuntimeOwnership.External,
                _ => RuntimeOwnership.Unknown
            };
            return Task.FromResult(new ComponentStateView(componentId, new(componentId, "host-a", 3, ownership,
                ComponentSourceMode.SystemPackage, new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.SystemPackage, "systemd"),
                kind == ComponentAvailabilityKind.Installed ? "1.2.3" : null, false),
                [new(ComponentSourceMode.SystemPackage, "linux", true, null, "apt")], []));
        }
    }
}
