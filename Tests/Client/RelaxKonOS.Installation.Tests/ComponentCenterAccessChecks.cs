using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Workspace;

internal static class ComponentCenterAccessChecks
{
    public static async Task RunAsync()
    {
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: Component access " + label); }
        var session = DispatchProxy.Create<IAuthSession, PreviewSession>(); var login = (PreviewSession)(object)session;
        var permissions = new Grants();
        using (var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>()))
        {
            var supported = true;
            var access = new ComponentCenterAccess(session, permissions, runtime, () => supported, _ => throw new Exception("Changed valid connection"));
            runtime.RefreshAccess = access.Synchronize;
            access.Synchronize();
            Check(runtime.CanRead && runtime.CanManage, "current controller and grants enable permitted actions without prompting");
            await runtime.LoadAsync();
            permissions.Manage = AppPermissionStatus.Denied; access.Synchronize();
            Check(runtime.CanRead && !runtime.CanManage && runtime.Installation is not null, "management revocation preserves read-only observation");
            permissions.Manage = AppPermissionStatus.Granted; login.Role = DeviceRole.Observer; access.Synchronize();
            Check(runtime.CanRead && !runtime.CanManage, "observer role cannot regain writes through a local grant");
            login.Role = DeviceRole.Controller; permissions.Read = AppPermissionStatus.Undecided; access.Synchronize();
            Check(!runtime.CanRead && !runtime.CanManage && runtime.Installation is null, "expired or undecided read grant clears host facts and management");
            permissions.Read = AppPermissionStatus.Granted; access.Synchronize();
            Check(runtime.CanRead && runtime.CanManage && runtime.Installation is null, "restoring access does not restore stale observations or load the host");
            supported = false; access.Synchronize();
            Check(!runtime.CanRead && !runtime.CanManage, "unsupported host stays disabled even when local permissions are granted");
            runtime.RefreshAccess = null;
        }
        foreach (var change in new Action<PreviewSession>[] { s => s.Server = "different-server", s => s.BaseUrl = "http://other-port/",
            s => s.User = s.User with { Id = Guid.NewGuid() }, s => s.Login = s.Login with { Id = Guid.NewGuid() } })
        {
            session = DispatchProxy.Create<IAuthSession, PreviewSession>(); login = (PreviewSession)(object)session;
            using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>());
            var disconnected = false;
            var access = new ComponentCenterAccess(session, permissions, runtime, () => true, value => disconnected = !value);
            access.Synchronize(); change(login); access.Synchronize();
            Check(disconnected && runtime.IsInvalidated && !runtime.CanRead && !runtime.CanManage,
                "server, transport, actor or login replacement invalidates the original window");
            access.Synchronize();
            Check(runtime.IsInvalidated && !runtime.CanRead, "invalidated window cannot regain access from a later grant");
        }
    }

    private sealed class Grants : IAppPermissionScope
    {
        public AppPermissionStatus Read = AppPermissionStatus.Granted, Manage = AppPermissionStatus.Granted;
        public AppPermissionStatus GetStatus(string permission) => permission == AppPermissions.ServerProxyRead ? Read : Manage;
        public bool IsGranted(string permission) => GetStatus(permission) == AppPermissionStatus.Granted;
        public Task<AppPermissionStatus> RequestAsync(string permission, CancellationToken cancellationToken = default) => throw new Exception("Passive access refresh prompted");
        public Task OpenSettingsAsync() => throw new Exception("Passive access refresh opened settings");
    }
}
