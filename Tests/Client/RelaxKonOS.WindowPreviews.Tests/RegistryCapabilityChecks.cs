using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;

internal static class RegistryCapabilityChecks
{
    public static async Task RunAsync()
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        session.Device = new DeviceDto(Guid.NewGuid(), "fixture", "windows", "test", null);
        var client = DispatchProxy.Create<IServerRegistry, RegistryCapabilityTransport>();
        var transport = (RegistryCapabilityTransport)(object)client;
        var permissions = new Permission();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "registry-capability-memory.json"));
        var connection = new ExternalServerConnection(memory.Capture(auth));
        IServerRegistry registry = new ServerRegistryCapability(new AppId("relaxkonos.registry"), permissions,
            connection, client);
        using var preferences = new ClientPresentationPreferences(client, auth, new PresentationSettingsChanges());
        IRegistryAppearancePreferences appearance = new ExternalRegistryAppearance(preferences, connection);
        var originalDevice = session.Device;
        session.Device = new DeviceDto(Guid.NewGuid(), "replacement", "windows", "test", null);
        await RejectAsync<InvalidOperationException>(() => appearance.SaveSidebarWidthAsync(320));
        Check(appearance.Registry == RegistryAppearanceSettings.Default && transport.Requests == 0,
            "The old Registry appearance context must not read or write settings for another device.");
        session.Device = originalDevice;
        var target = Guid.NewGuid();
        var scoped = registry.ForUser(target);
        var request = new CreateRegistryKeyRequest(RegistryScope.User, "fixture");

        await RejectAsync<UnauthorizedAccessException>(() => registry.GetContextAsync());
        await RejectAsync<UnauthorizedAccessException>(() => scoped.CreateKeyAsync(request));
        permissions.Read = true;
        await RejectAsync<UnauthorizedAccessException>(() => scoped.CreateKeyAsync(request));
        Check(transport.Requests == 0 && transport.Target == target, "Owner scoping must preserve registry permissions.");
        permissions.Write = true;
        Check((await scoped.CreateKeyAsync(request)).Path == request.Path && transport.Requests == 1,
            "The authorized write must reach the bound registry transport.");

        var pending = registry.GetContextAsync();
        session.ServiceId = "https://replacement.invalid";
        transport.Context.SetResult(new RegistryContextDto("fixture", target, "fixture", null, false, null, Guid.NewGuid()));
        await RejectAsync<InvalidOperationException>(() => pending);
        await RejectAsync<InvalidOperationException>(() => scoped.CreateKeyAsync(request));
        Check(transport.Requests == 2, "The previous registry context must not contact the new server.");
        Console.WriteLine("Registry read/write grants, owner scoping and connection isolation passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Permission : IAppPermissionManager
    {
        public bool Read { get; set; }
        public bool Write { get; set; }
        public bool IsGranted(AppId id, string permission) => id.Value == "relaxkonos.registry"
            && (permission == AppPermissions.ServerRegistryRead && Read || permission == AppPermissions.ServerRegistryWrite && Write);
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
    }
}

public class RegistryCapabilityTransport : DispatchProxy
{
    internal int Requests { get; private set; }
    internal Guid Target { get; private set; }
    internal TaskCompletionSource<RegistryContextDto> Context { get; } = new();

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IServerRegistry.ForUser))
        {
            Target = (Guid)args![0]!;
            return this;
        }
        Requests++;
        if (method?.Name == nameof(IServerRegistry.GetContextAsync)) return Context.Task;
        if (method?.Name == nameof(IServerRegistry.CreateKeyAsync) && args?[0] is CreateRegistryKeyRequest request)
            return Task.FromResult(new RegistryKeyDto(request.Scope, request.Path));
        throw new NotSupportedException(method?.Name);
    }
}
