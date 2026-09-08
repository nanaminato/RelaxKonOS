using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using Avalonia.Controls;
using System.Text.Json;

namespace RelaxKonOS.AppSDK;

/// <summary>
/// Entry point implemented by a package application. Unlike <see cref="IRemoteApplication"/>,
/// it deliberately receives no service provider or host implementation objects.
/// </summary>
public interface IExternalRemoteApplication
{
    ApplicationManifest Manifest { get; }

    Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default);
}

/// <summary>Optional extension for package apps that can open a remote file from RemoteExplorer.</summary>
public interface IExternalFileOpenApplication
{
    Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default);
}

/// <summary>Optional extension for package apps that declare URI schemes in their manifest.</summary>
/// <remarks>The host validates the URI and selects the target application before this method runs.</remarks>
public interface IExternalAppActivationHandler
{
    bool CanHandleActivation(Uri uri);

    Task HandleActivationAsync(IExternalAppContext context, Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>Capability-only context supplied to a third-party package application.</summary>
public interface IExternalAppContext
{
    AppId AppId { get; }
    IAppPermissionScope Permissions { get; }
    IDesktopAppearance DesktopAppearance { get; }
    IServerMonitor ServerMonitor { get; }
    IServerFiles ServerFiles { get; }
    IExternalFileApiAccess FileApi { get; }
    IExternalMediaService Media { get; }
    /// <summary>Versioned JSON configuration private to this application and persisted by the connected server.</summary>
    IExternalAppSettings SettingsStore { get; }
    /// <summary>Read-only system language and language-change notifications.</summary>
    ISystemLanguage SystemLanguage { get; }
    /// <summary>Host-validated navigation to a registered <c>remoteos://</c> route or manifest-declared external scheme.</summary>
    IAppActivation Activations { get; }
    ISettingsNavigation Settings { get; }
    IExternalAppWindowService Windows { get; }
}

/// <summary>Scope of an application's persisted configuration document.</summary>
public enum ExternalAppSettingsScope
{
    User,
    Workspace,
    Device,
}

/// <summary>Host-mediated storage for configuration owned by the current external application.</summary>
public interface IExternalAppSettings
{
    Task<ExternalAppSettingsDocument?> GetAsync(
        ExternalAppSettingsScope scope = ExternalAppSettingsScope.Workspace,
        string key = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a configuration document. Supply the revision returned by <see cref="GetAsync"/>
    /// to reject a concurrent overwrite; use <c>0</c> to create only when absent.
    /// </summary>
    Task<ExternalAppSettingsDocument> SetAsync(
        JsonElement value,
        int schemaVersion = 1,
        ExternalAppSettingsScope scope = ExternalAppSettingsScope.Workspace,
        string key = "default",
        long? expectedRevision = null,
        CancellationToken cancellationToken = default);
}

/// <summary>A versioned JSON configuration document returned through <see cref="IExternalAppSettings"/>.</summary>
public sealed record ExternalAppSettingsDocument(
    ExternalAppSettingsScope Scope,
    string Key,
    JsonElement Value,
    int SchemaVersion,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>Host-mediated desktop appearance operations available to package applications.</summary>
public interface IDesktopAppearance
{
    Task<AppCapabilityResult> SetWallpaperAsync(string wallpaperKey, CancellationToken cancellationToken = default);
}

/// <summary>Allows an application to bring the user to a relevant, host-owned Settings page.</summary>
public interface ISettingsNavigation
{
    /// <summary>Opens Settings and selects its Applications page.</summary>
    Task OpenApplicationsAsync();
}

/// <summary>Window creation surface for package applications. Every window is owned by the package app id.</summary>
public interface IExternalAppWindowService
{
    IExternalAppWindowHandle ShowWindow(
        string title,
        Control content,
        Rect? bounds = null,
        string? iconGlyph = null,
        bool canResize = true,
        bool canMinimize = true,
        bool canMaximize = true);
}

/// <summary>Window handle with a token cancelled as soon as its managed window closes.</summary>
public interface IExternalAppWindowHandle
{
    ManagedWindow Window { get; }
    CancellationToken Closed { get; }
    bool IsFullScreen { get; }
    void EnterFullScreen();
    void ExitFullScreen();
}

/// <summary>Result returned by a host-mediated capability call.</summary>
public enum AppCapabilityResult
{
    Succeeded,
    PermissionDenied,
    InvalidArgument,
    Unavailable,
}
