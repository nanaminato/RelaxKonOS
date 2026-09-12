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

    public PreferencesSync(
        IAuthSession session,
        IWorkspaceSettingsService client,
        ShellSettings settings,
        DefaultAppRegistry registry,
        WallpaperService wallpapers,
        WorkspacePreferencesEditor editor)
    {
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
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } ws })
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
            _ = SettingsChangesStream.RunAsync(url, tokens.AccessToken, ws.Id,
                async () =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    await _session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: cancellation.Token);
                    if (cancellation.IsCancellationRequested) return;
                    if (_session.Tokens?.AccessToken != tokens.AccessToken)
                    {
                        await EnsureCurrentWorkspacePreferencesAsync();
                        return;
                    }
                    await LoadAsync(url, tokens.AccessToken, ws.Id, cancellation.Token);
                }, cancellation.Token);
            return _currentLoadTask = LoadAsync(url, tokens.AccessToken, ws.Id, cancellation.Token);
        }
    }

    private async Task LoadAsync(string url, string accessToken, Guid workspaceId, CancellationToken cancellationToken)
    {
        try
        {
            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                var prefs = await _client.GetAsync(url, accessToken, workspaceId, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (cancellationToken.IsCancellationRequested || _session.State != AuthSessionState.Authenticated
                        || _session.ServerUrl != url || _session.CurrentWorkspace?.Id != workspaceId
                        || _session.Tokens?.AccessToken != accessToken) return;
                    if (_editor.HasDraft)
                    {
                        _editor.ObserveExternalRevision(prefs.Revision);
                        return;
                    }
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
        }
    }

    public void Dispose()
    {
        _session.StateChanged -= OnStateChanged;
        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
    }
}
