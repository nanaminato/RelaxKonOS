using Avalonia.Controls;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.AppSDK;

/// <summary>
/// The runtime context handed to an application when it is launched. This is the surface
/// through which a RelaxKonOS application creates windows and reaches system services.
/// </summary>
public sealed class AppContext
{
    public AppContext(AppId appId, IWindowManager windowManager, IServiceProvider services, ApplicationManifest? manifest = null)
    {
        AppId = appId;
        WindowManager = windowManager;
        Services = services;
        Manifest = manifest;
    }

    /// <summary>The id of the application this context belongs to.</summary>
    public AppId AppId { get; }

    /// <summary>The desktop window manager — used to create application windows.</summary>
    public IWindowManager WindowManager { get; }

    /// <summary>
    /// DI container for first-party applications compiled with the host. This is not exposed to
    /// package applications; they receive <see cref="IExternalAppContext"/> instead.
    /// </summary>
    public IServiceProvider Services { get; }
    public ApplicationManifest? Manifest { get; }

    /// <summary>Activates a host-validated <c>relaxkonos://</c> route or manifest-declared external URI as this application.</summary>
    public IAppActivation Activations => new AppActivationScope(AppId, Services.GetService(typeof(IAppActivationService)) as IAppActivationService);

    /// <summary>Permission status and approval requests scoped to this built-in application.</summary>
    public IAppPermissionScope Permissions => new AppPermissionScope(
        AppId, Services.GetService(typeof(IAppPermissionRequestService)) as IAppPermissionRequestService);

    /// <summary>Convenience: create and show a window owned by this application.</summary>
    public ManagedWindow ShowWindow(
        string title,
        Control content,
        Rect? bounds = null,
        string? iconGlyph = null,
        bool canResize = true,
        bool canMinimize = true,
        bool canMaximize = true)
        => ShowWindow(title, content, bounds, iconGlyph, canResize, canMinimize, canMaximize,
            WindowInitialPlacement.CenteredCascade);

    /// <summary>
    /// Creates and shows a window using an explicit initial-placement policy.
    /// Applications normally use the overload above, which centers and cascades windows.
    /// </summary>
    public ManagedWindow ShowWindow(
        string title,
        Control content,
        Rect? bounds,
        string? iconGlyph,
        bool canResize,
        bool canMinimize,
        bool canMaximize,
        WindowInitialPlacement initialPlacement)
    {
        return WindowManager.Create(new WindowCreateOptions(
            OwnerAppId: AppId,
            Title: title,
            Content: content,
            Bounds: bounds,
            IconGlyph: iconGlyph ?? Manifest?.IconGlyph,
            CanResize: canResize,
            CanMinimize: canMinimize,
            CanMaximize: canMaximize,
            InitialPlacement: initialPlacement,
            IconPath: Manifest?.EffectiveIconPath));
    }

    /// <summary>Displays an application window over the entire desktop, including shell chrome.</summary>
    public void EnterFullScreen(ManagedWindow window) => WindowManager.EnterFullScreen(window);

    /// <summary>Returns a full-screen application window to its prior size and state.</summary>
    public void ExitFullScreen(ManagedWindow window) => WindowManager.ExitFullScreen(window);

    /// <summary>Shows a dialog that blocks its owner until it is confirmed or cancelled.</summary>
    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Rect? bounds = null)
        => WindowManager.ShowDialogAsync(owner, title, contentFactory, bounds);

    /// <summary>Shows a dialog centered over its owner at the requested size.</summary>
    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize)
        => WindowManager.ShowDialogAsync(owner, title, contentFactory, preferredSize);

    private sealed class AppActivationScope(AppId sourceAppId, IAppActivationService? service) : IAppActivation
    {
        public AppActivationResult Activate(Uri uri, bool userInitiated = true, string? correlationId = null) =>
            service?.Activate(new AppActivationRequest(uri, sourceAppId, userInitiated, correlationId))
            ?? new AppActivationResult(AppActivationStatus.Unavailable);
    }

    private sealed class AppPermissionScope(AppId appId, IAppPermissionRequestService? service) : IAppPermissionScope
    {
        public AppPermissionStatus GetStatus(string permissionId) =>
            service?.GetStatus(appId, permissionId) ?? AppPermissionStatus.Undecided;

        public bool IsGranted(string permissionId) => GetStatus(permissionId) == AppPermissionStatus.Granted;

        public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default) =>
            service?.RequestAsync(appId, permissionId, cancellationToken)
            ?? Task.FromResult(AppPermissionStatus.Undecided);

        public Task OpenSettingsAsync() => service?.OpenSettingsAsync(appId) ?? Task.CompletedTask;
    }
}
