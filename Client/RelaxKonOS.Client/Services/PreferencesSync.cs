using RelaxKonOS.Client.Services.Diagnostics;
using Avalonia.Threading;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services;

/// <summary>用户偏好同步（单例）。监听 <see cref="IAuthSession.StateChanged"/>：
/// 认证成功 → 从服务端拉取 <see cref="WorkspacePreferencesDto"/>，应用到 <see cref="ShellSettings"/>
/// （壁纸/主题/时间格式/语言/区域，桌面外壳即时生效）并填充 <see cref="DefaultAppRegistry"/>；
/// 登出 → 重置为默认偏好。设置草稿与保存由独立 <c>WorkspacePreferencesEditor</c> 处理。</summary>
public sealed class PreferencesSync : IDisposable
{
    private readonly ClientPresentationPreferences _presentation;
    private readonly PresentationSettingsChanges _changes;
    private string _syncMode = "event";
    private bool _presentationActive;
    private readonly IAuthSession _session;
    private readonly IWorkspaceSettingsService _client;
    private readonly ShellSettings _settings;
    private readonly DefaultAppRegistry _registry;
    private readonly WallpaperService _wallpapers;
    private readonly WorkspacePreferencesEditor _editor;
    private CancellationTokenSource? _streamCancellation;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _loadGate = new();
    private Task _currentLoadTask = Task.CompletedTask;
    private string? _currentLoadScope;
    private bool _desktopRetryScheduled;
    private int _desktopRetrySeconds = 1;

    public PreferencesSync(
        IAuthSession session,
        IWorkspaceSettingsService client,
        ShellSettings settings,
        DefaultAppRegistry registry,
        WallpaperService wallpapers,
        WorkspacePreferencesEditor editor, ClientPresentationPreferences presentation, PresentationSettingsChanges changes)
    {
        _presentation = presentation; _changes = changes;
        _presentation.Changed += OnPolicyChanged;
        _changes.RecoveryRequested += OnRecoveryRequested;
        _session = session;
        _client = client;
        _settings = settings;
        _registry = registry;
        _wallpapers = wallpapers;
        _editor = editor;
        _session.StateChanged += OnStateChanged;
        // 桌面外壳可能在登录后才构造本服务——若此时已认证，立即加载。
        _ = EnsureCurrentWorkspacePreferencesAsync();
    }

    private void OnStateChanged(object? sender, AuthSessionStateChangedEventArgs e)
    {
        if (e.State == AuthSessionState.Authenticated)
            _ = EnsureCurrentWorkspacePreferencesAsync();
        else
        {
            lock (_loadGate)
            {
                _currentLoadScope = null;
                _streamCancellation?.Cancel();
            }
            if (e.State != AuthSessionState.Unauthenticated) return;
            if (_presentationActive) { _presentation.DeactivatePolicy(); _presentationActive = false; }
            _settings.Apply(WorkspacePreferencesDto.Default);
            _registry.SetMappings(WorkspacePreferencesDto.Default.DefaultApps);
        }
    }

    /// <summary>
    /// Waits for the authenticated workspace preferences to be applied. The desktop uses this
    /// before opening first-run UI so it is created in the workspace's selected language.
    /// </summary>
    public Task EnsureCurrentWorkspacePreferencesAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
            return Task.CompletedTask;

