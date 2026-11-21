using RelaxKonOS.App.CodeEditor;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Protocol.Capabilities;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

/// <summary>Creates the capability-only context used by the future package loader.</summary>
public sealed class ExternalAppContextFactory
{
    private readonly IAppPermissionManager _permissions;
    private readonly IAppPermissionRequestService _permissionRequests;
    private readonly ISystemLanguage _systemLanguage;
    private readonly ShellSettings _settings;
    private readonly IWorkspaceSettingsService _settingsClient;
    private readonly IAuthSession _session;
    private readonly DefaultAppRegistry _defaultApps;
    private readonly IWindowManager _windowManager;
    private readonly ITaskManagerClient _systemMonitor;
    private readonly IRemoteEventAlertClient _alerts;
    private readonly IExplorerClient _files;
    private readonly SshDesktopSession _sshSession;
    private readonly SshExplorerClient _sshFiles;
    private readonly ISettingsNavigation _settingsNavigation;
    private readonly IAppCapabilityClient _capabilities;
    private readonly IAppSettingsClient _appSettings;
    private readonly IAppActivationService _activations;
    private readonly UsageMemoryStore _usageMemory;
    private readonly TextEditorEncodingSettings _textEncoding;

    public ExternalAppContextFactory(
        IAppPermissionManager permissions,
        IAppPermissionRequestService permissionRequests,
        ISystemLanguage systemLanguage,
        ShellSettings settings,
        IWorkspaceSettingsService settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps,
        IWindowManager windowManager,
        ITaskManagerClient systemMonitor,
        IExplorerClient files,
        SshDesktopSession sshSession,
        SshExplorerClient sshFiles,
        ISettingsNavigation settingsNavigation,
        IAppCapabilityClient capabilities,
        IAppSettingsClient appSettings,
        IAppActivationService activations,
        UsageMemoryStore usageMemory,
        TextEditorEncodingSettings textEncoding,
        IRemoteEventAlertClient alerts)
    {
        _permissions = permissions;
        _permissionRequests = permissionRequests;
        _systemLanguage = systemLanguage;
        _settings = settings;
        _settingsClient = settingsClient;
        _session = session;
        _defaultApps = defaultApps;
        _windowManager = windowManager;
        _systemMonitor = systemMonitor;
        _alerts = alerts;
        _files = files;
        _sshSession = sshSession;
        _sshFiles = sshFiles;
        _settingsNavigation = settingsNavigation;
        _capabilities = capabilities;
        _appSettings = appSettings;
        _activations = activations;
        _usageMemory = usageMemory;
        _textEncoding = textEncoding;
    }

    public IExternalAppContext Create(ApplicationManifest manifest, CancellationToken lifetime = default,
        Action<ManagedWindow>? onWindowCreated = null, string? openedFile = null)
    {
        var appId = manifest.Id;
        var sshEndpoint = _sshSession.Endpoint;
        var fileSession = _usageMemory.Capture(_session);
        return new ExternalAppContext(
        appId,
        new AppPermissionScope(appId, _permissionRequests),
        new DesktopAppearanceCapability(appId, _permissions, _settings, _settingsClient, _session, _defaultApps),
        new ServerMonitorCapability(appId, _permissions, _systemMonitor),
        new ServerAlertsCapability(appId, _permissions, _session, fileSession, _alerts),
        new ServerFilesCapability(appId, _permissions,
            sshEndpoint is null ? _files.DownloadAsync : (path, ct) => _sshFiles.DownloadAsync(path, sshEndpoint, ct),
            sshEndpoint is not null, sshEndpoint is not null || _session.CurrentServer?.Platform != RelaxKonOS.Protocol.Common.HostPlatformKind.Windows,
            openedFile, _files, fileSession, _windowManager),
        appId.Value == "relaxkonos.notepad" ? new NotepadEncodingPreferences(_textEncoding, fileSession) : null,
        new ExternalFileApiAccess(appId, _permissions, _session, _capabilities),
        new ExternalMediaService(appId, _permissions, _session, _capabilities),
        new ExternalAppSettings(appId, _appSettings),
        _systemLanguage,
        new ExternalAppActivation(appId, _activations),
        _settingsNavigation,
        new ExternalAppWindowService(manifest, _windowManager, lifetime, onWindowCreated));
    }

