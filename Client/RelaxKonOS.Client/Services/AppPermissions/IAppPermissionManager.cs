using RelaxKonOS.Core.Applications;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services.AppPermissions;

/// <summary>Host-owned persisted decision registry. Only Shell permission UI may mutate decisions.</summary>
public interface IAppPermissionManager
{
    AppPermissionStatus GetStatus(AppId appId, string permissionId);
    bool IsGranted(AppId appId, string permissionId);
    void SetStatus(AppId appId, string permissionId, AppPermissionStatus status);
    void SetGranted(AppId appId, string permissionId, bool granted);
    void Clear(AppId appId);
}
