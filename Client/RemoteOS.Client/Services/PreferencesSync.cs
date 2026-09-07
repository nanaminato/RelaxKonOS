using Client.Services.WorkspaceSettings;
using Client.Apps.Settings;
using Client.Services.Auth;
using RemoteOS.Protocol.Workspace;

namespace Client.Services;

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
    private readonly object _loadGate = new();
    private Task _currentLoadTask = Task.CompletedTask;
    private string? _currentLoadScope;

    public PreferencesSync(
        IAuthSession session,
        IWorkspaceSettingsService client,
        ShellSettings settings,
        DefaultAppRegistry registry,
        WallpaperService wallpapers)
    {
        _session = session;
        _client = client;
        _settings = settings;
        _registry = registry;
        _wallpapers = wallpapers;
        _session.StateChanged += OnStateChanged;
        // 桌面外壳可能在登录后才构造本服务——若此时已认证，立即加载。
        _ = EnsureCurrentWorkspacePreferencesAsync();
    }

    private void OnStateChanged(object? sender, AuthSessionStateChangedEventArgs e)
    {
        if (e.State == AuthSessionState.Authenticated)
            _ = EnsureCurrentWorkspacePreferencesAsync();
        else if (e.State == AuthSessionState.Unauthenticated)
        {
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
            return _currentLoadTask = LoadAsync(url, tokens.AccessToken, ws.Id);
        }
    }

    private async Task LoadAsync(string url, string accessToken, Guid workspaceId)
    {
        try
        {
            var prefs = await _client.GetAsync(url, accessToken, workspaceId);
            if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } currentUrl, CurrentWorkspace: { } currentWorkspace }
                || !string.Equals(currentUrl, url, StringComparison.Ordinal)
                || currentWorkspace.Id != workspaceId)
                return;

            _settings.Apply(prefs);
            await _wallpapers.ApplyAsync(prefs);
            _registry.SetMappings(prefs.DefaultApps);
        }
        catch
        {
            // 服务端无偏好或旧版本：沿用 ShellSettings 默认值。
        }
    }

    public void Dispose() => _session.StateChanged -= OnStateChanged;
}
