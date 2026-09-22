using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// One-shot Linux dispatcher for normal user file I/O. It validates the canonical NSS identity
/// while privileged, then permanently drops groups/GID/UID before touching user-controlled paths.
/// This process exits after one request and never regains privilege.
/// </summary>
public static class UserExecutionExecutor
{
    private const string LibC = "libc.so.6";

    public static async Task<int> RunOneShotAsync()
    {
        UserExecutionRequest? request;
        try { request = await JsonSerializer.DeserializeAsync<UserExecutionRequest>(Console.OpenStandardInput(), RelaxKonOSJsonOptions.Default); }
        catch (JsonException) { return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "invalid user-execution request")); }
        if (request is null) return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "missing user-execution request"));
        if (!OperatingSystem.IsLinux() || geteuid() != 0) return await WriteAsync(Fail(UserExecutionProblemCode.HelperUnavailable, "root Linux Helper is required"));
        if (request.Version != UserExecutionProtocol.Version || request.OperationId is not { } operationId || operationId == Guid.Empty
            || !Enum.IsDefined(request.Operation)) return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "invalid user-execution request"));
        if (!TryResolve(request.Identity, out var account)) return await WriteAsync(Fail(UserExecutionProblemCode.IdentityMismatch, "OS identity changed or is not executable"));
        try
        {
            // initgroups must happen before setgid/setuid. Any failure fails closed before I/O.
            if (initgroups(account.Name, account.Gid) != 0 || setgid(account.Gid) != 0 || setuid(account.Uid) != 0
                || geteuid() != account.Uid || getegid() != account.Gid)
                return await WriteAsync(Fail(UserExecutionProblemCode.IdentityNotExecutable, "could not assume OS user identity"));
            return await WriteAsync(await ExecuteAsync(request, account.Home));
        }
        catch (UnauthorizedAccessException) { return await WriteAsync(Fail(UserExecutionProblemCode.AccessDenied, "access denied")); }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException) { return await WriteAsync(Fail(UserExecutionProblemCode.NotFound, "path not found")); }
        catch (IOException) { return await WriteAsync(Fail(UserExecutionProblemCode.Conflict, "file operation failed")); }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "invalid file operation")); }
        catch { return await WriteAsync(Fail(UserExecutionProblemCode.InternalError, "user-execution operation failed")); }
    }

    private static async Task<int> WriteAsync(UserExecutionResult result)
    {
        await Console.Out.WriteAsync(JsonSerializer.Serialize(result, RelaxKonOSJsonOptions.Default));
        return result.Success ? 0 : 1;
    }

    private static async Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, string home)
    {
        // Identity has already been permanently dropped.  Paths deliberately retain normal
        // desktop semantics: any absolute host path is allowed here and the kernel evaluates the
        // target user's UID, supplementary groups, ACLs and traversal permissions.  The root
        // dispatcher never opens or resolves a caller-controlled path.
        var path = request.Path is null ? null : ValidatePath(request.Path);
        var destination = request.DestinationPath is null ? null : ValidatePath(request.DestinationPath);
        object result = request.Operation switch
        {
            UserExecutionOperationKind.FileListDirectory => List(path!),
            UserExecutionOperationKind.FileGetSpecialLocations => Special(home),
            UserExecutionOperationKind.FileGetInfo => Info(path),
            UserExecutionOperationKind.FileRead => await ReadAsync(path!),
            UserExecutionOperationKind.FileWrite => await WriteAsync(path!, request.ContentBase64!),
            UserExecutionOperationKind.FileDelete => Delete(path!),
            UserExecutionOperationKind.FileRename => Rename(path!, request.NewName!, home),
            UserExecutionOperationKind.FileMove => Move(path!, destination!, request.Overwrite),
            UserExecutionOperationKind.FileCopy => Copy(path!, destination!, request.Overwrite),
            UserExecutionOperationKind.FileUpload => await UploadAsync(path!, request.FileName!, request.ContentBase64!, home),
            UserExecutionOperationKind.FileCreateDirectory => Create(path!),
            UserExecutionOperationKind.FileGetProperties => Properties(path!),
            UserExecutionOperationKind.FileSetUnixPermissions => SetMode(path!, request.UnixMode),
            UserExecutionOperationKind.GitExecute => await GitAsync(path!, request.GitArguments),
            _ => throw new ArgumentException(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(result, RelaxKonOSJsonOptions.Default);
        if (json.Length > UserExecutionProtocol.MaximumFileContentBytes) return Fail(UserExecutionProblemCode.ContentTooLarge, "user-execution result is too large");
        return new(true, Convert.ToBase64String(json));
    }

    private static DirectoryDto List(string path)
    {
        var d = new DirectoryInfo(path);
        var directories = d.EnumerateDirectories().Select(x => new FileSystemEntryDto(x.FullName, x.Name, null, FileSystemEntryType.Directory,
            x.CreationTimeUtc, x.LastWriteTimeUtc, x.LastAccessTimeUtc, x.Attributes.HasFlag(FileAttributes.Hidden), x.Attributes.HasFlag(FileAttributes.System), "inode/directory")).ToArray();
        var files = d.EnumerateFiles().Select(FileEntry).ToArray();
        return new(d.FullName, d.Name, FileSystemEntryType.Directory, directories, files, d.CreationTimeUtc, d.LastWriteTimeUtc);
    }
    private static IReadOnlyList<SpecialLocationDto> Special(string home) => new[] { (SpecialFolderKind.Home, "主目录", home), (SpecialFolderKind.Desktop, "桌面", Path.Combine(home, "Desktop")),
        (SpecialFolderKind.Documents, "文档", Path.Combine(home, "Documents")), (SpecialFolderKind.Downloads, "下载", Path.Combine(home, "Downloads")),
        (SpecialFolderKind.Pictures, "图片", Path.Combine(home, "Pictures")), (SpecialFolderKind.Music, "音乐", Path.Combine(home, "Music")), (SpecialFolderKind.Videos, "视频", Path.Combine(home, "Videos")) }
        .Where(x => System.IO.Directory.Exists(x.Item3)).Select(x => new SpecialLocationDto(x.Item1, x.Item2, x.Item3)).ToArray();
    private static FileSystemEntryDto? Info(string? path) => path is not null && System.IO.Directory.Exists(path) ? DirectoryEntry(path) : path is not null && System.IO.File.Exists(path) ? ToInfo(FileEntry(new FileInfo(path))) : null;
    private static async Task<FileRead> ReadAsync(string path) { var content = await System.IO.File.ReadAllBytesAsync(path); if (content.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new IOException(); return new(Convert.ToBase64String(content), Path.GetFileName(path), ContentType(path)); }
    private static async Task<FileEntryDto> WriteAsync(string path, string content) { await System.IO.File.WriteAllBytesAsync(path, Decode(content)); return FileEntry(new FileInfo(path)); }
    private static bool Delete(string path) { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); else if (System.IO.File.Exists(path)) System.IO.File.Delete(path); else throw new FileNotFoundException(); return true; }
    private static FileSystemEntryDto Rename(string path, string name, string home) { ValidateName(name); var target = ValidatePath(Path.Combine(Path.GetDirectoryName(path)!, name)); if (System.IO.Directory.Exists(path)) { System.IO.Directory.Move(path, target); return DirectoryEntry(target); } System.IO.File.Move(path, target); return ToInfo(FileEntry(new FileInfo(target))); }
    private static FileSystemEntryDto Move(string source, string target, bool overwrite) { if (System.IO.Directory.Exists(source)) { if (System.IO.Directory.Exists(target) && overwrite) System.IO.Directory.Delete(target, true); System.IO.Directory.Move(source, target); return DirectoryEntry(target); } System.IO.File.Move(source, target, overwrite); return ToInfo(FileEntry(new FileInfo(target))); }
    private static FileSystemEntryDto Copy(string source, string target, bool overwrite) { if (System.IO.Directory.Exists(source)) { CopyDirectory(source, target, overwrite); return DirectoryEntry(target); } System.IO.File.Copy(source, target, overwrite); return ToInfo(FileEntry(new FileInfo(target))); }
    private static async Task<FileEntryDto> UploadAsync(string directory, string name, string content, string home) { ValidateName(name); var path = ValidatePath(Path.Combine(directory, name)); await System.IO.File.WriteAllBytesAsync(path, Decode(content)); return FileEntry(new FileInfo(path)); }
    private static bool Create(string path) { System.IO.Directory.CreateDirectory(path); return true; }
    private static async Task<GitResult> GitAsync(string workingDirectory, IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0 || arguments.Count > 64 || arguments.Any(x => x is null || x.Length > 16_384)) throw new ArgumentException();
        var git = File.Exists("/usr/bin/git") ? "/usr/bin/git" : File.Exists("/usr/local/bin/git") ? "/usr/local/bin/git" : throw new FileNotFoundException();
        using var process = new Process { StartInfo = new ProcessStartInfo(git) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_EDITOR"] = "true";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode == 0, process.ExitCode, await stdout, await stderr);
    }
    private static FilePropertiesDto? Properties(string path)
    {
        var info = Info(path); if (info is null) return null;
        var mode = (int)System.IO.File.GetUnixFileMode(path);
        return new(info.Path, info.Name, info.Type, info.Size, info.Created, info.Modified, info.Accessed,
            Convert.ToString(mode, 8).PadLeft(4, '0'), System.IO.File.GetAttributes(path).ToString(), mode);
    }
    private static FilePropertiesDto SetMode(string path, int? mode) { if (mode is < 0 or > 0xfff or null) throw new ArgumentException(); System.IO.File.SetUnixFileMode(path, (UnixFileMode)mode.Value); return Properties(path)!; }
    private static FileEntryDto FileEntry(FileInfo f) => new(f.FullName, f.Name, f.Extension, f.Length, f.CreationTimeUtc, f.LastWriteTimeUtc, f.LastAccessTimeUtc, f.Attributes.HasFlag(FileAttributes.Hidden), f.Attributes.HasFlag(FileAttributes.System), ContentType(f.FullName));
    private static FileSystemEntryDto ToInfo(FileEntryDto f) => new(f.Path, f.Name, f.Size, FileSystemEntryType.File, f.Created, f.Modified, f.Accessed, f.IsHidden, f.IsSystem, f.MimeType);
    private static FileSystemEntryDto DirectoryEntry(string path) { var d = new DirectoryInfo(path); return new(d.FullName, d.Name, null, FileSystemEntryType.Directory, d.CreationTimeUtc, d.LastWriteTimeUtc, d.LastAccessTimeUtc, d.Attributes.HasFlag(FileAttributes.Hidden), d.Attributes.HasFlag(FileAttributes.System), "inode/directory"); }
    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" or ".json" or ".cs" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
    private static byte[] Decode(string value) { var bytes = Convert.FromBase64String(value); if (bytes.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new ArgumentException(); return bytes; }
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')) throw new ArgumentException(); }
    private static string ValidatePath(string path) => !Path.IsPathFullyQualified(path) ? throw new ArgumentException() : Path.GetFullPath(path);
    private static void CopyDirectory(string source, string target, bool overwrite) { System.IO.Directory.CreateDirectory(target); foreach (var file in System.IO.Directory.EnumerateFiles(source)) System.IO.File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite); foreach (var dir in System.IO.Directory.EnumerateDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), overwrite); }
    private static UserExecutionResult Fail(UserExecutionProblemCode code, string message) => new(false, Error: message, ProblemCode: code);
    private static bool TryResolve(UserExecutionIdentity expected, out Account account)
    {
        account = default; if (expected.Platform != PlatformKind.Linux || !uint.TryParse(expected.StableIdentity, out var uid) || uid < 1000) return false;
        var buffer = Marshal.AllocHGlobal(1_048_576); try { if (getpwuid_r(uid, out var entry, buffer, 1_048_576, out var found) != 0 || found == IntPtr.Zero) return false; var name = Text(entry.Name); var home = Text(entry.Home); if (name != expected.CanonicalAccount || home != expected.HomeDirectory || !Path.IsPathFullyQualified(home!)) return false; account = new(name!, home!, entry.Uid, entry.Gid); return true; } finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string? Text(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    private readonly record struct Account(string Name, string Home, uint Uid, uint Gid);
    private sealed record FileRead(string ContentBase64, string FileName, string ContentType);
    private sealed record GitResult(bool Success, int ExitCode, string Output, string Error);
    [StructLayout(LayoutKind.Sequential)] private struct Passwd { public IntPtr Name, Password; public uint Uid, Gid; public IntPtr Gecos, Home, Shell; }
    [DllImport(LibC)] private static extern uint geteuid(); [DllImport(LibC)] private static extern uint getegid(); [DllImport(LibC, SetLastError = true)] private static extern int initgroups(string user, uint group); [DllImport(LibC, SetLastError = true)] private static extern int setgid(uint gid); [DllImport(LibC, SetLastError = true)] private static extern int setuid(uint uid); [DllImport(LibC)] private static extern int getpwuid_r(uint uid, out Passwd pwd, IntPtr buffer, nuint length, out IntPtr result);
}