        var scope = $"{url}\n{ws.Id}\n{tokens.AccessToken}";
        lock (_loadGate)
        {
            if (string.Equals(_currentLoadScope, scope, StringComparison.Ordinal))
                return _currentLoadTask;

            _currentLoadScope = scope;
            _streamCancellation?.Cancel();
            _streamCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _streamCancellation = cancellation;
            if (!_presentationActive) { _presentation.ActivatePolicy(); _presentationActive = true; }
            return _currentLoadTask = InitializeAsync(url, tokens.AccessToken, ws.Id, cancellation);

        }
    }

    private void OnPolicyChanged(object? sender, EventArgs args)
    {
        if (_syncMode == _presentation.Synchronization.Mode) return;
        _syncMode = _presentation.Synchronization.Mode;
        lock (_loadGate) { _currentLoadScope = null; _streamCancellation?.Cancel(); }
        _ = EnsureCurrentWorkspacePreferencesAsync();
    }
    private void OnRecoveryRequested(string origin, Guid workspace)
    {
        if (_session.State != AuthSessionState.Authenticated || _session.CurrentWorkspace?.Id != workspace || _session.EffectiveBaseUrl is not { } url || _session.Tokens is not { } tokens
            || !string.Equals(new Uri(url).GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)) return;
        _ = LoadAsync(url, tokens.AccessToken, workspace, _streamCancellation?.Token ?? CancellationToken.None);
    }
    private async Task InitializeAsync(string url, string token, Guid workspace, CancellationTokenSource cancellation)
    {
        try { await _presentation.EnsureLoadedAsync(); }
        catch { /* A failed presentation read does not erase the desktop's last valid settings. */ }
        if (cancellation.IsCancellationRequested) return;
        await LoadAsync(url, token, workspace, cancellation.Token);
        if (cancellation.IsCancellationRequested || _presentation.Synchronization.Mode == "manual") return;
        _ = ObserveAsync(url, token, workspace, cancellation.Token);
    }
    private async Task ObserveAsync(string url, string token, Guid workspace, CancellationToken cancellation)
    {
        try
        {
            await SettingsChangesStream.RunAsync(url, token, workspace,
                async () =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    await LoadAsync(url, token, workspace, cancellation);
                },
                change =>
                {
                    if (!cancellation.IsCancellationRequested) _changes.Invalidate(new Uri(url).GetLeftPart(UriPartial.Authority), change);
                    return Task.CompletedTask;
                },
                async () =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    await _session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: cancellation);
                    if (!cancellation.IsCancellationRequested && _session.Tokens?.AccessToken != token) await EnsureCurrentWorkspacePreferencesAsync();
                }, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task LoadAsync(string url, string accessToken, Guid workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                var readId = Guid.NewGuid();
                var desktopWriteGeneration = _settings.DesktopWriteGeneration;
                LanguageSwitchDiagnostics.Record("sync.read.begin", new { readId, workspaceId, language = _settings.Language, hasDraft = _editor.HasDraft });
                var prefs = await _client.GetAsync(url, accessToken, workspaceId, cancellationToken);
                _desktopRetrySeconds = 1;
                LanguageSwitchDiagnostics.Record("sync.read.received", new { readId, workspaceId, incoming = prefs.Language, prefs.Revision });
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (cancellationToken.IsCancellationRequested || _session.State != AuthSessionState.Authenticated
                        || _session.EffectiveBaseUrl != url || _session.CurrentWorkspace?.Id != workspaceId
                        || _session.Tokens?.AccessToken != accessToken)
                    {
                        LanguageSwitchDiagnostics.Record("sync.read.skipped_session", new { readId });
                        return;
                    }
                    if (_editor.HasDraft)
                    {
                        LanguageSwitchDiagnostics.Record("sync.read.skipped_draft", new { readId, incoming = prefs.Language, actual = _settings.Language });
                        _editor.ObserveExternalRevision(prefs.Revision);
                        return;
                    }
                    if (!_settings.CanApplySynchronizedPreferences(desktopWriteGeneration, prefs.Revision))
                    {
                        ScheduleDesktopRetry(url, accessToken, workspaceId, cancellationToken);
                        return;
                    }
                    LanguageSwitchDiagnostics.Record("sync.read.apply", new { readId, incoming = prefs.Language, actual = _settings.Language });
                    _registry.SetMappings(prefs.DefaultApps);
                    await _wallpapers.ApplyAsync(prefs, cancellationToken);
                });
            }
            finally { _refreshGate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

        catch
        {
            // A failed read must be retryable; do not overwrite runtime preferences.
            lock (_loadGate)
            {
                if (_currentLoadScope == $"{url}\n{workspaceId}\n{accessToken}")
                    _currentLoadScope = null;
            }
            ScheduleDesktopRetry(url, accessToken, workspaceId, cancellationToken);
        }
    }

    private async void ScheduleDesktopRetry(string url, string token, Guid workspace, CancellationToken cancellation)
    {
        if (_desktopRetryScheduled || cancellation.IsCancellationRequested || _presentation.Synchronization.Mode == "manual") return;
        _desktopRetryScheduled = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_desktopRetrySeconds), cancellation);
            _desktopRetrySeconds = Math.Min(60, _desktopRetrySeconds * 2);
            _desktopRetryScheduled = false;
            if (_session.State == AuthSessionState.Authenticated && _session.EffectiveBaseUrl == url && _session.CurrentWorkspace?.Id == workspace && _session.Tokens?.AccessToken == token)
                await LoadAsync(url, token, workspace, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { _desktopRetryScheduled = false; }
    }

    public void Dispose()
    {
        _presentation.Changed -= OnPolicyChanged;
        _changes.RecoveryRequested -= OnRecoveryRequested;
        if (_presentationActive) _presentation.DeactivatePolicy();
        _session.StateChanged -= OnStateChanged;
        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
    }
}
