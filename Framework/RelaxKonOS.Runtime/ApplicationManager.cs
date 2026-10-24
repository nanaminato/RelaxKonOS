using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Runtime;

/// <summary>
/// The RelaxKonOS application runtime: maintains the registry of installed applications and
/// launches them on demand, wiring each launch to the shared <see cref="IWindowManager"/>.
/// </summary>
public sealed class ApplicationManager : IAppActivationService
{
    private readonly Dictionary<AppId, IRemoteApplication> _apps = new();
    private readonly HashSet<AppId> _builtInApps = [];
    private readonly Dictionary<AppId, Action> _pendingActivations = [];
    private readonly IWindowManager _windowManager;
    private readonly IServiceProvider _services;

    public ApplicationManager(IWindowManager windowManager, IServiceProvider services)
    {
        _windowManager = windowManager;
        _services = services;
    }

    /// <summary>Raised whenever the launchable application registry changes.</summary>
    public event EventHandler? RegistryChanged;

    /// <summary>Metadata for every registered application (desktop / start menu).</summary>
    public IReadOnlyList<ApplicationInfo> Registered =>
        _apps.Values.Select(a => a.Manifest.ToInfo()).OrderBy(i => i.DisplayName).ToList();

    /// <summary>Applications that explicitly declare one or more supported file rules.</summary>
    public IReadOnlyList<ApplicationInfo> FileOpeners =>
        _apps.Values.Where(a => a is IFileOpenApplication && a.Manifest.SupportsFiles).Select(a => a.Manifest.ToInfo())
            .OrderBy(i => i.DisplayName).ToList();

