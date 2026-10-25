using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>Re-read local grants without prompting. A window and its typed clients belong to one transport/login.</summary>
public sealed class ComponentCenterAccess(IAuthSession session, IAppPermissionScope permissions,
    ProxyServerRuntimeViewModel runtime, Func<bool> supported, Action<bool> connection,
    IReadOnlyList<ProxyServerRuntimeViewModel>? additionalRuntimes = null)
{
    private readonly object? _login = session.CurrentSession;
    private readonly Guid? _actor = session.CurrentUser?.Id;
    private readonly string? _server = session.ServiceId;
    private readonly string? _address = session.EffectiveBaseUrl;
    /// <summary>Every component runtime shares the same host grants; none may keep a stale enablement.</summary>
    private readonly IReadOnlyList<ProxyServerRuntimeViewModel> _runtimes = additionalRuntimes is null
        ? [runtime] : [runtime, .. additionalRuntimes.Where(item => !ReferenceEquals(item, runtime))];

    public void Synchronize()
    {
        if (runtime.IsInvalidated) return;
        if (_login is null || _actor is null || !ReferenceEquals(_login, session.CurrentSession)
            || session.State != AuthSessionState.Authenticated || session.CurrentUser?.Id != _actor
            || session.ServiceId != _server || session.EffectiveBaseUrl != _address)
        {
            connection(false);
            foreach (var item in _runtimes) item.InvalidateSession();
            return;
        }
        var canRead = supported() && permissions.GetStatus(AppPermissions.ServerProxyRead) == AppPermissionStatus.Granted;
        var canManage = canRead && session.AssignedRole == DeviceRole.Controller
            && permissions.GetStatus(AppPermissions.ServerProxyManage) == AppPermissionStatus.Granted;
        foreach (var item in _runtimes) { item.CanRead = canRead; item.CanManage = canManage; }
    }
}
