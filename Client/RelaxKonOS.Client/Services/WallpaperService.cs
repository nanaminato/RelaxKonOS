using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services;

/// <summary>将 Workspace 中的 <c>custom:{blobId}</c> 壁纸键解析为本机可渲染图片。
/// 图片本身由服务端保存并按需下载；ShellSettings 只持有当前会话的渲染缓存。</summary>
public sealed class WallpaperService(IAuthSession session, IWallpaperClient client, ShellSettings settings)
{
    public async Task UploadAndApplyAsync(Stream image, string fileName, CancellationToken ct = default)
    {
        if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            throw new InvalidOperationException("Sign in before setting a synchronized wallpaper.");
        var preferences = await client.UploadAsync(url, tokens.AccessToken, workspace.Id, image, fileName, ct);
        if (!IsCurrent(url, tokens.AccessToken, workspace.Id) || ct.IsCancellationRequested) return;
        await ApplyAsync(preferences, ct);
    }

    public async Task ApplyAsync(WorkspacePreferencesDto preferences, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        settings.Apply(preferences);
        if (!TryGetBlobId(preferences.WallpaperKey, out var blobId)) return;
        if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;
        try
        {
            var bytes = await client.DownloadAsync(url, tokens.AccessToken, workspace.Id, blobId, ct);
            if (ct.IsCancellationRequested || !IsCurrent(url, tokens.AccessToken, workspace.Id)
                || settings.CurrentWallpaperKey != preferences.WallpaperKey) return;
            using var stream = new MemoryStream(bytes, writable: false);
            settings.SetCustomWallpaper(preferences.WallpaperKey, new Bitmap(stream));
        }
        catch
        {
            // Keep the built-in fallback selected by ShellSettings. The next sync / login retries.
        }
    }

    private bool IsCurrent(string url, string token, Guid workspaceId) =>
        session.State == AuthSessionState.Authenticated && session.ServerUrl == url
        && session.Tokens?.AccessToken == token && session.CurrentWorkspace?.Id == workspaceId;

    private static bool TryGetBlobId(string? key, out string blobId)
    {
        blobId = string.Empty;
        if (string.IsNullOrWhiteSpace(key)
            || !key.StartsWith(WorkspacePreferencesDto.CustomWallpaperPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var value = key[WorkspacePreferencesDto.CustomWallpaperPrefix.Length..];
        if (!Guid.TryParseExact(value, "N", out _)) return false;
        blobId = value;
        return true;
    }
}
