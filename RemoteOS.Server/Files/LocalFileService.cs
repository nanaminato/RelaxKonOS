// 目录枚举逻辑移植自 Jaya FileSystemService.GetDirectoryAsync（BSD-3）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 Apps/Explorer/LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.AspNetCore.StaticFiles;
using RemoteOS.Protocol.Files;

namespace Server.Files;

/// <summary>宿主 OS 本地文件系统服务。移植自 Jaya <c>FileSystemService.GetDirectoryAsync</c> 的枚举逻辑，
/// 扩展 create/delete/rename/move/copy/upload/download 操作。以宿主 OS 进程身份运行，复用宿主用户/权限。
/// 平台感知：Windows 列盘符；Linux 返回单条 "/" 根。<see cref="UnauthorizedAccessException"/> 在列举时吞并（部分目录不可访问不应导致整列失败）。</summary>
public sealed class LocalFileService : IFileService
{
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    /// <summary>超出该大小的未知扩展名文件不在列目录时做内容嗅探，避免遍历大目录 IO 过重。</summary>
    private const int ContentSniffMaxBytes = 4 * 1024 * 1024;
    private const string TextPlain = "text/plain";
    private const string OctetStream = "application/octet-stream";
    private const string InodeDirectory = "inode/directory";

    public IReadOnlyList<DriveDto> GetDrives()
    {
        // DriveInfo.GetDrives() 在 Linux 上会枚举每一个挂载点，其中包含 /dev、
        // /dev/pts、/dev/shm 等嵌套的伪文件系统。把它们全部作为“此电脑”的
        // 直接子项会丢失目录层级，也会让挂载点看起来像并列磁盘。Linux 的
        // 文件系统只有一个可浏览的根，因此仅将 / 作为导航树根；其余挂载点
        // 会在按目录展开 /dev、/sys 等时显示在各自正确的父目录下。
        if (IsLinux)
        {
            var root = new DriveInfo(Path.DirectorySeparatorChar.ToString());
            return [new DriveDto(
                Name: root.Name,
                Path: root.RootDirectory.FullName,
                TotalSize: root.IsReady ? root.TotalSize : null,
                IsReady: root.IsReady)];
        }

        var list = new List<DriveDto>();
        foreach (var d in DriveInfo.GetDrives())
        {
            long? total = d.IsReady ? d.TotalSize : null;
            list.Add(new DriveDto(
                Name: d.Name,
                Path: d.RootDirectory.FullName,
                TotalSize: total,
                IsReady: d.IsReady));
        }
        return list;
    }

    public IReadOnlyList<SpecialLocationDto> GetSpecialLocations(string? userHomeDirectory = null)
    {
        // The request handler resolves the signed-in user's home directory. Falling back keeps
        // the service usable for callers that do not have an authenticated user context.
        var home = userHomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        if (string.IsNullOrEmpty(home))
            return Array.Empty<SpecialLocationDto>();

        // Linux users may localize these folders (for example ~/桌面 rather than ~/Desktop).
        // xdg-user-dirs records the user's exact choices in ~/.config/user-dirs.dirs.
        var xdgDirectories = IsLinux ? ReadXdgUserDirectories(home) : new Dictionary<string, string>();
        string userDirectory(string xdgName, string fallback) =>
            xdgDirectories.TryGetValue(xdgName, out var configured) ? configured : Path.Combine(home, fallback);

        // 候选列表：(协议枚举, 显示名, 路径)。
        var candidates = new[]
        {
            (SpecialFolderKind.Home,      "主目录", home),
            (SpecialFolderKind.Desktop,   "桌面",   userDirectory("XDG_DESKTOP_DIR", "Desktop")),
            (SpecialFolderKind.Documents, "文档",   userDirectory("XDG_DOCUMENTS_DIR", "Documents")),
            (SpecialFolderKind.Downloads, "下载",   userDirectory("XDG_DOWNLOAD_DIR", "Downloads")),
            (SpecialFolderKind.Pictures,  "图片",   userDirectory("XDG_PICTURES_DIR", "Pictures")),
            (SpecialFolderKind.Music,      "音乐",   userDirectory("XDG_MUSIC_DIR", "Music")),
            (SpecialFolderKind.Videos,     "视频",   userDirectory("XDG_VIDEOS_DIR", "Videos")),
        };

        var list = new List<SpecialLocationDto>();
        foreach (var (kind, name, path) in candidates)
        {
            // 仅返回真实存在的目录；headless Linux 上 Downloads/Pictures 等可能缺失，过滤后不返回。
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add(new SpecialLocationDto(kind, name, path));
        }
        return list;
    }

