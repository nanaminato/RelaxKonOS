using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Settings;

/// <summary>访问 Workspace 托管图片壁纸的 HTTP 客户端。</summary>
public interface IWallpaperClient
{
    /// <summary>上传图片并原子地设为当前 Workspace 壁纸。</summary>
    Task<WorkspacePreferencesDto> UploadAsync(string serverUrl, string accessToken, Guid workspaceId,
        Stream image, string fileName, CancellationToken ct = default);

    /// <summary>下载一个已选中的 Workspace 壁纸资源。</summary>
    Task<byte[]> DownloadAsync(string serverUrl, string accessToken, Guid workspaceId, string blobId,
        CancellationToken ct = default);
}
