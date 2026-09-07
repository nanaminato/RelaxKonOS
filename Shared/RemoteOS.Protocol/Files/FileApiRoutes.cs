using RemoteOS.Protocol.Common;

namespace RemoteOS.Protocol.Files;

/// <summary>文件管理 REST 端点路由常量。路径已含 /api/v1 前缀。Server 注册路由与 Client 拼接 URL 共用。
/// 所有端点需 JWT（[Authorize]），错误统一返回 RFC 7807 ProblemDetails。</summary>
public static class FileApiRoutes
{
    public const string Operations = $"/{RemoteOsEndpoints.ApiVersionPrefix}/files/operations";

    private const string V1 = RemoteOsEndpoints.ApiVersionPrefix;

    /// <summary>列举驱动器/根挂载点（GET，需 JWT）。</summary>
    public const string Drives = $"/{V1}/files/drives";

    /// <summary>列举特殊文件夹位置（GET，需 JWT）。返回家目录/桌面/文档/下载/图片/音乐/视频中已存在的项。
    /// 跨平台枚举由服务端 IFileService.GetSpecialLocations 完成（Environment.GetFolderPath + Directory.Exists 过滤）。</summary>
    public const string Special = $"/{V1}/files/special";

    /// <summary>列举目录内容（GET，需 JWT）。query: path（空=盘符根）。</summary>
    public const string List = $"/{V1}/files/list";

    /// <summary>获取单个条目元数据（GET，需 JWT）。query: path。</summary>
    public const string Info = $"/{V1}/files/info";

    /// <summary>下载文件（GET，需 JWT）。query: path。返回字节流。</summary>
    public const string Download = $"/{V1}/files/download";

    /// <summary>读取或覆盖保存单个文件内容（GET/PUT，需 JWT）。Query: path。</summary>
    public const string Content = $"/{V1}/files/content";

    /// <summary>Tests direct file access and, when needed, establishes a short-lived elevated file session.</summary>
    public const string Elevation = $"/{V1}/files/elevation";

    /// <summary>获取文件或目录的属性与宿主 OS 权限摘要（GET，需 JWT）。Query: path。</summary>
    public const string Properties = $"/{V1}/files/properties";

    /// <summary>更新 Linux POSIX 权限（PUT，需 JWT）。Body: UpdateUnixPermissionsRequest。</summary>
    public const string Permissions = $"/{V1}/files/permissions";

    /// <summary>创建目录（POST，需 JWT）。query: path。</summary>
    public const string Directory = $"/{V1}/files/directory";

    /// <summary>删除文件或目录（DELETE，需 JWT）。query: path。目录递归删除。</summary>
    public const string Delete = $"/{V1}/files";

    /// <summary>重命名（POST，需 JWT）。body: RenameRequest。同目录改名。</summary>
    public const string Rename = $"/{V1}/files/rename";

    /// <summary>移动（POST，需 JWT）。body: MoveRequest。可跨目录。</summary>
    public const string Move = $"/{V1}/files/move";

    /// <summary>复制（POST，需 JWT）。body: CopyRequest。</summary>
    public const string Copy = $"/{V1}/files/copy";

    /// <summary>上传文件（POST，需 JWT）。query: path（目标目录）。body: multipart/form-data。</summary>
    public const string Upload = $"/{V1}/files/upload";
}
