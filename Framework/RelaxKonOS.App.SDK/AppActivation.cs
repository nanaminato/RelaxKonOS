using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.AppSDK;

/// <summary>One request to activate a Shell-owned <c>relaxkonos://</c> route or a manifest-declared external URI.</summary>
public sealed record AppActivationRequest(
    Uri Uri,
    AppId? SourceAppId = null,
    bool UserInitiated = true,
    string? CorrelationId = null);

/// <summary>Stable outcome for a local activation request.</summary>
public enum AppActivationStatus
{
    Activated,
    InvalidUri,
    RouteNotFound,
    /// <summary>No installed application accepts the requested third-party URI scheme.</summary>
    NoHandler,
    /// <summary>The Shell has asked the user to select one of several eligible handlers.</summary>
    HandlerSelectionRequired,
    Unavailable,
}

public sealed record AppActivationResult(AppActivationStatus Status, AppId? TargetAppId = null)
{
    public bool Succeeded => Status == AppActivationStatus.Activated;
    /// <summary>Whether the Shell accepted the request and may still complete it after user input.</summary>
    public bool IsPendingUserChoice => Status == AppActivationStatus.HandlerSelectionRequired;
}

/// <summary>
/// Host-owned activation surface. Applications never instantiate or discover another
/// application directly; the Shell validates and resolves the URI.
/// </summary>
public interface IAppActivationService
{
    AppActivationResult Activate(AppActivationRequest request);
}

/// <summary>Host-owned lookup for a user's default application for a URI scheme.</summary>
/// <remarks>
/// Implementations are optional. Without one, the runtime activates a scheme only when exactly
/// one registered application declares that it can handle the URI.
/// </remarks>
public interface IUriSchemeDefaultResolver
{
    AppId? ResolveDefaultApplication(string scheme);
}

/// <summary>A user choice made for an ambiguous external URI scheme.</summary>
public sealed record UriSchemeHandlerChoice(AppId ApplicationId, bool SetAsDefault);

/// <summary>
/// Optional Shell-owned UI for third-party URI routing. The runtime calls it only for
/// user-initiated requests that have no eligible handler or more than one eligible handler.
/// Implementations may persist a selected default application.
/// </summary>
public interface IUriSchemeRoutingUi
{
    Task<UriSchemeHandlerChoice?> ChooseHandlerAsync(Uri uri, IReadOnlyList<ApplicationInfo> candidates);

    Task SaveDefaultHandlerAsync(string scheme, AppId applicationId);

    Task NotifyNoHandlerAsync(Uri uri);
}

/// <summary>Optional host-owned sink for privacy-safe application activation diagnostics.</summary>
public interface IAppActivationDiagnostics
{
    void Record(string message);
}

/// <summary>
/// Optional built-in application extension for registered <c>relaxkonos://</c> routes. The handler is also
/// invoked for an already-open single-window application, before that window is focused.
/// </summary>
public interface IAppActivationHandler
{
    bool CanHandleActivation(Uri uri);

    void HandleActivation(AppContext context, AppActivationRequest request, ManagedWindow? existingWindow);
}

/// <summary>Source-bound activation surface exposed to an application context.</summary>
public interface IAppActivation
{
    AppActivationResult Activate(Uri uri, bool userInitiated = true, string? correlationId = null);
}

/// <summary>Stable Shell-owned activation URIs exposed to built-in and package applications.</summary>
public static class RelaxKonOSActivationUris
{
    public static Uri SettingsPersonalization { get; } = new("relaxkonos://settings/personalization");
    public static Uri SettingsApplications { get; } = new("relaxkonos://settings/apps");

    public static Uri SettingsAppPermissions(AppId appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId.Value);
        return new Uri($"relaxkonos://settings/apps/{Uri.EscapeDataString(appId.Value)}/permissions");
    }

    /// <summary>
    /// Internal Explorer file-open route. Third-party applications should request a file
    /// capability instead of constructing host paths.
    /// </summary>
    public static Uri OpenFile(AppId appId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri($"relaxkonos://file/open?appId={Uri.EscapeDataString(appId.Value)}&path={Uri.EscapeDataString(path)}");
    }

    /// <summary>Internal route to navigate RemoteExplorer to a server-side directory.</summary>
    public static Uri ExplorerPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Uri($"relaxkonos://explorer/open?path={Uri.EscapeDataString(path)}");
    }
}