    private static IReadOnlyDictionary<string, string> ReadXdgUserDirectories(string home)
    {
        var configPath = Path.Combine(home, ".config", "user-dirs.dirs");
        if (!File.Exists(configPath)) return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var key = line[..separator].Trim();
                if (!key.StartsWith("XDG_", StringComparison.Ordinal) || !key.EndsWith("_DIR", StringComparison.Ordinal))
                    continue;

                var rawValue = line[(separator + 1)..].Trim();
                if (rawValue.Length < 2 || rawValue[0] != '"' || rawValue[^1] != '"')
                    continue;

                var value = rawValue[1..^1];
                string? path = value switch
                {
                    "$HOME" => home,
                    _ when value.StartsWith("$HOME/", StringComparison.Ordinal) => Path.Combine(home, value[6..]),
                    _ when Path.IsPathRooted(value) => value,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(path)) result[key] = path;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return result;
    }

    public DirectoryDto GetDirectory(string? path)
    {
        // path 为空：返回盘符根聚合视图
        if (string.IsNullOrWhiteSpace(path))
        {
            if (IsLinux)
            {
                // Linux：把 "/" 根当作一个目录列举返回
                return ListDirectory("/");
            }

            // Windows：顶层 directories = 各盘符（与 Jaya 一致）
            var dirs = new List<FileSystemEntryDto>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                dirs.Add(new FileSystemEntryDto(
                    Path: d.RootDirectory.FullName,
                    Name: d.Name,
                    Size: d.TotalSize,
                    Type: FileSystemEntryType.Drive,
                    Created: null,
                    Modified: null,
                    Accessed: null,
                    IsHidden: false,
                    IsSystem: false,
                    MimeType: null));
            }
            return new DirectoryDto(
                Path: string.Empty,
                Name: "Computer",
                Type: FileSystemEntryType.Directory,
                Directories: dirs,
                Files: Array.Empty<FileEntryDto>(),
                Created: null,
                Modified: null);
        }

        // Empty optical drives are still reported by Windows as drive roots, but opening one
        // raises an IOException ("device not ready") rather than reporting a missing directory.
        // Treat it as an empty, unavailable location so browsing it never turns into a server error.
        if (IsUnavailableDrive(path))
            return CreateUnavailableDirectory(path);

