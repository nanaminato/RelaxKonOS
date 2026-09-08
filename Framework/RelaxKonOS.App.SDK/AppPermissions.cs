using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.AppSDK;

/// <summary>The user's persisted decision for one permission declared by an application.</summary>
public enum AppPermissionStatus
{
    /// <summary>No decision has been made yet. The application must treat this as not granted.</summary>
    Undecided,
    Granted,
    Denied,
}

/// <summary>
/// Host-owned permission workflow. Applications receive this only through their own scoped
/// context, so they can never inspect or request another application's permissions.
/// </summary>
public interface IAppPermissionRequestService
{
    AppPermissionStatus GetStatus(AppId appId, string permissionId);
    Task<AppPermissionStatus> RequestAsync(AppId appId, string permissionId, CancellationToken cancellationToken = default);
    Task RequestUndecidedAsync(AppId appId, CancellationToken cancellationToken = default);
    Task OpenSettingsAsync(AppId appId);
}

/// <summary>Read-only status and user-mediated requests for the current application's grants.</summary>
public interface IAppPermissionScope
{
    /// <summary>Returns whether this declared permission is granted, denied, or still undecided.</summary>
    AppPermissionStatus GetStatus(string permissionId);

    /// <summary>Convenience equivalent to <c>GetStatus(permissionId) == Granted</c>.</summary>
    bool IsGranted(string permissionId);

    /// <summary>
    /// Shows this application's permission prompt again. Cancelling leaves the current decision
    /// unchanged; a denial never prevents the application from continuing to run.
    /// </summary>
    Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default);

    /// <summary>Opens Settings directly on this application's permissions page.</summary>
    Task OpenSettingsAsync();
}
