using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer;

/// <summary>RelaxKonOS Server 文件管理 HTTP 客户端抽象。typed HttpClient 实现（见 <see cref="ExplorerClient"/>）。
/// 所有方法从 <c>IAuthSession</c> 取 <c>serverUrl</c> + <c>accessToken</c> 构造绝对 URI 与 Authorization 头。
/// 路由常量见 <see cref="FileApiRoutes"/>。错误统一为 <see cref="RemoteOsAuthException"/>（含 ProblemDetails）。</summary>
public interface IExplorerClient
{
    Task<FileOperationDto> StartOperationAsync(StartFileOperationRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FileOperationDto>> ListOperationsAsync(CancellationToken ct = default);
    Task<FileOperationDto> GetOperationAsync(Guid id, CancellationToken ct = default);
    Task<FileOperationDto> CancelOperationAsync(Guid id, CancellationToken ct = default);
    Task<FileOperationDto> DecideOperationAsync(Guid id, FileOperationDecisionRequest request, CancellationToken ct = default);

    /// <summary>列举驱动器/根挂载点（GET /files/drives）。</summary>
    Task<IReadOnlyList<DriveDto>> GetDrivesAsync(CancellationToken ct = default);

    /// <summary>列举特殊文件夹位置（GET /files/special）。家目录/桌面/文档/下载/图片/音乐/视频中已存在的项。
    /// 服务端已 <c>Directory.Exists</c> 过滤，缺失项不返回。</summary>
    Task<IReadOnlyList<SpecialLocationDto>> GetSpecialLocationsAsync(CancellationToken ct = default);

    /// <summary>列举目录内容（GET /files/list）。path 为空表示盘符根。</summary>
    Task<DirectoryDto> GetDirectoryAsync(string? path, CancellationToken ct = default);

    /// <summary>获取单个条目元数据（GET /files/info）。</summary>
    Task<FileSystemEntryDto?> GetInfoAsync(string path, CancellationToken ct = default);

    /// <summary>下载文件（GET /files/download）。返回字节流；调用方负责释放。</summary>
    Task<(Stream Stream, string FileName)?> DownloadAsync(string path, CancellationToken ct = default);

    /// <summary>读取远程文件的原始字节；文件不存在时返回 null。</summary>
    Task<byte[]?> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>Tests direct access and, after host password confirmation, authorizes short-lived elevated access to one path.</summary>
    Task<FileElevationResult> ElevateFileAccessAsync(string path, FileElevationCapability capability, string? password = null, CancellationToken ct = default);

    /// <summary>Authorizes the containing protected directories for a failed mutating operation.</summary>
    Task<FileElevationResult> ElevateFileOperationAsync(IReadOnlyList<string> directoryPaths, FileElevationCapability capability, string? password = null, CancellationToken ct = default);

    /// <summary>以原始字节覆盖保存远程文件。</summary>
    Task<FileEntryDto> WriteFileAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>获取远程文件或目录的详细属性与权限摘要；不存在时返回 null。</summary>
    Task<FilePropertiesDto?> GetPropertiesAsync(string path, CancellationToken ct = default);

    /// <summary>更新远程 Linux 文件或目录的 POSIX 权限位。</summary>
    Task<FilePropertiesDto> SetUnixPermissionsAsync(string path, int unixMode, CancellationToken ct = default);

    /// <summary>创建目录（POST /files/directory）。</summary>
    Task<FileSystemEntryDto> CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>删除文件或目录（DELETE /files）。</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>同目录重命名（POST /files/rename）。</summary>
    Task<FileSystemEntryDto> RenameAsync(string sourcePath, string newName, CancellationToken ct = default);

    /// <summary>移动（POST /files/move）。</summary>
    Task<FileSystemEntryDto> MoveAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default);

    /// <summary>复制（POST /files/copy）。</summary>
    Task<FileSystemEntryDto> CopyAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default);

    /// <summary>上传文件（POST /files/upload）。</summary>
    Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content,
        IProgress<long>? progress = null, CancellationToken ct = default);
}