        try
        {
            return ListDirectory(path);
        }
        catch (IOException) when (IsUnavailableDrive(path))
        {
            // Media can be ejected after the readiness check and before enumeration begins.
            return CreateUnavailableDirectory(path);
        }
    }

    private static bool IsUnavailableDrive(string path)
    {
        if (IsLinux) return false;

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) && !new DriveInfo(root).IsReady;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static DirectoryDto CreateUnavailableDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new DirectoryDto(
            Path: fullPath,
            Name: Path.TrimEndingDirectorySeparator(fullPath),
            Type: FileSystemEntryType.Directory,
            Directories: Array.Empty<FileSystemEntryDto>(),
            Files: Array.Empty<FileEntryDto>(),
            Created: null,
            Modified: null);
    }

    private static DirectoryDto ListDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        var dirs = new List<FileSystemEntryDto>();
        var files = new List<FileEntryDto>();

        // 子目录
        try
        {
            foreach (var di in info.EnumerateDirectories())
            {
                dirs.Add(new FileSystemEntryDto(
                    Path: di.FullName,
                    Name: di.Name,
                    Size: null,
                    Type: FileSystemEntryType.Directory,
                    Created: di.CreationTimeUtc,
                    Modified: di.LastWriteTimeUtc,
                    Accessed: di.LastAccessTimeUtc,
                    IsHidden: di.Attributes.HasFlag(FileAttributes.Hidden),
                    IsSystem: di.Attributes.HasFlag(FileAttributes.System),
                    MimeType: InodeDirectory));
            }
        }
        catch (UnauthorizedAccessException) { /* 部分子目录不可访问：跳过 */ }
        catch (DirectoryNotFoundException) { throw; }

        // 文件
        try
        {
            foreach (var fi in info.EnumerateFiles())
                files.Add(ToFileEntry(fi));
        }
        catch (UnauthorizedAccessException) { }

        return new DirectoryDto(
            Path: info.FullName,
            Name: info.Name,
            Type: FileSystemEntryType.Directory,
            Directories: dirs,
            Files: files,
            Created: info.CreationTimeUtc,
            Modified: info.LastWriteTimeUtc);
    }

    public FileSystemEntryDto? GetInfo(string path)
    {
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            return new FileSystemEntryDto(
                Path: di.FullName,
                Name: di.Name,
                Size: null,
                Type: FileSystemEntryType.Directory,
                Created: di.CreationTimeUtc,
                Modified: di.LastWriteTimeUtc,
                Accessed: di.LastAccessTimeUtc,
                IsHidden: di.Attributes.HasFlag(FileAttributes.Hidden),
                IsSystem: di.Attributes.HasFlag(FileAttributes.System),
                MimeType: InodeDirectory);
        }
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            var fe = ToFileEntry(fi);
            return new FileSystemEntryDto(
                Path: fe.Path,
                Name: fe.Name,
                Size: fe.Size,
                Type: FileSystemEntryType.File,
                Created: fe.Created,
                Modified: fe.Modified,
                Accessed: fe.Accessed,
                IsHidden: fe.IsHidden,
                IsSystem: fe.IsSystem,
                MimeType: fe.MimeType);
        }
        return null;
    }

    public (Stream Stream, string ContentType, string FileName)? OpenRead(string path)
    {
        if (!File.Exists(path)) return null;
        var fi = new FileInfo(path);
        return (fi.OpenRead(), GuessMimeType(fi), fi.Name);
    }

    public async Task<FileEntryDto> WriteFileAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Target directory does not exist: {directory}");

        return await WriteAtomicallyAsync(path, content, cancellationToken);
    }

    public FilePropertiesDto? GetProperties(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return new FilePropertiesDto(directory.FullName, directory.Name, FileSystemEntryType.Directory,
                null, directory.CreationTimeUtc, directory.LastWriteTimeUtc, directory.LastAccessTimeUtc,
                GetPermissions(path, directory.Attributes), directory.Attributes.ToString(), GetUnixMode(path));
        }

        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new FilePropertiesDto(file.FullName, file.Name, FileSystemEntryType.File,
                file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, file.LastAccessTimeUtc,
                GetPermissions(path, file.Attributes), file.Attributes.ToString(), GetUnixMode(path));
        }

        return null;
    }

    public FilePropertiesDto SetUnixPermissions(string path, int unixMode)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Changing POSIX permissions is supported only on Linux hosts.");
        if (unixMode is < 0 or > 0xFFF)
            throw new ArgumentOutOfRangeException(nameof(unixMode), "Unix mode must be between 0000 and 7777.");
        if (!Exists(path))
            throw new FileNotFoundException("Path does not exist.", path);

        File.SetUnixFileMode(path, (UnixFileMode)unixMode);
        return GetProperties(path)!;
    }

    public void CreateDirectory(string path)
    {
        // Directory.CreateDirectory 对已存在目录是 no-op；为产生 409 我们先检查
        if (Directory.Exists(path))
            throw new IOException($"目录已存在: {path}");
        Directory.CreateDirectory(path);
    }

    public void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
        else
            throw new FileNotFoundException($"路径不存在: {path}", path);
    }

    public FileSystemEntryDto Rename(string sourcePath, string newName)
    {
        var info = GetInfo(sourcePath) ?? throw new FileNotFoundException($"源路径不存在: {sourcePath}", sourcePath);
        var parent = System.IO.Path.GetDirectoryName(sourcePath);
        var dest = string.IsNullOrEmpty(parent)
            ? newName
            : System.IO.Path.Combine(parent, newName);

        if (Directory.Exists(sourcePath))
        {
            var di = new DirectoryInfo(sourcePath);
            di.MoveTo(dest);
        }
        else
        {
            File.Move(sourcePath, dest);
        }
        return GetInfo(dest)!;
    }

    public FileSystemEntryDto Move(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!Exists(sourcePath))
            throw new FileNotFoundException($"源路径不存在: {sourcePath}", sourcePath);
        if (Exists(destinationPath) && !overwrite)
            throw new IOException($"目标已存在: {destinationPath}");

        if (Directory.Exists(sourcePath))
        {
            if (Exists(destinationPath) && overwrite) Directory.Delete(destinationPath, recursive: true);
            Directory.Move(sourcePath, destinationPath);
        }
        else
        {
            File.Move(sourcePath, destinationPath, overwrite);
        }
        return GetInfo(destinationPath)!;
    }

    public FileSystemEntryDto Copy(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!Exists(sourcePath))
            throw new FileNotFoundException($"源路径不存在: {sourcePath}", sourcePath);
        if (Exists(destinationPath) && !overwrite)
            throw new IOException($"目标已存在: {destinationPath}");

        if (Directory.Exists(sourcePath))
        {
            // DirectoryInfo.CreateSubdirectory 不支持覆盖；CopyTo 同样不支持 overwrite 参数语义，这里手动实现
            if (Exists(destinationPath) && overwrite) Directory.Delete(destinationPath, recursive: true);
            CopyDirectory(sourcePath, destinationPath);
        }
        else
        {
            File.Copy(sourcePath, destinationPath, overwrite);
        }
        return GetInfo(destinationPath)!;
    }

    public async Task<FileEntryDto> UploadAsync(string targetDirectoryPath, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(targetDirectoryPath))
            throw new DirectoryNotFoundException($"目标目录不存在: {targetDirectoryPath}");
        var dest = System.IO.Path.Combine(targetDirectoryPath, fileName);
        return await WriteAtomicallyAsync(dest, content, cancellationToken);
    }

    /// <summary>
    /// Writes to a sibling temporary file before replacing the destination, preserving any
    /// existing document when the request is cancelled or its body cannot be read.
    /// </summary>
    private static async Task<FileEntryDto> WriteAtomicallyAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new DirectoryNotFoundException($"Target directory does not exist: {path}");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return ToFileEntry(new FileInfo(path));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { /* Preserve the original write exception if cleanup also fails. */ }
            }
        }
    }

    private static FileEntryDto ToFileEntry(FileInfo file)
    {
        var ext = string.IsNullOrEmpty(file.Extension) ? null : file.Extension[1..].ToLowerInvariant();
        var mime = GuessMimeType(file);
        return new FileEntryDto(file.FullName, file.Name, ext, file.Length, file.CreationTimeUtc,
            file.LastWriteTimeUtc, file.LastAccessTimeUtc, file.Attributes.HasFlag(FileAttributes.Hidden),
            file.Attributes.HasFlag(FileAttributes.System), mime);
    }

    /// <summary>
    /// 推断文件 MIME：
    /// 1. 用 <see cref="ContentTypeProvider"/> 按扩展名匹配，命中且非 octet-stream 直接使用；
    /// 2. 否则文件小于 <see cref="ContentSniffMaxBytes"/> 时读前 4KB 嗅探是否纯文本：
    ///    是文本 → <c>text/plain</c>；否则 → <c>application/octet-stream</c>。
    /// 对列目录性能敏感，超大未知文件仅返回 octet-stream，不触发磁盘 IO。
    /// </summary>
    private static string GuessMimeType(FileInfo file)
    {
        if (ContentTypes.TryGetContentType(file.Name, out var mapped)
            && !string.Equals(mapped, OctetStream, StringComparison.Ordinal))
            return mapped;

        if (file.Length > 0 && file.Length <= ContentSniffMaxBytes)
        {
            try
            {
                Span<byte> head = stackalloc byte[Math.Min((int)file.Length, 4096)];
                using var fs = file.OpenRead();
                var read = fs.Read(head);
                if (read > 0 && SniffIsTextContent(head[..read]))
                    return TextPlain;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return OctetStream;
    }

    /// <summary>与客户端 TextFileSniffer 相同的判定规则，避免两端分叉。</summary>
    private static bool SniffIsTextContent(Span<byte> span)
    {
        var length = span.Length;
        if (length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) return true;
        if (length >= 2 && span[0] == 0xFF && span[1] == 0xFE) return true;
        if (length >= 2 && span[0] == 0xFE && span[1] == 0xFF) return true;
        if (length >= 4 && span[0] == 0x00 && span[1] == 0x00 && span[2] == 0xFE && span[3] == 0xFF) return true;
        if (length >= 4 && span[0] == 0xFF && span[1] == 0xFE && span[2] == 0x00 && span[3] == 0x00) return true;

        var printable = 0;
        for (var i = 0; i < length; i++)
        {
            var b = span[i];
            if (b == 0x00) return false;
            // ASCII 可打印 + 常见空白控制字符
            if ((b >= 0x20 && b <= 0x7E) || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\f' || b == 0x0B) printable++;
            // UTF-8 多字节序列：起始字节 (>=0xC2) + 续字节 (0x80–0xBF) 都宽松计为"可能文本"
            //   2 字节字符：起始 0xC0–0xDF（含 0xC2 起的合法范围）+ 1 个续字节
            //   3 字节字符：起始 0xE0–0xEF + 2 个续字节（中文等）
            //   4 字节字符：起始 0xF0–0xF7 + 3 个续字节（Emoji 等）
            // 这样每个中文 3 字节都被完整计入，避免 2/3 比例低于阈值被误判为二进制。
            else if (b >= 0x80) printable++;
        }
        return printable >= (length * 7 / 10);
    }

    private static string GetPermissions(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                FileSystemSecurity security = Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                return "Windows ACL (SDDL): " + security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            }
            catch
            {
                return attributes.HasFlag(FileAttributes.ReadOnly) ? "Read-only" : "Read/write";
            }
        }

        return File.GetUnixFileMode(path).ToString();
    }

    private static int? GetUnixMode(string path)
        => OperatingSystem.IsLinux() ? (int)File.GetUnixFileMode(path) : null;

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void CopyDirectory(string source, string destination)
    {
        var src = new DirectoryInfo(source);
        Directory.CreateDirectory(destination);
        foreach (var fi in src.EnumerateFiles())
            fi.CopyTo(System.IO.Path.Combine(destination, fi.Name), overwrite: false);
        foreach (var di in src.EnumerateDirectories())
            CopyDirectory(di.FullName, System.IO.Path.Combine(destination, di.Name));
    }
}
