using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RemoteOS.Protocol.Workspace;

namespace Server.Storage;

/// <summary>保存 Workspace 自定义壁纸的私有 blob 存储。
/// 文件不属于宿主机桌面，也不接受客户端提供的路径；访问始终由 Workspace 端点完成归属校验。</summary>
public sealed class WorkspaceWallpaperStore
{
    public const long MaxFileBytes = WorkspaceWallpaperUploadLimits.MaxFileBytes;
    private readonly string _root;

    public WorkspaceWallpaperStore(IHostEnvironment environment, IOptions<StorageOptions> options)
    {
        var configured = options.Value.WallpaperPath;
        _root = Path.GetFullPath(Path.Combine(environment.ContentRootPath,
            string.IsNullOrWhiteSpace(configured) ? "data/wallpapers" : configured));
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredWallpaper> SaveAsync(Guid workspaceId, IFormFile file, CancellationToken ct)
    {
        if (file.Length < WorkspaceWallpaperUploadLimits.MinFileBytes || file.Length > MaxFileBytes)
            throw new InvalidWallpaperException($"The image must be between 1 byte and {WorkspaceWallpaperUploadLimits.MaxFileMegabytes} MB.");

        await using var source = file.OpenReadStream();
        var header = new byte[16];
        var headerCount = await source.ReadAsync(header.AsMemory(), ct);
        var contentType = DetectImageType(header.AsSpan(0, headerCount));
        if (contentType is null)
            throw new InvalidWallpaperException("Only PNG, JPEG, WebP, and GIF images are supported.");

        var id = Guid.NewGuid().ToString("N");
        var directory = WorkspaceDirectory(workspaceId);
        Directory.CreateDirectory(directory);
        var target = BlobPath(workspaceId, id);
        var temporary = target + ".uploading";
        try
        {
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await destination.WriteAsync(header.AsMemory(0, headerCount), ct);
                await source.CopyToAsync(destination, ct);
            }
            File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new StoredWallpaper(id, contentType);
    }

    public (Stream Stream, string ContentType)? OpenRead(Guid workspaceId, string id)
    {
        if (!IsBlobId(id)) return null;
        var path = BlobPath(workspaceId, id);
        if (!File.Exists(path)) return null;
        using var probe = File.OpenRead(path);
        var header = new byte[16];
        var count = probe.Read(header, 0, header.Length);
        var contentType = DetectImageType(header.AsSpan(0, count));
        return contentType is null ? null : (File.OpenRead(path), contentType);
    }

    public Task DeleteAsync(Guid workspaceId, string id)
    {
        if (IsBlobId(id))
        {
            var path = BlobPath(workspaceId, id);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string WorkspaceDirectory(Guid workspaceId) => Path.Combine(_root, workspaceId.ToString("D"));
    private string BlobPath(Guid workspaceId, string id) => Path.Combine(WorkspaceDirectory(workspaceId), id + ".img");
    private static bool IsBlobId(string id) => Guid.TryParseExact(id, "N", out _);

    private static string? DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        return null;
    }
}

public sealed record StoredWallpaper(string Id, string ContentType);
public sealed class InvalidWallpaperException(string message) : Exception(message);