    /// <summary>Applications eligible to open the specified file extension.</summary>
    public IReadOnlyList<ApplicationInfo> FileOpenersForExtension(string extension) =>
        FileOpeners.Where(app => app.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Applications eligible to open the supplied path, ordered by rule specificity.</summary>
    public IReadOnlyList<ApplicationInfo> FileOpenersForPath(string path) =>
        _apps.Values
            .Where(app => app is IFileOpenApplication && app.Manifest.SupportsFile(path))
            .OrderByDescending(app => app.Manifest.GetFileMatchPriority(path))
            .ThenBy(app => app.Manifest.DisplayName)
            .Select(app => app.Manifest.ToInfo())
            .ToList();

    /// <summary>
    /// Applications that declared <see cref="ApplicationManifest.SupportsTextFiles"/>: they
    /// accept any file the Shell has confirmed is text via content sniffing. Ordered by
    /// display name so the desktop picker shows a stable list.
    /// </summary>
    public IReadOnlyList<ApplicationInfo> TextFileOpeners =>
        _apps.Values
            .Where(app => app is IFileOpenApplication && app.Manifest.SupportsTextFiles)
            .Select(app => app.Manifest.ToInfo())
            .OrderBy(info => info.DisplayName)
            .ToList();

    /// <summary>
    /// Opens a file in a <see cref="ApplicationManifest.SupportsTextFiles"/> application,
    /// bypassing <see cref="ApplicationManifest.SupportsFile"/>. Only callers that have
    /// already confirmed the file is text (e.g. the Shell after content sniffing) should
    /// use this entry point; the regular <see cref="OpenFile"/> path remains the default.
    /// </summary>
    public bool OpenFileAsText(AppId id, string path)
    {
        if (!_apps.TryGetValue(id, out var app) || app is not IFileOpenApplication fileOpener
            || !app.Manifest.SupportsTextFiles)
            return false;

        return Accepted(GuardActivation(app, () => OpenFileCore(app, fileOpener, path)));
    }

    /// <summary>Whether the registered application explicitly accepts the supplied file path.</summary>
    public bool SupportsFile(AppId id, string path) =>
        _apps.TryGetValue(id, out var app)
        && app is IFileOpenApplication
        && app.Manifest.SupportsFile(path);

    /// <summary>Register an application so it can be launched.</summary>
    public void Register(IRemoteApplication application)
    {
        _apps[application.Manifest.Id] = application;
        _builtInApps.Remove(application.Manifest.Id);
        RegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Registers a Host-shipped app. Package manifests cannot self-assign this origin.</summary>
    public void RegisterBuiltIn(IRemoteApplication application)
    {
        _apps[application.Manifest.Id] = application;
        _builtInApps.Add(application.Manifest.Id);
        RegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a dynamically loaded application from the launch registry.</summary>
    public bool Unregister(AppId id)
    {
        var removed = _apps.Remove(id);
        _builtInApps.Remove(id);
        if (removed)
            RegistryChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public bool IsRegistered(AppId id) => _apps.ContainsKey(id);
    /// <summary>Returns whether the Host registered this app as built-in, rather than inferring it from its id.</summary>
    public bool IsBuiltIn(AppId id) => _builtInApps.Contains(id);
    public IRemoteApplication? Get(AppId id) => _apps.GetValueOrDefault(id);
    public ApplicationManifest? GetManifest(AppId id) => _apps.GetValueOrDefault(id)?.Manifest;

    /// <summary>Returns the current host compatibility without showing an unavailable-app window.</summary>
    public ApplicationCompatibilityResult EvaluateCompatibility(ApplicationManifest manifest) =>
        (_services.GetService(typeof(IApplicationCompatibilityEvaluator)) as IApplicationCompatibilityEvaluator)
            ?.Evaluate(manifest) ?? ApplicationCompatibilityResult.Compatible;

    /// <summary>Accept a launch request, checking current prerequisites before opening or reusing its window.</summary>
    public bool Launch(AppId id)
    {
        if (!_apps.TryGetValue(id, out var app))
            return false;

        return Accepted(ActivateApplication(app, null));
    }

    /// <summary>Open a file in a registered file-opening application.</summary>
    public bool OpenFile(AppId id, string path) => Accepted(OpenFileResult(id, path));

    private AppActivationResult OpenFileResult(AppId id, string path)
    {
        if (!_apps.TryGetValue(id, out var app) || app is not IFileOpenApplication fileOpener
            || !app.Manifest.SupportsFile(path))
            return new(AppActivationStatus.Unavailable, id);

        return GuardActivation(app, () => OpenFileCore(app, fileOpener, path));
    }

    private void OpenFileCore(IRemoteApplication app, IFileOpenApplication fileOpener, string path)
    {
        var existing = FindExistingPrimaryWindow(app.Manifest.Id);
        if (existing is not null && app.Manifest.InstancePolicy == ApplicationInstancePolicy.SingleWindow)
        {
            _windowManager.Restore(existing);
            _windowManager.Focus(existing);
        }
        else fileOpener.OpenFile(new AppContext(app.Manifest.Id, _windowManager, _services, app.Manifest), path);
        RequestUndecidedPermissions(app.Manifest.Id);
    }

    /// <summary>
    /// Opens a new terminal in <paramref name="workingDirectory"/> using a registered terminal
    /// application. This lets applications request a terminal without taking a dependency on a
    /// concrete terminal implementation.
    /// </summary>
    public bool OpenTerminal(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return false;

        var terminal = _apps.Values
            .Where(app => app is IOpenTerminalApplication)
            .OrderBy(app => app.Manifest.DisplayName)
            .FirstOrDefault();
        if (terminal is not IOpenTerminalApplication terminalOpener)
            return false;

        return Accepted(GuardActivation(terminal, () =>
        {
            terminalOpener.OpenTerminal(new AppContext(terminal.Manifest.Id, _windowManager, _services, terminal.Manifest), workingDirectory);
            RequestUndecidedPermissions(terminal.Manifest.Id);
        }));
    }

    /// <summary>
    /// Resolves a Shell-owned <c>relaxkonos://</c> URI or a manifest-declared external URI scheme.
    /// </summary>
    public AppActivationResult Activate(AppActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Uri);
        var uri = request.Uri;
        Log($"Activation requested: uri={FormatUri(uri)}, source={request.SourceAppId?.Value ?? "<shell>"}, userInitiated={request.UserInitiated}, correlation={request.CorrelationId ?? "<none>"}.");
        if (!uri.IsAbsoluteUri || string.IsNullOrWhiteSpace(uri.Scheme) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != -1)
        {
            Log($"Activation rejected: absolute={uri.IsAbsoluteUri}, hasScheme={!string.IsNullOrWhiteSpace(uri.Scheme)}, hasUserInfo={!string.IsNullOrEmpty(uri.UserInfo)}, port={uri.Port}.");
            return new AppActivationResult(AppActivationStatus.InvalidUri);
        }

        if (!uri.Scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase))
            return ActivateExternalUri(request);

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            Log("Shell route rejected: URI host is empty.");
            return new AppActivationResult(AppActivationStatus.InvalidUri);
        }

        if (uri.Host.Equals("file", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/open", StringComparison.OrdinalIgnoreCase))
            return ActivateFileOpen(request);

        var matches = _apps.Values
            .Where(app => app is IAppActivationHandler handler && handler.CanHandleActivation(uri))
            .ToArray();
        if (matches.Length != 1)
        {
            Log($"Shell route not found: matches={matches.Length}.");
            return new AppActivationResult(AppActivationStatus.RouteNotFound);
        }

        var application = matches[0];
        var activated = ActivateApplication(application, request);
        Log($"Shell route target={application.Manifest.Id.Value}, activated={activated}.");
        return activated;
    }

    private AppActivationResult ActivateExternalUri(AppActivationRequest request)
    {
        var uri = request.Uri;
        if (uri.Scheme.Length > 32 || string.IsNullOrWhiteSpace(uri.Host) || uri.Query.Length > 4097)
        {
            Log("External route rejected: scheme, host, or query validation failed.");
            return new AppActivationResult(AppActivationStatus.InvalidUri);
        }

        var matches = _apps.Values
            .Where(app => app.Manifest.UriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            .Where(app => app is IAppActivationHandler handler && handler.CanHandleActivation(uri))
            .ToArray();

        var preferredId = (_services.GetService(typeof(IUriSchemeDefaultResolver)) as IUriSchemeDefaultResolver)
            ?.ResolveDefaultApplication(uri.Scheme);
        Log($"External route candidates: scheme={uri.Scheme}, count={matches.Length}, ids=[{string.Join(',', matches.Select(app => app.Manifest.Id.Value))}], default={preferredId?.Value ?? "<none>"}.");
        var application = preferredId is { } preferred
            ? matches.SingleOrDefault(app => app.Manifest.Id == preferred)
            : null;
        if (preferredId is not null && application is null)
            Log($"Configured default is not an eligible handler for this URI: target={preferredId.Value}.");
        if (application is not null)
        {
            var activated = ActivateApplication(application, request);
            Log($"External route chose default target={application.Manifest.Id.Value}, activated={activated}.");
            return activated;
        }

        if (matches.Length == 1)
        {
            application = matches[0];
            var activated = ActivateApplication(application, request);
            Log($"External route chose sole target={application.Manifest.Id.Value}, activated={activated}.");
            return activated;
        }

        var routingUi = _services.GetService(typeof(IUriSchemeRoutingUi)) as IUriSchemeRoutingUi;
        if (matches.Length == 0)
        {
            if (request.UserInitiated && routingUi is not null)
            {
                Log("External route has no handler: scheduling missing-handler prompt.");
                _ = NotifyNoHandlerAsync(routingUi, uri);
            }
            else
                Log("External route has no handler and no prompt can be shown.");
            return new AppActivationResult(AppActivationStatus.NoHandler);
        }

        if (request.UserInitiated && routingUi is not null)
        {
            Log("External route is ambiguous: scheduling handler picker.");
            _ = ChooseExternalHandlerAsync(routingUi, request, matches);
            return new AppActivationResult(AppActivationStatus.HandlerSelectionRequired);
        }

        Log("External route is ambiguous and no user picker is available.");
        return new AppActivationResult(AppActivationStatus.RouteNotFound);
    }

    private async Task ChooseExternalHandlerAsync(IUriSchemeRoutingUi routingUi, AppActivationRequest request,
        IReadOnlyList<IRemoteApplication> candidates)
    {
        try
        {
            Log($"Handler picker opened: uri={FormatUri(request.Uri)}, candidates={candidates.Count}.");
            var choice = await routingUi.ChooseHandlerAsync(request.Uri,
                candidates.Select(candidate => candidate.Manifest.ToInfo()).ToArray());
            if (choice is null)
            {
                Log("Handler picker dismissed without a selection.");
                return;
            }

            var application = candidates.SingleOrDefault(candidate => candidate.Manifest.Id == choice.ApplicationId);
            if (application is null)
            {
                Log($"Handler picker selection is no longer registered: target={choice.ApplicationId.Value}.");
                return; // The package registry changed while the dialog was open.
            }

            Log($"Handler picker selected target={application.Manifest.Id.Value}, setDefault={choice.SetAsDefault}.");

            if (choice.SetAsDefault)
            {
                try
                {
                    await routingUi.SaveDefaultHandlerAsync(request.Uri.Scheme, application.Manifest.Id);
                    Log($"Default handler saved: scheme={request.Uri.Scheme}, target={application.Manifest.Id.Value}.");
                }
                catch (Exception exception)
                {
                    Log($"Default handler save failed: {exception.GetType().Name}: {exception.Message}");
                }
            }

            var activated = ActivateApplication(application, request);
            Log($"Handler picker target activation completed: target={application.Manifest.Id.Value}, activated={activated}.");
        }
        catch (Exception exception)
        {
            Log($"Handler picker failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task NotifyNoHandlerAsync(IUriSchemeRoutingUi routingUi, Uri uri)
    {
        try { await routingUi.NotifyNoHandlerAsync(uri); }
        catch (Exception exception)
        {
            Log($"Missing-handler prompt failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private AppActivationResult ActivateFileOpen(AppActivationRequest request)
    {
        // Host paths are intentionally not an inter-package protocol. Only the first-party
        // file browsers and the Shell (SourceAppId is null) may use this route; package
        // applications must use their file capability APIs. The SSH browser owns SFTP paths in
        // an SSH-only desktop, so it needs the same host-routed handoff as Explorer.
        if (request.SourceAppId is { Value: not ("relaxkonos.explorer" or "relaxkonos.ssh-files") })
            return new AppActivationResult(AppActivationStatus.Unavailable);

        var values = ParseQuery(request.Uri);
        if (!values.TryGetValue("appId", out var appId) || !values.TryGetValue("path", out var path)
            || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(path))
            return new AppActivationResult(AppActivationStatus.InvalidUri);

        var target = new AppId(appId);
        return OpenFileResult(target, path);
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split < 1) continue;
            var key = Uri.UnescapeDataString(pair[..split]);
            var value = Uri.UnescapeDataString(pair[(split + 1)..]);
            if (key.Length <= 32 && value.Length <= 4096)
                values[key] = value;
        }
        return values;
    }

    private static bool Accepted(AppActivationResult result) => result.Succeeded || result.IsPendingAvailabilityCheck;

    private AppActivationResult GuardActivation(IRemoteApplication app, Action activate)
    {
        var id = app.Manifest.Id;
        if (!EnsureCompatible(app.Manifest)) return new(AppActivationStatus.Unavailable, id);
        if (_services.GetService(typeof(IApplicationLaunchGuard)) is IApplicationLaunchGuard guard
            && guard.RequiresCheck(app.Manifest))
        {
            if (_pendingActivations.ContainsKey(id))
                _pendingActivations[id] = activate; // One pending check; the latest local navigation wins.
            else
            {
                _pendingActivations.Add(id, activate);
                _ = CompleteGuardedActivationAsync(app, guard);
            }
            return new(AppActivationStatus.AvailabilityCheckPending, id);
        }
        activate();
        return new(AppActivationStatus.Activated, id);
    }

    private async Task CompleteGuardedActivationAsync(IRemoteApplication app, IApplicationLaunchGuard guard)
    {
        try
        {
            var context = new AppContext(app.Manifest.Id, _windowManager, _services, app.Manifest);
            if (!await guard.CheckAsync(context, FindExistingPrimaryWindow(app.Manifest.Id))) return;
            if (_apps.TryGetValue(app.Manifest.Id, out var registered) && ReferenceEquals(registered, app)
                && EnsureCompatible(app.Manifest) && _pendingActivations.TryGetValue(app.Manifest.Id, out var activate))
                activate();
        }
        catch (Exception exception)
        {
            // A failed or interrupted observation must never fall through to activation.
            Log($"Application prerequisite check failed: target={app.Manifest.Id.Value}, error={exception.GetType().Name}.");
        }
        finally { _pendingActivations.Remove(app.Manifest.Id); }
    }

    private AppActivationResult ActivateApplication(IRemoteApplication app, AppActivationRequest? request) =>
        GuardActivation(app, () => ActivateApplicationCore(app, request));

    private void ActivateApplicationCore(IRemoteApplication app, AppActivationRequest? request)
    {
        var context = new AppContext(app.Manifest.Id, _windowManager, _services, app.Manifest);
        var existing = FindExistingPrimaryWindow(app.Manifest.Id);
        if (existing is not null && app.Manifest.InstancePolicy == ApplicationInstancePolicy.SingleWindow)
        {
            if (request is not null && app is IAppActivationHandler handler)
                handler.HandleActivation(context, request, existing);
            _windowManager.Restore(existing);
            _windowManager.Focus(existing);
            RequestUndecidedPermissions(app.Manifest.Id);
            return;
        }
        app.Activate(context);
        RequestUndecidedPermissions(app.Manifest.Id);
        if (request is not null && app is IAppActivationHandler activationHandler)
            activationHandler.HandleActivation(context, request, FindExistingPrimaryWindow(app.Manifest.Id));
    }

    private void RequestUndecidedPermissions(AppId appId)
    {
        var service = _services.GetService(typeof(IAppPermissionRequestService)) as IAppPermissionRequestService;
        if (service is null)
            return;

        // Permission decisions are intentionally not part of application activation: the app
        // is already open and remains usable if every request is rejected or deferred.
        _ = RequestUndecidedPermissionsAsync(service, appId);
    }

    private static async Task RequestUndecidedPermissionsAsync(IAppPermissionRequestService service, AppId appId)
    {
        try { await service.RequestUndecidedAsync(appId); }
        catch { /* A prompt failure must never turn a successful launch into a failed launch. */ }
    }

    private ManagedWindow? FindExistingPrimaryWindow(AppId appId) => _windowManager.Windows
        .LastOrDefault(window => window.Info.OwnerAppId == appId && !window.IsModalDialog);

    private void Log(string message) =>
        (_services.GetService(typeof(IAppActivationDiagnostics)) as IAppActivationDiagnostics)?.Record(message);

    private static string FormatUri(Uri uri) => !uri.IsAbsoluteUri
        ? "<relative-uri>"
        : $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

    private bool EnsureCompatible(ApplicationManifest manifest)
    {
        var result = EvaluateCompatibility(manifest);
        if (result.IsCompatible)
            return true;

        (_services.GetService(typeof(IApplicationCompatibilityNotifier)) as IApplicationCompatibilityNotifier)
            ?.Notify(manifest, result);
        return false;
    }
}
