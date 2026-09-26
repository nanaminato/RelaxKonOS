using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.UserExecution;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Executes the closed Windows file-operation subset under a fresh local-account S4U token.
/// Identity lookup and token creation happen before impersonation; user-controlled paths are not
/// touched until <see cref="WindowsIdentity.RunImpersonated{T}(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle,Func{T})"/> is active.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsUserExecutionExecutor
{
    private static readonly SecurityIdentifier LocalSystemSid =
        new(WellKnownSidType.LocalSystemSid, null);

    public static Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request,
        CancellationToken cancellationToken) => Task.FromResult(Execute(request, cancellationToken));

    private static UserExecutionResult Execute(UserExecutionRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Fail(UserExecutionProblemCode.UnsupportedPlatform, "Windows user execution requires Windows");
        using (var current = WindowsIdentity.GetCurrent())
            if (current.User != LocalSystemSid)
                return Fail(UserExecutionProblemCode.HelperUnavailable, "LocalSystem Helper is required");

        if (!TryResolveLocalIdentity(request.Identity, out var account))
            return Fail(UserExecutionProblemCode.IdentityMismatch, "OS identity changed or is not executable");

        try
        {
            using var token = WindowsS4ULogon.Logon(account.Username, account.Domain);
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (identity.User?.Value != request.Identity.StableIdentity)
                    return Fail(UserExecutionProblemCode.IdentityMismatch, "S4U token identity mismatch");
                if (identity.ImpersonationLevel != TokenImpersonationLevel.Impersonation
                    || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    return Fail(UserExecutionProblemCode.IdentityNotExecutable,
                        "S4U token is not an ordinary impersonation token");
            }

            // Keep all path access within the synchronous impersonation callback. No task or file
            // handle is allowed to outlive the OS token scope.
            var result = WindowsIdentity.RunImpersonated(token,
                () => ExecuteImpersonated(request, account.HomeDirectory, cancellationToken));
            using var restored = WindowsIdentity.GetCurrent();
            return restored.User == LocalSystemSid
                ? result
                : Fail(UserExecutionProblemCode.InternalError, "Helper identity was not restored");
        }
        catch (OperationCanceledException) { return Fail(UserExecutionProblemCode.Cancelled, "user-execution operation was cancelled"); }
        catch (ContentTooLargeException) { return Fail(UserExecutionProblemCode.ContentTooLarge, "user-execution content is too large"); }
        catch (UserExecutionUnsupportedException) { return Fail(UserExecutionProblemCode.UnsupportedPlatform, "operation is not implemented for Windows user execution"); }
        catch (UnauthorizedAccessException) { return Fail(UserExecutionProblemCode.AccessDenied, "access denied"); }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        { return Fail(UserExecutionProblemCode.NotFound, "path not found"); }
        catch (IOException) { return Fail(UserExecutionProblemCode.Conflict, "file operation failed"); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException)
        { return Fail(UserExecutionProblemCode.InvalidRequest, "invalid file operation"); }
        catch (Win32Exception) { return Fail(UserExecutionProblemCode.IdentityNotExecutable, "could not create user execution token"); }
        catch { return Fail(UserExecutionProblemCode.InternalError, "user-execution operation failed"); }
    }

    private static UserExecutionResult ExecuteImpersonated(UserExecutionRequest request,
        string homeDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? output = request.Operation switch
        {
            UserExecutionOperationKind.FileListDirectory => List(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileGetSpecialLocations => SpecialLocations(homeDirectory),
            UserExecutionOperationKind.FileGetInfo => GetInfo(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileRead => Read(ValidatePath(request.Path!), cancellationToken),
            UserExecutionOperationKind.FileWrite => Write(ValidatePath(request.Path!),
                Decode(request.ContentBase64!), cancellationToken),
            UserExecutionOperationKind.FileDelete => Delete(ValidatePath(request.Path!), cancellationToken),
            UserExecutionOperationKind.FileRename => Rename(ValidatePath(request.Path!), request.NewName!),
            UserExecutionOperationKind.FileMove => Move(ValidatePath(request.Path!),
                ValidatePath(request.DestinationPath!), request.Overwrite, cancellationToken),
            UserExecutionOperationKind.FileCopy => Copy(ValidatePath(request.Path!),
                ValidatePath(request.DestinationPath!), request.Overwrite, cancellationToken),
            UserExecutionOperationKind.FileUpload => Upload(ValidatePath(request.Path!),
                request.FileName!, Decode(request.ContentBase64!), cancellationToken),
            UserExecutionOperationKind.FileCreateDirectory => CreateDirectory(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileGetProperties => GetProperties(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileSetUnixPermissions => throw new UserExecutionUnsupportedException(),
            UserExecutionOperationKind.FileCreateStaging => CreateStaging(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileAppendStaging => AppendStaging(ValidatePath(request.Path!),
                request.Offset!.Value, request.ExpectedBytes!.Value, Decode(request.ContentBase64!)),
            UserExecutionOperationKind.FileGetStagingLength => StagingLength(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileDeleteStaging => DeleteStaging(ValidatePath(request.Path!)),
            UserExecutionOperationKind.FileCommitStaging => CommitStaging(ValidatePath(request.Path!),
                ValidatePath(request.DestinationPath!)),
            // Git and Terminal require separate executable/PTY lifecycle work and stay fail-closed.
            UserExecutionOperationKind.GitExecute or UserExecutionOperationKind.TerminalStart
                => throw new UserExecutionUnsupportedException(),
            _ => throw new ArgumentException(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(output, RelaxKonOSJsonOptions.Default);
        return json.Length > UserExecutionProtocol.MaximumResultBytes
            ? Fail(UserExecutionProblemCode.ContentTooLarge, "user-execution result is too large")
            : new UserExecutionResult(true, Convert.ToBase64String(json));
    }

    private static bool TryResolveLocalIdentity(UserExecutionIdentity expected, out LocalAccount account)
    {
        account = default;
        if (expected.Platform != HostPlatformKind.Windows || string.IsNullOrWhiteSpace(expected.StableIdentity)
            || string.IsNullOrWhiteSpace(expected.CanonicalAccount) || string.IsNullOrWhiteSpace(expected.HomeDirectory))
            return false;
        try
        {
            var sid = new SecurityIdentifier(expected.StableIdentity);
            var canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            var parts = canonical.Split('\\', 2);
            if (parts.Length != 2 || !parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || !canonical.Equals(expected.CanonicalAccount, StringComparison.OrdinalIgnoreCase))
                return false;
            var home = GetProfileDirectory(sid.Value);
            if (home is null || !Path.IsPathFullyQualified(home)
                || !Path.GetFullPath(home).Equals(Path.GetFullPath(expected.HomeDirectory), StringComparison.OrdinalIgnoreCase))
                return false;
            account = new(parts[1], parts[0], Path.GetFullPath(home));
            return true;
        }
        catch (Exception exception) when (exception is IdentityNotMappedException or ArgumentException
            or System.Security.SecurityException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static string? GetProfileDirectory(string sid)
    {
        using var profile = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        var raw = profile?.GetValue("ProfileImagePath") as string;
        return string.IsNullOrWhiteSpace(raw) ? null : Environment.ExpandEnvironmentVariables(raw);
    }

    private static IReadOnlyList<SpecialLocationDto> SpecialLocations(string home)
    {
        (SpecialFolderKind Kind, string Name, string Path)[] candidates =
        [
            (SpecialFolderKind.Home, "主目录", home),
            (SpecialFolderKind.Desktop, "桌面", Path.Combine(home, "Desktop")),
            (SpecialFolderKind.Documents, "文档", Path.Combine(home, "Documents")),
            (SpecialFolderKind.Downloads, "下载", Path.Combine(home, "Downloads")),
            (SpecialFolderKind.Pictures, "图片", Path.Combine(home, "Pictures")),
            (SpecialFolderKind.Music, "音乐", Path.Combine(home, "Music")),
            (SpecialFolderKind.Videos, "视频", Path.Combine(home, "Videos")),
        ];
        return candidates.Where(item => Directory.Exists(item.Path))
            .Select(item => new SpecialLocationDto(item.Kind, item.Name, item.Path)).ToArray();
    }

    private static DirectoryDto List(string path)
    {
        var directory = new DirectoryInfo(path);
        var directories = directory.EnumerateDirectories().Select(ToDirectoryEntry).ToArray();
        var files = directory.EnumerateFiles().Select(ToFileEntry).ToArray();
        return new(directory.FullName, directory.Name, FileSystemEntryType.Directory, directories, files,
            directory.CreationTimeUtc, directory.LastWriteTimeUtc);
    }

    private static FileSystemEntryDto? GetInfo(string path)
    {
        var attributes = File.GetAttributes(path);
        return attributes.HasFlag(FileAttributes.Directory)
            ? ToDirectoryEntry(new DirectoryInfo(path))
            : ToInfo(ToFileEntry(new FileInfo(path)));
    }

    private static FileRead Read(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > UserExecutionProtocol.MaximumFileContentBytes) throw new ContentTooLargeException();
        var content = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(content, offset, content.Length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return new(Convert.ToBase64String(content), Path.GetFileName(path), ContentType(path));
    }

    private static FileEntryDto Write(string path, byte[] content, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException();
        var staging = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.SequentialScan))
            {
                const int chunkSize = 64 * 1024;
                for (var offset = 0; offset < content.Length; offset += chunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(content, offset, Math.Min(chunkSize, content.Length - offset));
                }
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: true);
            return ToFileEntry(new FileInfo(path));
        }
        finally { TryDeletePath(staging); }
    }

    private static bool Delete(string path, CancellationToken cancellationToken)
    { DeletePath(path, cancellationToken); return true; }

    private static FileSystemEntryDto Rename(string path, string newName)
    {
        ValidateName(newName);
        var destination = Path.Combine(Path.GetDirectoryName(path) ?? throw new ArgumentException(), newName);
        if (Exists(destination)) throw new IOException();
        MovePath(path, destination);
        return GetInfo(destination)!;
    }

    private static FileSystemEntryDto Move(string source, string destination, bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!Exists(source)) throw new FileNotFoundException();
        try
        {
            CommitMove(source, destination, overwrite);
        }
        catch (IOException) when (Exists(source) && !Exists(destination)
            && File.GetAttributes(source).HasFlag(FileAttributes.Directory))
        {
            Copy(source, destination, overwrite, cancellationToken);
            DeletePath(source, cancellationToken);
        }
        return GetInfo(destination)!;
    }

    private static FileSystemEntryDto Copy(string source, string destination, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        if (!Exists(source)) throw new FileNotFoundException();
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException();
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException();
        if (Exists(destination) && !overwrite) throw new IOException();
        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.copy");
        var backup = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.backup");
        var backedUp = false;
        try
        {
            CopyPath(source, staging, cancellationToken);
            if (Exists(destination)) { MovePath(destination, backup); backedUp = true; }
            try { MovePath(staging, destination); }
            catch
            {
                if (backedUp && !Exists(destination)) MovePath(backup, destination);
                throw;
            }
            if (backedUp) DeletePath(backup);
            return GetInfo(destination)!;
        }
        finally
        {
            TryDeletePath(staging);
            if (backedUp && Exists(backup) && !Exists(destination)) MovePath(backup, destination);
        }
    }

    private static FileEntryDto Upload(string directory, string fileName, byte[] content,
        CancellationToken cancellationToken)
    {
        ValidateName(fileName);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        return Write(Path.Combine(directory, fileName), content, cancellationToken);
    }

    private static bool CreateDirectory(string path)
    {
        if (Exists(path)) throw new IOException();
        Directory.CreateDirectory(path);
        return true;
    }

    private static bool CreateStaging(string path)
    {
        if (Exists(path)) throw new IOException();
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException();
        using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
            FileOptions.None);
        return true;
    }

    private static long AppendStaging(string path, long offset, long expectedBytes, byte[] content)
    {
        if (offset < 0 || expectedBytes < 0 || content.Length != expectedBytes)
            throw new ArgumentException("The staging chunk does not match the declared offset arithmetic.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
            FileOptions.None);
        // Bytes beyond the confirmed offset may be the tail of an attempt that was interrupted before the
        // session index was advanced. They are discarded so a restarted client resumes from the index.
        if (file.Length > offset) file.SetLength(offset);
        if (file.Length != offset) throw new IOException("The staging file length does not match the confirmed offset.");
        try
        {
            file.Position = offset;
            file.Write(content, 0, content.Length);
            file.Flush(flushToDisk: true);
            return file.Length;
        }
        catch
        {
            try { file.SetLength(offset); file.Flush(flushToDisk: true); }
            catch (IOException) { }
            throw;
        }
    }

    private static long StagingLength(string path)
    {
        try { return Exists(path) ? new FileInfo(path).Length : -1; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException)
        { return -1; }
    }

    private static bool DeleteStaging(string path)
    {
        try { if (Exists(path)) DeletePath(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            // Cleanup is retried by the session sweep and must never fail the caller's final operation.
        }
        return true;
    }

    private static FileEntryDto CommitStaging(string path, string destination)
    {
        if (!Exists(path)) throw new FileNotFoundException();
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException();
        CommitMove(path, destination, overwrite: true);
        return ToFileEntry(new FileInfo(destination));
    }

    private static FilePropertiesDto? GetProperties(string path)
    {
        var info = GetInfo(path);
        if (info is null) return null;
        var attributes = File.GetAttributes(path);
        return new(info.Path, info.Name, info.Type, info.Size, info.Created, info.Modified, info.Accessed,
            attributes.HasFlag(FileAttributes.ReadOnly) ? "Read-only" : "Windows ACL", attributes.ToString(), null);
    }

    private static void CommitMove(string source, string destination, bool overwrite)
    {
        if (!Exists(destination)) { MovePath(source, destination); return; }
        if (!overwrite) throw new IOException();
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException();
        var backup = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.backup");
        MovePath(destination, backup);
        try { MovePath(source, destination); }
        catch { MovePath(backup, destination); throw; }
        DeletePath(backup);
    }

    private static void CopyPath(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(source);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            FileSystemInfo link = isDirectory ? new DirectoryInfo(source) : new FileInfo(source);
            var target = link.LinkTarget ?? throw new IOException("Unsupported reparse point.");
            if (isDirectory) Directory.CreateSymbolicLink(destination, target);
            else File.CreateSymbolicLink(destination, target);
            return;
        }
        if (!isDirectory)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            output.Flush(flushToDisk: true);
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            CopyPath(entry.FullName, Path.Combine(destination, entry.Name), cancellationToken);
    }

    private static void MovePath(string source, string destination)
    {
        if (File.GetAttributes(source).HasFlag(FileAttributes.Directory)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void DeletePath(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)) { File.Delete(path); return; }
        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                DeletePath(entry.FullName, cancellationToken);
        Directory.Delete(path, recursive: false);
    }

    private static void TryDeletePath(string path)
    {
        try { if (Exists(path)) DeletePath(path); }
        catch { }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static FileSystemEntryDto ToDirectoryEntry(DirectoryInfo directory)
        => new(directory.FullName, directory.Name, null, FileSystemEntryType.Directory,
            directory.CreationTimeUtc, directory.LastWriteTimeUtc, directory.LastAccessTimeUtc,
            directory.Attributes.HasFlag(FileAttributes.Hidden), directory.Attributes.HasFlag(FileAttributes.System),
            "inode/directory");

    private static FileEntryDto ToFileEntry(FileInfo file)
        => new(file.FullName, file.Name, Path.GetExtension(file.Name), file.Length, file.CreationTimeUtc,
            file.LastWriteTimeUtc, file.LastAccessTimeUtc, file.Attributes.HasFlag(FileAttributes.Hidden),
            file.Attributes.HasFlag(FileAttributes.System), ContentType(file.FullName));

    private static FileSystemEntryDto ToInfo(FileEntryDto file)
        => new(file.Path, file.Name, file.Size, FileSystemEntryType.File, file.Created, file.Modified,
            file.Accessed, file.IsHidden, file.IsSystem, file.MimeType);

    private static string ValidatePath(string path)
        => string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path)
            ? throw new ArgumentException("An absolute path is required.") : Path.GetFullPath(path);

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('\0')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Invalid file name.");
    }

    private static byte[] Decode(string value)
    {
        var content = Convert.FromBase64String(value);
        return content.Length <= UserExecutionProtocol.MaximumFileContentBytes ? content : throw new ContentTooLargeException();
    }

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

    private static UserExecutionResult Fail(UserExecutionProblemCode code, string message)
        => new(false, Error: message, ProblemCode: code);
    private readonly record struct LocalAccount(string Username, string Domain, string HomeDirectory);
    private sealed record FileRead(string ContentBase64, string FileName, string ContentType);
    private sealed class ContentTooLargeException : Exception { }
    private sealed class UserExecutionUnsupportedException : Exception { }
}
