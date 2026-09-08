using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>文件管理服务：以宿主 OS 进程身份执行 IO（复用宿主用户/权限，不另建 ACL——见 project_memory 硬约束）。
/// 实现方负责平台感知（Windows 盘符 / Linux "/" 根）与 <see cref="UnauthorizedAccessException"/> 吞并。
/// 移植自 Jaya FileSystemService 的目录列举逻辑并扩展为完整文件操作。</summary>
public interface IFileService
{
    /// <summary>列举驱动器/根挂载点。Windows 返回 C:\ 等盘符；Linux 返回单条 "/"。</summary>
    IReadOnlyList<DriveDto> GetDrives();

    /// <summary>列举特殊文件夹位置（家目录/桌面/文档/下载/图片/音乐/视频）。
    /// <paramref name="userHomeDirectory"/> 应由鉴权层传入当前登录用户的主目录；未提供时回退宿主进程的主目录。
    /// Linux 从 <c>~/.config/user-dirs.dirs</c> 读取 XDG 用户目录，因此可识别 <c>~/桌面</c> 等本地化名称。
    /// 仅返回 <see cref="System.IO.Directory.Exists"/> 的项，缺失项已被过滤。</summary>
    IReadOnlyList<SpecialLocationDto> GetSpecialLocations(string? userHomeDirectory = null);

    /// <summary>列举目录内容（目录自身元数据 + 子目录列表 + 文件列表）。
    /// path 为空/未提供时返回盘符根的聚合视图（Windows 顶层为各盘符目录项，Linux 为 "/" 根目录列举）。</summary>
    DirectoryDto GetDirectory(string? path);

    /// <summary>获取单个条目元数据。不存在返回 null。</summary>
    FileSystemEntryDto? GetInfo(string path);

    /// <summary>打开文件用于下载。返回 (stream, contentType, fileName)。不存在返回 null。</summary>
    (Stream Stream, string ContentType, string FileName)? OpenRead(string path);

    /// <summary>以提供的字节覆盖保存文件，并返回保存后的文件元数据。</summary>
    Task<FileEntryDto> WriteFileAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>获取属性和宿主 OS 权限摘要。不存在时返回 null。</summary>
    FilePropertiesDto? GetProperties(string path);

    /// <summary>更新 Linux POSIX 权限位；非 Linux 主机不支持此操作。</summary>
    FilePropertiesDto SetUnixPermissions(string path, int unixMode);

    /// <summary>创建目录。已存在时抛 <see cref="IOException"/>（端点映射 409 already-exists）。</summary>
    void CreateDirectory(string path);

    /// <summary>删除文件或目录（目录递归）。不存在抛 <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/>。</summary>
    void Delete(string path);

    /// <summary>同目录内重命名。源不存在抛 <see cref="FileNotFoundException"/>。</summary>
    FileSystemEntryDto Rename(string sourcePath, string newName);

    /// <summary>移动（可跨目录）。目标存在且 overwrite=false 时抛 <see cref="IOException"/>（端点映射 409）。</summary>
    FileSystemEntryDto Move(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>复制（可跨目录）。目标存在且 overwrite=false 时抛 <see cref="IOException"/>（端点映射 409）。</summary>
    FileSystemEntryDto Copy(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>上传文件到目标目录。返回新建文件条目。</summary>
    Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default);
}
