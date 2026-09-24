using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// One-shot Linux dispatcher for normal user file I/O. It validates the canonical NSS identity
/// while privileged, then permanently drops groups/GID/UID before touching user-controlled paths.
/// This process exits after one request and never regains privilege.
/// </summary>
public static class UserExecutionExecutor
{
    private const string LibC = "libc.so.6";
    private const int PrSetNoNewPrivileges = 38;

    public static async Task<int> RunOneShotAsync()
    {
        UserExecutionRequest? request;
        try
        {
            var json = await ReadBoundedAsync(Console.OpenStandardInput(), lineTerminated: false);
            request = JsonSerializer.Deserialize<UserExecutionRequest>(json, RelaxKonOSJsonOptions.Default);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        { return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "invalid user-execution request")); }
        if (request is null) return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "missing user-execution request"));
        if (!OperatingSystem.IsLinux() || geteuid() != 0) return await WriteAsync(Fail(UserExecutionProblemCode.HelperUnavailable, "root Linux Helper is required"));
        if (!IsValidRequest(request, terminal: false)) return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "invalid user-execution request"));
        if (!TryResolve(request.Identity, out var account)) return await WriteAsync(Fail(UserExecutionProblemCode.IdentityMismatch, "OS identity changed or is not executable"));
        try
        {
            if (!TryAssumeIdentity(account))
                return await WriteAsync(Fail(UserExecutionProblemCode.IdentityNotExecutable, "could not assume OS user identity"));
            return await WriteAsync(await ExecuteAsync(request, account.Home));
        }
        catch (ContentTooLargeException) { return await WriteAsync(Fail(UserExecutionProblemCode.ContentTooLarge, "user-execution content is too large")); }
        catch (UnauthorizedAccessException) { return await WriteAsync(Fail(UserExecutionProblemCode.AccessDenied, "access denied")); }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException) { return await WriteAsync(Fail(UserExecutionProblemCode.NotFound, "path not found")); }
        catch (IOException) { return await WriteAsync(Fail(UserExecutionProblemCode.Conflict, "file operation failed")); }
        catch (Exception exception) when (exception is ArgumentException or FormatException) { return await WriteAsync(Fail(UserExecutionProblemCode.InvalidRequest, "invalid file operation")); }
        catch { return await WriteAsync(Fail(UserExecutionProblemCode.InternalError, "user-execution operation failed")); }
    }

    public static async Task<int> RunTerminalAsync()
    {
        var input = Console.OpenStandardInput();
        UserExecutionRequest? request;
        try
        {
            var json = await ReadBoundedAsync(input, lineTerminated: true);
            request = JsonSerializer.Deserialize<UserExecutionRequest>(json, RelaxKonOSJsonOptions.Default);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { return 64; }
        if (!OperatingSystem.IsLinux() || geteuid() != 0) return 77;
        if (request is null || !IsValidRequest(request, terminal: true) || !TryShell(request.TerminalShell, out var shell)) return 64;
        if (!TryResolve(request.Identity, out var account)) return 77;
        var directory = ValidatePath(request.Path!);
        var columns = request.TerminalColumns ?? 80;
        var rows = request.TerminalRows ?? 24;
        var widthPixels = request.TerminalWidthPixels ?? 0;
        var heightPixels = request.TerminalHeightPixels ?? 0;
        try { UserTerminalStreamProtocol.ValidateDimensions(columns, rows, widthPixels, heightPixels); }
        catch (ArgumentOutOfRangeException) { return 64; }
        if (!TryAssumeIdentity(account)) return 77;

        // The package creates the PTY only after the irreversible UID/GID transition. The control
        // stream carries framed input and resize messages; it never accepts an executable or
        // environment supplied by the caller.
        var pty = new DefaultPtyFactory().Create();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Console.OpenStandardOutput();
        var outputLock = new object();
        pty.DataReceived += (buffer, count) =>
        {
            try
            {
                lock (outputLock)
                {
                    output.Write(buffer, 0, count);
                    output.Flush();
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            { try { pty.Stop(); } catch { } }
        };
        pty.ProcessExited += exitCode => exited.TrySetResult(exitCode);
        using var inputCancellation = new CancellationTokenSource();
        _ = exited.Task.ContinueWith(_ => inputCancellation.Cancel(), TaskScheduler.Default);
        try
        {
            pty.Start(shell, columns, rows, directory, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = account.Home,
                ["USER"] = account.Name,
                ["LOGNAME"] = account.Name,
                ["SHELL"] = shell,
                ["TERM"] = "xterm-256color",
                ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            }, null);
            if (widthPixels > 0 || heightPixels > 0)
                pty.Resize(columns, rows, widthPixels, heightPixels);
            while (pty.IsRunning)
            {
                UserTerminalFrame? frame;
                try { frame = await UserTerminalStreamProtocol.ReadAsync(input, inputCancellation.Token); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { return 64; }
                if (frame is null || frame.Kind == UserTerminalFrameKind.Close) break;
                if (frame.Kind == UserTerminalFrameKind.Input && frame.Input is { Length: > 0 } bytes)
                    pty.Write(bytes, 0, bytes.Length);
                else if (frame.Kind == UserTerminalFrameKind.Resize)
                    pty.Resize(frame.Columns, frame.Rows, frame.WidthPixels, frame.HeightPixels);
            }
            if (pty.IsRunning) pty.Stop();
            return exited.Task.IsCompletedSuccessfully ? exited.Task.Result : 0;
        }
        finally
        {
            try { pty.Stop(); } catch { }
            (pty as IDisposable)?.Dispose();
        }
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
        object? result = request.Operation switch
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
        if (json.Length > UserExecutionProtocol.MaximumResultBytes) return Fail(UserExecutionProblemCode.ContentTooLarge, "user-execution result is too large");
        return new(true, Convert.ToBase64String(json));
    }

    private static DirectoryDto List(string path)
    {
        LinuxUserFileOperations.RecoverAbandonedInDirectory(path);
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
    private static async Task<FileRead> ReadAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new ContentTooLargeException();
        var content = await ReadBoundedBytesAsync(file, UserExecutionProtocol.MaximumFileContentBytes);
        return new(Convert.ToBase64String(content), Path.GetFileName(path), ContentType(path));
    }
    private static Task<FileEntryDto> WriteAsync(string path, string content)
    {
        LinuxUserFileOperations.WriteAllBytes(path, Decode(content));
        return Task.FromResult(FileEntry(new FileInfo(path)));
    }
    private static bool Delete(string path) { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); else if (System.IO.File.Exists(path)) System.IO.File.Delete(path); else throw new FileNotFoundException(); return true; }
    private static FileSystemEntryDto Rename(string path, string name, string home) { ValidateName(name); var target = ValidatePath(Path.Combine(Path.GetDirectoryName(path)!, name)); if (System.IO.Directory.Exists(path)) { System.IO.Directory.Move(path, target); return DirectoryEntry(target); } System.IO.File.Move(path, target); return ToInfo(FileEntry(new FileInfo(target))); }
    private static FileSystemEntryDto Move(string source, string target, bool overwrite) { var directory = System.IO.Directory.Exists(source); LinuxUserFileOperations.Move(source, target, overwrite); return directory ? DirectoryEntry(target) : ToInfo(FileEntry(new FileInfo(target))); }
    private static FileSystemEntryDto Copy(string source, string target, bool overwrite) { var directory = System.IO.Directory.Exists(source); LinuxUserFileOperations.Copy(source, target, overwrite); return directory ? DirectoryEntry(target) : ToInfo(FileEntry(new FileInfo(target))); }
    private static Task<FileEntryDto> UploadAsync(string directory, string name, string content, string home)
    {
        ValidateName(name);
        var path = ValidatePath(Path.Combine(directory, name));
        LinuxUserFileOperations.WriteAllBytes(path, Decode(content));
        return Task.FromResult(FileEntry(new FileInfo(path)));
    }
    private static bool Create(string path) { System.IO.Directory.CreateDirectory(path); return true; }
    private static async Task<GitResult> GitAsync(string workingDirectory, IReadOnlyList<string>? arguments)
    {
        if (!UserExecutionGitPolicy.IsAllowed(arguments)) throw new ArgumentException();
        var git = File.Exists("/usr/bin/git") ? "/usr/bin/git" : File.Exists("/usr/local/bin/git") ? "/usr/local/bin/git" : throw new FileNotFoundException();
        using var process = new Process { StartInfo = new ProcessStartInfo(git) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        UserExecutionGitPolicy.ApplySafeEnvironment(process.StartInfo);
        foreach (var argument in arguments!) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var budget = new OutputBudget(UserExecutionProtocol.MaximumResultBytes);
        var stdout = ReadBoundedTextAsync(process.StandardOutput.BaseStream, budget);
        var stderr = ReadBoundedTextAsync(process.StandardError.BaseStream, budget);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
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
    private static bool TryShell(string? requested, out string shell)
    {
        shell = requested?.Trim() switch { null or "" or "bash" or "/bin/bash" => "/bin/bash", "sh" or "/bin/sh" => "/bin/sh", _ => string.Empty };
        return shell.Length > 0 && File.Exists(shell);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream input, bool lineTerminated)
    {
        await using var content = new MemoryStream();
        if (lineTerminated)
        {
            var one = new byte[1];
            while (content.Length <= UserExecutionProtocol.MaximumRequestBytes)
            {
                if (await input.ReadAsync(one) == 0) break;
                if (one[0] == (byte)'\n') return content.ToArray();
                if (one[0] != (byte)'\r') content.WriteByte(one[0]);
            }
            if (content.Length == 0 || content.Length > UserExecutionProtocol.MaximumRequestBytes)
                throw new InvalidDataException("Invalid user-execution request size.");
            throw new InvalidDataException("Terminal request is not line terminated.");
        }

        var buffer = new byte[16 * 1024];
        while (true)
        {
            var remaining = UserExecutionProtocol.MaximumRequestBytes + 1 - (int)content.Length;
            if (remaining <= 0) throw new InvalidDataException("User-execution request is too large.");
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
            if (read == 0) break;
            await content.WriteAsync(buffer.AsMemory(0, read));
        }
        if (content.Length == 0 || content.Length > UserExecutionProtocol.MaximumRequestBytes)
            throw new InvalidDataException("Invalid user-execution request size.");
        return content.ToArray();
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream input, int maximumBytes)
    {
        await using var content = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) return content.ToArray();
            if (content.Length + read > maximumBytes) throw new ContentTooLargeException();
            await content.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static async Task<string> ReadBoundedTextAsync(Stream input, OutputBudget budget)
    {
        await using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var exceeded = false;
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            if (budget.TryReserve(read)) await content.WriteAsync(buffer.AsMemory(0, read));
            else exceeded = true;
        }
        if (exceeded) throw new ContentTooLargeException();
        return Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
    }

    private static bool IsValidRequest(UserExecutionRequest request, bool terminal)
    {
        if (request.Identity is null || request.Version != UserExecutionProtocol.Version
            || request.OperationId is not { } operationId || operationId == Guid.Empty || !Enum.IsDefined(request.Operation))
            return false;
        var noDestination = request.DestinationPath is null;
        var noName = request.NewName is null && request.FileName is null;
        var noContent = request.ContentBase64 is null;
        var noMode = request.UnixMode is null;
        var noGit = request.GitArguments is null;
        var noTerminal = request.TerminalShell is null && request.TerminalColumns is null && request.TerminalRows is null
            && request.TerminalWidthPixels is null && request.TerminalHeightPixels is null;
        return request.Operation switch
        {
            UserExecutionOperationKind.FileGetSpecialLocations => !terminal && request.Path is null && noDestination && noName && noContent && noMode && noGit && noTerminal,
            UserExecutionOperationKind.FileListDirectory or UserExecutionOperationKind.FileGetInfo or UserExecutionOperationKind.FileRead
                or UserExecutionOperationKind.FileDelete or UserExecutionOperationKind.FileCreateDirectory or UserExecutionOperationKind.FileGetProperties
                => !terminal && request.Path is not null && noDestination && noName && noContent && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileWrite => !terminal && request.Path is not null && noDestination && noName
                && request.ContentBase64 is not null && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileRename => !terminal && request.Path is not null && noDestination
                && request.NewName is not null && request.FileName is null && noContent && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileMove or UserExecutionOperationKind.FileCopy => !terminal && request.Path is not null
                && request.DestinationPath is not null && noName && noContent && noMode && noGit && noTerminal,
            UserExecutionOperationKind.FileUpload => !terminal && request.Path is not null && noDestination
                && request.NewName is null && request.FileName is not null && request.ContentBase64 is not null
                && noMode && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.FileSetUnixPermissions => !terminal && request.Path is not null && noDestination
                && noName && noContent && request.UnixMode is not null && noGit && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.GitExecute => !terminal && request.Path is not null && noDestination && noName
                && noContent && noMode && request.GitArguments is not null && noTerminal && !request.Overwrite,
            UserExecutionOperationKind.TerminalStart => terminal && request.Path is not null && noDestination && noName
                && noContent && noMode && noGit && request.TerminalColumns is not null && request.TerminalRows is not null
                && request.TerminalWidthPixels is not null && request.TerminalHeightPixels is not null && !request.Overwrite,
            _ => false,
        };
    }
    private static UserExecutionResult Fail(UserExecutionProblemCode code, string message) => new(false, Error: message, ProblemCode: code);
    private static bool TryResolve(UserExecutionIdentity expected, out Account account)
    {
        account = default; if (expected.Platform != PlatformKind.Linux || !uint.TryParse(expected.StableIdentity, out var uid)
            || !UserExecutionProtocol.IsEligibleLinuxUserId(uid)) return false;
        var buffer = Marshal.AllocHGlobal(1_048_576); try { if (getpwuid_r(uid, out var entry, buffer, 1_048_576, out var found) != 0 || found == IntPtr.Zero) return false; var name = Text(entry.Name); var home = Text(entry.Home); if (name != expected.CanonicalAccount || home != expected.HomeDirectory || !Path.IsPathFullyQualified(home!)) return false; account = new(name!, home!, entry.Uid, entry.Gid); return true; } finally { Marshal.FreeHGlobal(buffer); }
    }
    private static bool TryAssumeIdentity(Account account)
    {
        // Supplementary groups must be initialized while privileged. setresgid/setresuid replace
        // the real, effective and saved IDs together so the worker has no saved-root identity to
        // regain. no_new_privs also prevents later exec from acquiring privilege through setuid
        // binaries or file capabilities.
        if (initgroups(account.Name, account.Gid) != 0
            || setresgid(account.Gid, account.Gid, account.Gid) != 0
            || setresuid(account.Uid, account.Uid, account.Uid) != 0
            || prctl(PrSetNoNewPrivileges, 1, 0, 0, 0) != 0)
            return false;
        if (getresuid(out var realUid, out var effectiveUid, out var savedUid) != 0
            || getresgid(out var realGid, out var effectiveGid, out var savedGid) != 0
            || realUid != account.Uid || effectiveUid != account.Uid || savedUid != account.Uid
            || realGid != account.Gid || effectiveGid != account.Gid || savedGid != account.Gid)
            return false;

        // A successful attempt would mean the transition was reversible. Fail before any
        // caller-controlled path is touched, then re-check that even a failed attempt changed no ID.
        if (setuid(0) == 0) return false;
        return getresuid(out realUid, out effectiveUid, out savedUid) == 0
            && realUid == account.Uid && effectiveUid == account.Uid && savedUid == account.Uid;
    }
    private static string? Text(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    private sealed class ContentTooLargeException : Exception { }
    private sealed class OutputBudget(int maximumBytes)
    {
        private int _used;
        public bool TryReserve(int count) => Interlocked.Add(ref _used, count) <= maximumBytes;
    }
    private readonly record struct Account(string Name, string Home, uint Uid, uint Gid);
    private sealed record FileRead(string ContentBase64, string FileName, string ContentType);
    private sealed record GitResult(bool Success, int ExitCode, string Output, string Error);
    [StructLayout(LayoutKind.Sequential)] private struct Passwd { public IntPtr Name, Password; public uint Uid, Gid; public IntPtr Gecos, Home, Shell; }
    [DllImport(LibC)] private static extern uint geteuid();
    [DllImport(LibC, SetLastError = true)] private static extern int initgroups(string user, uint group);
    [DllImport(LibC, SetLastError = true)] private static extern int setresgid(uint real, uint effective, uint saved);
    [DllImport(LibC, SetLastError = true)] private static extern int setresuid(uint real, uint effective, uint saved);
    [DllImport(LibC, SetLastError = true)] private static extern int getresgid(out uint real, out uint effective, out uint saved);
    [DllImport(LibC, SetLastError = true)] private static extern int getresuid(out uint real, out uint effective, out uint saved);
    [DllImport(LibC, SetLastError = true)] private static extern int setuid(uint uid);
    [DllImport(LibC, SetLastError = true)] private static extern int prctl(int option, nuint argument2, nuint argument3, nuint argument4, nuint argument5);
    [DllImport(LibC)] private static extern int getpwuid_r(uint uid, out Passwd pwd, IntPtr buffer, nuint length, out IntPtr result);
}
