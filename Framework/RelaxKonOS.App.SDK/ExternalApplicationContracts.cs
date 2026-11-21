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
    IServerProcesses ServerProcesses { get; }
    IServerRegistry Registry { get; }
    IServerCertificates Certificates { get; }
    IRegistryAppearancePreferences? RegistryAppearance { get; }
    /// <summary>Available only to the official Browser application.</summary>
    IBrowserData? BrowserData { get; }
    /// <summary>Server PTY discovery for the official Terminal application.</summary>
    ITerminalSessionDiscovery? TerminalSessions { get; }
    ITerminalDevicePreferences? TerminalDevicePreferences { get; }
    ITerminalWorkspacePreferences? TerminalWorkspacePreferences { get; }
    IExternalTerminal? Terminal { get; }
    IServerAlerts ServerAlerts { get; }
    IServerDockerResources DockerResources { get; }
    IServerDockerImageMirrors DockerImageMirrors { get; }
    /// <summary>Reports whether the application context has an authenticated server connection.</summary>
    bool HasAuthenticatedServer { get; }
    IExternalServerConnection ServerConnection { get; }
    /// <summary>Read-only, host-rendered settings for the application's declared component dependencies.</summary>
    IExternalAppComponentDependencies? ComponentDependencies { get; }
    IServerFiles ServerFiles { get; }
    /// <summary>Workspace-backed encoding preferences for a host-configured text editor.</summary>
    IExternalTextEditorPreferences? TextEditorPreferences { get; }
    IExternalFileApiAccess FileApi { get; }
    IExternalMediaService Media { get; }
    /// <summary>Versioned JSON configuration private to this application and persisted by the connected server.</summary>
    IExternalAppSettings SettingsStore { get; }
    /// <summary>Read-only system language and language-change notifications.</summary>
    ISystemLanguage SystemLanguage { get; }
    /// <summary>Host-validated navigation to a registered <c>relaxkonos://</c> route or manifest-declared external scheme.</summary>
    IAppActivation Activations { get; }
    ISettingsNavigation Settings { get; }
    IExternalAppWindowService Windows { get; }
}

public interface ITerminalSessionDiscovery
{
    bool IsCurrent { get; }
    Task<IReadOnlyList<RelaxKonOS.Protocol.Hubs.TerminalSessionInfo>> ListAsync(CancellationToken cancellationToken);
}

public interface ITerminalDevicePreferences
{
    string VtEngine { get; }
    void SetVtEngine(string value);
}

public interface ITerminalWorkspacePreferences
{
    RelaxKonOS.Protocol.Workspace.TerminalSettingsDto Appearance { get; }
    RelaxKonOS.Protocol.Registry.TerminalPaddingSettings Padding { get; }
    IDisposable Observe(Action changed);
    Task LoadAsync();
    Task SaveAsync(RelaxKonOS.Protocol.Workspace.TerminalSettingsDto appearance);
}

/// <summary>Observes the authenticated server connection captured for an application launch.</summary>
public interface IExternalServerConnection
{
    bool IsCurrent { get; }
    IDisposable ObserveInvalidation(Action invalidated);
}

public interface IExternalAppComponentDependencies
{
    Control? CreateSettingsView(IExternalAppWindowHandle owner);
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
    /// <summary>Selects a shipped photograph or gradient using its complete <c>builtin:</c>
    /// identifier (for example <c>builtin:alpine-lake</c>). No image file is uploaded.</summary>
    Task<AppCapabilityResult> SetWallpaperAsync(string wallpaperKey, CancellationToken cancellationToken = default);
}

/// <summary>Allows an application to bring the user to a relevant, host-owned Settings page.</summary>
public interface ISettingsNavigation
{
    /// <summary>Opens Settings and selects its Applications page.</summary>
    Task OpenApplicationsAsync();

    /// <summary>Opens the remote host environment editor. Navigation does not grant read or write access.</summary>
    Task OpenEnvironmentAsync();
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
    Task<TResult?> ShowDialogAsync<TResult>(string title, Func<ModalDialog<TResult>, Control> content, Size preferredSize);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel);

    /// <summary>Opens a confirmation owned by an active dialog belonging to the same application.</summary>
    Task<bool> ConfirmNestedAsync<T>(ModalDialog<T> owner, string title, string message, string confirmLabel);
}

public interface IExternalTextEditorPreferences
{
    string DefaultEncoding { get; }
    Task SetDefaultEncodingAsync(string encoding);
    Task<EncodingDialogAction?> PickEncodingActionAsync(IExternalAppWindowHandle owner);
    Task<string?> PickEncodingAsync(IExternalAppWindowHandle owner, string currentEncoding);
}

public enum EncodingDialogAction { Reopen, Save }

/// <summary>Result returned by a host-mediated capability call.</summary>
public enum AppCapabilityResult
{
    Succeeded,
    PermissionDenied,
    InvalidArgument,
    Unavailable,
    Conflict,
}