    private sealed record ExternalAppContext(
        AppId AppId,
        IAppPermissionScope Permissions,
        IDesktopAppearance DesktopAppearance,
        IServerMonitor ServerMonitor,
        IServerAlerts ServerAlerts,
        IServerFiles ServerFiles,
        IExternalTextEditorPreferences? TextEditorPreferences,
        IExternalFileApiAccess FileApi,
        IExternalMediaService Media,
        IExternalAppSettings SettingsStore,
        ISystemLanguage SystemLanguage,
        IAppActivation Activations,
        ISettingsNavigation Settings,
        IExternalAppWindowService Windows) : IExternalAppContext;

    private sealed class ServerAlertsCapability(AppId appId, IAppPermissionManager permissions,
        IAuthSession session, UsageMemoryScope connection, IRemoteEventAlertClient alerts) : IServerAlerts
    {
        public async Task<ServerAlertsResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerEventAlertsRead))
                return new(AppCapabilityResult.PermissionDenied);
            if (!IsConnected())
                return new(AppCapabilityResult.Unavailable);

            try
            {
                var summary = await alerts.SummaryAsync(connection, cancellationToken);
                if (!IsConnected())
                    return new(AppCapabilityResult.Unavailable);
                if (!permissions.IsGranted(appId, CoreAppPermissions.ServerEventAlertsRead))
                    return new(AppCapabilityResult.PermissionDenied);
                var page = await alerts.ListAlertsAsync(connection, cancellationToken);
                if (!IsConnected())
                    return new(AppCapabilityResult.Unavailable);
                if (!permissions.IsGranted(appId, CoreAppPermissions.ServerEventAlertsRead))
                    return new(AppCapabilityResult.PermissionDenied);
                return new(AppCapabilityResult.Succeeded,
                    new(summary.OpenCount, summary.AcknowledgedCount, summary.UnacknowledgedCriticalCount,
                        page.Items.Select(alert => new ServerAlert(alert.Severity.ToString(), alert.Type, alert.ProblemCode,
                            alert.Status.ToString(), alert.OccurrenceCount)).ToArray()));
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested
                && error is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
            {
                return new(AppCapabilityResult.Unavailable);
            }
        }

        private bool IsConnected() => connection.IsCurrent && session.State == AuthSessionState.Authenticated;
    }

    private sealed class NotepadEncodingPreferences(TextEditorEncodingSettings settings, UsageMemoryScope session) : IExternalTextEditorPreferences
    {
        public string DefaultEncoding => session.IsCurrent ? settings.NotepadDefaultEncoding : "UTF-8";
        public Task SetDefaultEncodingAsync(string encoding) => session.IsCurrent
            ? settings.SetNotepadDefaultEncodingAsync(encoding) : Task.CompletedTask;

        public Task<EncodingDialogAction?> PickEncodingActionAsync(IExternalAppWindowHandle owner) =>
            owner.ShowDialogAsync<EncodingDialogAction?>(LocalizedText.Get("common.file_encoding"),
                dialog => new EncodingActionDialogView
                {
                    DataContext = new EncodingActionDialogViewModel(choice => dialog.Close(choice)),
                }, new Size(420, 220));

        public Task<string?> PickEncodingAsync(IExternalAppWindowHandle owner, string currentEncoding) =>
            owner.ShowDialogAsync<string>(LocalizedText.Get("common.file_encoding"),
                dialog => new EncodingDialogView
                {
                    DataContext = new EncodingDialogViewModel(currentEncoding, encoding =>
                    {
                        if (encoding is null) dialog.Cancel();
                        else dialog.Close(encoding);
                    }),
                }, new Size(420, 330));
    }

    private sealed class ExternalAppActivation(AppId appId, IAppActivationService activations) : IAppActivation
    {
        public AppActivationResult Activate(Uri uri, bool userInitiated = true, string? correlationId = null) =>
            activations.Activate(new AppActivationRequest(uri, appId, userInitiated, correlationId));
    }

    private sealed class ExternalAppSettings(AppId appId, IAppSettingsClient client) : IExternalAppSettings
    {
        public async Task<ExternalAppSettingsDocument?> GetAsync(
            ExternalAppSettingsScope scope = ExternalAppSettingsScope.Workspace,
            string key = "default", CancellationToken cancellationToken = default)
        {
            var document = await client.GetAsync(appId.Value, ToProtocolScope(scope), key, cancellationToken);
            return document is null ? null : ToExternal(document);
        }

        public async Task<ExternalAppSettingsDocument> SetAsync(
            System.Text.Json.JsonElement value, int schemaVersion = 1,
            ExternalAppSettingsScope scope = ExternalAppSettingsScope.Workspace,
            string key = "default", long? expectedRevision = null, CancellationToken cancellationToken = default)
            => ToExternal(await client.SaveAsync(appId.Value, ToProtocolScope(scope), key, value,
                schemaVersion, expectedRevision, cancellationToken));

        private static AppSettingsScope ToProtocolScope(ExternalAppSettingsScope scope) => scope switch
        {
            ExternalAppSettingsScope.User => AppSettingsScope.User,
            ExternalAppSettingsScope.Workspace => AppSettingsScope.Workspace,
            ExternalAppSettingsScope.Device => AppSettingsScope.Device,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        private static ExternalAppSettingsDocument ToExternal(AppSettingsDocumentDto document) => new(
            document.Scope switch
            {
                AppSettingsScope.User => ExternalAppSettingsScope.User,
                AppSettingsScope.Workspace => ExternalAppSettingsScope.Workspace,
                AppSettingsScope.Device => ExternalAppSettingsScope.Device,
                _ => throw new ArgumentOutOfRangeException(nameof(document)),
            },
            document.Key, document.Value, document.SchemaVersion, document.Revision, document.UpdatedAt);
    }

    private sealed class ExternalAppWindowService(ApplicationManifest manifest, IWindowManager windowManager,
        CancellationToken lifetime, Action<ManagedWindow>? onWindowCreated) : IExternalAppWindowService
    {
        public IExternalAppWindowHandle ShowWindow(
            string title,
            Avalonia.Controls.Control content,
            Rect? bounds = null,
            string? iconGlyph = null,
            bool canResize = true,
            bool canMinimize = true,
            bool canMaximize = true)
        {
            lifetime.ThrowIfCancellationRequested();
            var window = windowManager.Create(new WindowCreateOptions(
                OwnerAppId: manifest.Id,
                Title: title,
                Content: content,
                Bounds: bounds,
                IconGlyph: iconGlyph ?? manifest.IconGlyph,
                CanResize: canResize,
                CanMinimize: canMinimize,
                CanMaximize: canMaximize,
                InitialPlacement: WindowInitialPlacement.CenteredCascade,
                IconPath: manifest.EffectiveIconPath));
            onWindowCreated?.Invoke(window);
            return new ExternalAppWindowHandle(window, windowManager);
        }
    }

    private sealed class ExternalAppWindowHandle : IExternalAppWindowHandle
    {
        private readonly IWindowManager _windowManager;
        private readonly EventHandler<ManagedWindow> _closedHandler;
        private readonly CancellationTokenSource _closed = new();

        public ExternalAppWindowHandle(ManagedWindow window, IWindowManager windowManager)
        {
            Window = window;
            _windowManager = windowManager;
            _closedHandler = (_, closedWindow) =>
            {
                if (!ReferenceEquals(closedWindow, Window)) return;
                _windowManager.WindowClosed -= _closedHandler;
                _closed.Cancel();
                _closed.Dispose();
            };
            _windowManager.WindowClosed += _closedHandler;
        }

        public ManagedWindow Window { get; }
        public CancellationToken Closed => _closed.Token;
        public bool IsFullScreen => Window.IsFullScreen;

        public void EnterFullScreen() => _windowManager.EnterFullScreen(Window);

        public void ExitFullScreen() => _windowManager.ExitFullScreen(Window);

        public Task<TResult?> ShowDialogAsync<TResult>(string title, Func<ModalDialog<TResult>, Avalonia.Controls.Control> content, Size preferredSize)
        {
            if (!_windowManager.Windows.Contains(Window)) return Task.FromResult<TResult?>(default);
            return _windowManager.ShowDialogAsync(Window, title, content, preferredSize);
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
            ShowDialogAsync<bool>(title, dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, dialog.Close, confirmLabel),
            }, new Size(460, 320));
    }

    private sealed class ExternalFileApiAccess(
        AppId appId,
        IAppPermissionManager permissions,
        IAuthSession session,
        IAppCapabilityClient capabilities) : IExternalFileApiAccess
    {
        public async Task<FileApiAccessResult> GetAccessAsync(CancellationToken cancellationToken = default)
        {
            var scopes = new List<string>();
            if (permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
            {
                scopes.Add(FileCapabilityScopes.List);
                scopes.Add(FileCapabilityScopes.Read);
            }
            if (permissions.IsGranted(appId, CoreAppPermissions.ServerFilesWrite))
            {
                scopes.Add(FileCapabilityScopes.Write);
                scopes.Add(FileCapabilityScopes.Manage);
            }
            if (scopes.Count == 0)
                return new FileApiAccessResult(AppCapabilityResult.PermissionDenied, null, null, null);
            if (session.EffectiveBaseUrl is null)
                return new FileApiAccessResult(AppCapabilityResult.Unavailable, null, null, null);

            try
            {
                var token = await capabilities.IssueFileTokenAsync(appId.Value, scopes, cancellationToken);
                return new FileApiAccessResult(AppCapabilityResult.Succeeded,
                    new Uri(session.EffectiveBaseUrl, UriKind.Absolute), token.AccessToken, token.ExpiresAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new FileApiAccessResult(AppCapabilityResult.Unavailable, null, null, null);
            }
        }
    }

    private sealed class ExternalMediaService(
        AppId appId,
        IAppPermissionManager permissions,
        IAuthSession session,
        IAppCapabilityClient capabilities) : IExternalMediaService
    {
        public async Task<ExternalMediaLeaseResult> OpenPlaybackAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new ExternalMediaLeaseResult(AppCapabilityResult.PermissionDenied, null);
            if (string.IsNullOrWhiteSpace(path))
                return new ExternalMediaLeaseResult(AppCapabilityResult.InvalidArgument, null);
            if (session.EffectiveBaseUrl is null)
                return new ExternalMediaLeaseResult(AppCapabilityResult.Unavailable, null);

            try
            {
                var created = await capabilities.CreateMediaLeaseAsync(appId.Value, path, cancellationToken);
                var playbackUri = new Uri(new Uri(session.EffectiveBaseUrl, UriKind.Absolute),
                    AppCapabilityRoutes.MediaStream(created.LeaseId).TrimStart('/'));
                return new ExternalMediaLeaseResult(AppCapabilityResult.Succeeded,
                    new HostMediaLease(playbackUri, created, capabilities,
                        () => permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                var detail = exception.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => "The file no longer exists on the server.",
                    System.Net.HttpStatusCode.Forbidden => "The server denied access to the file.",
                    System.Net.HttpStatusCode.Unauthorized => "Your RelaxKonOS session has expired.",
                    System.Net.HttpStatusCode.BadRequest => exception.Message,
                    { } statusCode => $"The server rejected the playback lease request (HTTP {(int)statusCode}).",
                    _ => "The server could not create a playback lease.",
                };
                return new ExternalMediaLeaseResult(AppCapabilityResult.Unavailable, null, detail);
            }
            catch (Exception)
            {
                return new ExternalMediaLeaseResult(AppCapabilityResult.Unavailable, null,
                    "The RelaxKonOS server could not be reached.");
            }
        }
    }

    private sealed class HostMediaLease : IExternalMediaLease
    {
        private readonly IAppCapabilityClient _capabilities;
        private readonly Func<bool> _canRenew;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _renewal;
        private readonly object _gate = new();
        private readonly string _leaseId;
        private DateTimeOffset _expiresAt;
        private bool _disposed;

        public HostMediaLease(Uri playbackUri, MediaLeaseDto created, IAppCapabilityClient capabilities, Func<bool> canRenew)
        {
            PlaybackUri = playbackUri;
            _leaseId = created.LeaseId;
            _expiresAt = created.ExpiresAt;
            _capabilities = capabilities;
            _canRenew = canRenew;
            _renewal = RenewUntilDisposedAsync();
        }

        public Uri PlaybackUri { get; }
        public DateTimeOffset ExpiresAt
        {
            get { lock (_gate) return _expiresAt; }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            await _shutdown.CancelAsync();
            try { await _renewal; }
            catch (OperationCanceledException) { }
            try { await _capabilities.RevokeMediaLeaseAsync(_leaseId); }
            catch { /* The short server-side expiry is the fallback revocation path. */ }
            _shutdown.Dispose();
        }

        private async Task RenewUntilDisposedAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), _shutdown.Token);
                    if (!_canRenew())
                    {
                        try { await _capabilities.RevokeMediaLeaseAsync(_leaseId, _shutdown.Token); }
                        catch { /* The short lease expiry remains the fallback. */ }
                        return;
                    }
                    try
                    {
                        var renewed = await _capabilities.RenewMediaLeaseAsync(_leaseId, _shutdown.Token);
                        lock (_gate)
                            _expiresAt = renewed.ExpiresAt;
                    }
                    catch when (!_shutdown.IsCancellationRequested && ExpiresAt > DateTimeOffset.UtcNow)
                    {
                        // A short network interruption should not immediately end an otherwise active player.
                        await Task.Delay(TimeSpan.FromSeconds(10), _shutdown.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // The owner has disposed the media lease.
            }
            catch
            {
                // Renewal failed after the lease expired or the host session became unavailable.
            }
        }
    }

    private sealed class AppPermissionScope(
        AppId appId,
        IAppPermissionRequestService requests) : IAppPermissionScope
    {
        public AppPermissionStatus GetStatus(string permissionId) => requests.GetStatus(appId, permissionId);
        public bool IsGranted(string permissionId) => GetStatus(permissionId) == AppPermissionStatus.Granted;

        public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default) =>
            requests.RequestAsync(appId, permissionId, cancellationToken);

        public Task OpenSettingsAsync() => requests.OpenSettingsAsync(appId);
    }

    private sealed class DesktopAppearanceCapability(
        AppId appId,
        IAppPermissionManager permissions,
        ShellSettings settings,
        IWorkspaceSettingsService settingsClient,
        IAuthSession session,
        DefaultAppRegistry defaultApps) : IDesktopAppearance
    {
        public async Task<AppCapabilityResult> SetWallpaperAsync(string wallpaperKey, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.DesktopWallpaperWrite))
                return AppCapabilityResult.PermissionDenied;

            if (!settings.TrySetWallpaperKey(wallpaperKey))
                return AppCapabilityResult.InvalidArgument;

            if (session is not { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
                return AppCapabilityResult.Succeeded;

            try
            {
                await settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id,
                    settings.ToPreferences(defaultApps.Snapshot), cancellationToken);
                return AppCapabilityResult.Succeeded;
            }
            catch
            {
                return AppCapabilityResult.Unavailable;
            }
        }
    }

    private sealed class ServerMonitorCapability(
        AppId appId,
        IAppPermissionManager permissions,
        ITaskManagerClient systemMonitor) : IServerMonitor
    {
        public async Task<ServerMetricsResult> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerMetricsRead))
                return new ServerMetricsResult(AppCapabilityResult.PermissionDenied, null);

            try
            {
                var info = await systemMonitor.GetPerformanceInfoAsync(cancellationToken);
                var snapshot = await systemMonitor.GetPerformanceSnapshotAsync(cancellationToken);
                return new ServerMetricsResult(AppCapabilityResult.Succeeded, ToSnapshot(info, snapshot));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ServerMetricsResult(AppCapabilityResult.Unavailable, null);
            }
        }

        public async IAsyncEnumerable<ServerMetricsResult> WatchAsync(TimeSpan? interval = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var cadence = interval ?? TimeSpan.FromSeconds(2);
            cadence = TimeSpan.FromMilliseconds(Math.Clamp(cadence.TotalMilliseconds, 1000, 60000));
            while (!cancellationToken.IsCancellationRequested)
            {
                yield return await GetSnapshotAsync(cancellationToken);
                await Task.Delay(cadence, cancellationToken);
            }
        }

        private static ServerMetricsSnapshot ToSnapshot(PerformanceInfoDto info, PerformanceRealtimeSnapshotDto snapshot)
        {
            var filesystemNames = info.Filesystems.ToDictionary(filesystem => filesystem.Id, filesystem => filesystem.Name, StringComparer.Ordinal);
            var networkNames = info.Networks.ToDictionary(network => network.Id, network => network.Name, StringComparer.Ordinal);
            return new ServerMetricsSnapshot(
                snapshot.Timestamp,
                snapshot.Cpu.TotalPercent,
                info.Cpu.LogicalProcessorCount,
                snapshot.Cpu.PerLogicalCpuPercent,
                snapshot.Memory.TotalBytes,
                snapshot.Memory.UsedBytes,
                snapshot.Memory.AvailableBytes,
                snapshot.Memory.TotalBytes == 0 ? 0 : snapshot.Memory.UsedBytes * 100d / snapshot.Memory.TotalBytes,
                snapshot.Filesystems.Select(filesystem => new ServerDiskMetric(
                    filesystemNames.GetValueOrDefault(filesystem.Id, filesystem.Id), filesystem.TotalBytes, filesystem.UsedBytes,
                    filesystem.AvailableBytes, filesystem.Percent)).ToArray(),
                snapshot.Networks.Select(network => new ServerNetworkMetric(
                    networkNames.GetValueOrDefault(network.Id, network.Id), network.SendBytesPerSecond, network.ReceiveBytesPerSecond)).ToArray(),
                [],
                snapshot.UptimeSeconds);
        }
    }

    private sealed class ServerFilesCapability(
        AppId appId,
        IAppPermissionManager permissions,
        Func<string, CancellationToken, Task<(Stream Stream, string FileName)?>> downloadFile,
        bool isSshSession,
        bool isPathCaseSensitive,
        string? openedFile,
        IExplorerClient files,
        UsageMemoryScope session,
        IWindowManager windows) : IServerFiles
    {
        public bool IsSshSession => isSshSession;
        public bool IsPathCaseSensitive => isPathCaseSensitive;
        private bool IsServerSession => !isSshSession && session.Session.State == AuthSessionState.Authenticated && session.IsCurrent;

        public async Task<ServerFileReadResult> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new ServerFileReadResult(AppCapabilityResult.PermissionDenied, null, null);
            // SSH file access is limited to the selection handed off by the host file browser.
            if (string.IsNullOrWhiteSpace(path) || (isSshSession && !string.Equals(path, openedFile, StringComparison.Ordinal)))
                return new ServerFileReadResult(AppCapabilityResult.InvalidArgument, null, null);
            if (!isSshSession && !IsServerSession)
                return new ServerFileReadResult(AppCapabilityResult.Unavailable, null, null);

            try
            {
                var result = await downloadFile(path, cancellationToken);
                return result is { } download
                    ? new ServerFileReadResult(AppCapabilityResult.Succeeded, download.Stream, download.FileName)
                    : new ServerFileReadResult(AppCapabilityResult.InvalidArgument, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new ServerFileReadResult(AppCapabilityResult.Unavailable, null, null);
            }
        }

        public async Task<ServerFileContentResult> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new(AppCapabilityResult.PermissionDenied);
            if (string.IsNullOrWhiteSpace(path)) return new(AppCapabilityResult.InvalidArgument);
            if (!IsServerSession) return new(AppCapabilityResult.Unavailable);
            var bytes = await files.ReadFileAsync(path, cancellationToken);
            if (!IsServerSession) return new(AppCapabilityResult.Unavailable);
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new(AppCapabilityResult.PermissionDenied);
            return new(AppCapabilityResult.Succeeded, bytes, bytes is null ? null : GitTextFileWrite.Version(bytes));
        }

        public async Task<ServerDirectoryResult> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new(AppCapabilityResult.PermissionDenied);
            if (string.IsNullOrWhiteSpace(path)) return new(AppCapabilityResult.InvalidArgument);
            if (!IsServerSession) return new(AppCapabilityResult.Unavailable);

            var directory = await files.GetDirectoryAsync(path, cancellationToken);
            if (!IsServerSession) return new(AppCapabilityResult.Unavailable);
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead))
                return new(AppCapabilityResult.PermissionDenied);

            return new(AppCapabilityResult.Succeeded, new(directory.Path, directory.Name,
                directory.Directories.Select(entry => new ServerDirectoryEntry(entry.Path, entry.Name, IsDirectory: true))
                    .Concat(directory.Files.Select(entry => new ServerDirectoryEntry(entry.Path, entry.Name, IsDirectory: false)))
                    .ToArray()));
        }

        public async Task<AppCapabilityResult> WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default)
        {
            var status = CheckWriteAccess(path, cancellationToken);
            if (status != AppCapabilityResult.Succeeded) return status;
            await files.WriteFileAsync(path, content, cancellationToken);
            return AppCapabilityResult.Succeeded;
        }

        public async Task<AppCapabilityResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            var status = CheckWriteAccess(path, cancellationToken);
            if (status != AppCapabilityResult.Succeeded) return status;
            await files.CreateDirectoryAsync(path, cancellationToken);
            return AppCapabilityResult.Succeeded;
        }

        public async Task<ServerFileWriteResult> WriteConditionalAsync(string path, byte[] content, string? expectedVersion,
            CancellationToken cancellationToken)
        {
            var status = CheckWriteAccess(path, cancellationToken);
            if (status != AppCapabilityResult.Succeeded) return new(status);
            if (files is not IEditorFileWriter writer) return new(AppCapabilityResult.Unavailable);
            try
            {
                var version = await writer.WriteEditorFileAsync(path, content, expectedVersion, cancellationToken);
                return IsServerSession ? new(AppCapabilityResult.Succeeded, version) : new(AppCapabilityResult.Unavailable);
            }
            catch (RelaxKonOSAuthException error) when (error.Status == 409
                && error.Type == "https://relaxkonos.app/problems/text-file-changed")
            {
                return new(AppCapabilityResult.Conflict);
            }
        }

        public async Task<ServerFileRenameResult> RenameAsync(string path, string newName, CancellationToken cancellationToken)
        {
            var status = CheckWriteAccess(path, cancellationToken);
            if (status != AppCapabilityResult.Succeeded) return new(status);
            if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." || newName.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
                return new(AppCapabilityResult.InvalidArgument);
            var renamed = await files.RenameAsync(path, newName, cancellationToken);
            return IsServerSession ? new(AppCapabilityResult.Succeeded, renamed.Path) : new(AppCapabilityResult.Unavailable);
        }

        public async Task<AppCapabilityResult> DeleteAsync(string path, CancellationToken cancellationToken)
        {
            var status = CheckWriteAccess(path, cancellationToken);
            if (status != AppCapabilityResult.Succeeded) return status;
            await files.DeleteAsync(path, cancellationToken);
            return AppCapabilityResult.Succeeded;
        }

        private AppCapabilityResult CheckWriteAccess(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!permissions.IsGranted(appId, CoreAppPermissions.ServerFilesWrite))
                return AppCapabilityResult.PermissionDenied;
            if (string.IsNullOrWhiteSpace(path)) return AppCapabilityResult.InvalidArgument;
            return IsServerSession ? AppCapabilityResult.Succeeded : AppCapabilityResult.Unavailable;
        }

        public async Task<ServerFilePickResult> PickAsync(IExternalAppWindowHandle owner, ServerFilePickerOptions options)
        {
            var save = options.Mode == ServerFilePickerMode.SaveFile;
            bool HasGrants() => permissions.IsGranted(appId, CoreAppPermissions.ServerFilesRead)
                && (!save || permissions.IsGranted(appId, CoreAppPermissions.ServerFilesWrite));

            if (!HasGrants()) return new(AppCapabilityResult.PermissionDenied);
            if (!IsServerSession) return new(AppCapabilityResult.Unavailable);
            if (owner is not ExternalAppWindowHandle handle || handle.Window.Info.OwnerAppId != appId || !windows.Windows.Contains(handle.Window))
                return new(AppCapabilityResult.InvalidArgument);

            var mode = options.Mode switch
            {
                ServerFilePickerMode.OpenFile => ExplorerPickerMode.OpenFile,
                ServerFilePickerMode.SelectFolder => ExplorerPickerMode.SelectFolder,
                ServerFilePickerMode.SaveFile => ExplorerPickerMode.SaveFile,
                _ => (ExplorerPickerMode?)null,
            };
            if (mode is null || !save && options.SuggestedFileName is not null)
                return new(AppCapabilityResult.InvalidArgument);
            var path = await handle.ShowDialogAsync<string>(options.Title, dialog =>
            {
                var picker = new ExplorerViewModel(files, new ExplorerPickerOptions(mode.Value,
                    Filters: options.Filters?.Select(filter => new ExplorerFileFilter(filter.Label, filter.Patterns,
                        filter.IncludeExtensionlessFiles)).ToArray(),
                    DefaultFileName: options.SuggestedFileName), paths => dialog.Close(paths[0]),
                    usageMemory: session, memoryPurpose: appId.Value)
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, new Size(760, 520));

            if (!HasGrants()) return new(AppCapabilityResult.PermissionDenied);
            return IsServerSession ? new(AppCapabilityResult.Succeeded, path) : new(AppCapabilityResult.Unavailable);
        }
    }
}
