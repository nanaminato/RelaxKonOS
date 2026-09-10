using System.Runtime.InteropServices;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.PrivilegedHelper;

if (OperatingSystem.IsWindows() && args.Contains("--windows-service", StringComparer.Ordinal))
{
    WindowsPrivilegedHelperService.Run(args);
    return 0;
}

if (OperatingSystem.IsWindows() && args.Contains("--console", StringComparer.Ordinal))
{
    await WindowsPrivilegedHelperConsoleHost.RunAsync(args);
    return 0;
}

return await PrivilegedOperationExecutor.RunOneShotAsync();

/// <summary>
/// Closed-set privileged operations shared by the Linux one-shot worker and both Windows hosts.
/// Host code owns transport, identity and lifecycle; this type never does.
/// </summary>
public static class PrivilegedOperationExecutor
{
public static async Task<int> RunOneShotAsync()
{
    if (OperatingSystem.IsLinux() && geteuid() != 0)
    {
        await WriteResultAsync(Fail(77, PrivilegedProblemCode.AccessDenied, "root is required"));
        return 77;
    }

    // A root-owned installation configures this list. An empty list deliberately fails closed: a
    // compromised Server account must not turn the file explorer into arbitrary root file I/O.
    var policy = new PrivilegedOperationPolicy(LoadAllowedRoots(), LoadAllowedServices());
    PrivilegedOperationRequest? request;
    try
    {
        request = await ReadRequestAsync(Console.OpenStandardInput());
    }
    catch (Exception exception) when (exception is JsonException or InvalidDataException)
    {
        await WriteResultAsync(Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid request"));
        return 64;
    }

    if (request is null)
    {
        await WriteResultAsync(Fail(64, PrivilegedProblemCode.InvalidRequest, "missing request"));
        return 64;
    }

    var result = await ExecuteAsync(request, policy);
    await WriteResultAsync(result);
    return result.ExitCode;
}

public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, PrivilegedOperationPolicy policy)
{
    if (request.Version != PrivilegedOperationProtocol.Version)
        return Fail(64, PrivilegedProblemCode.InvalidProtocol, "unsupported protocol version");
    if (request.OperationId is not { } operationId || operationId == Guid.Empty)
        return Fail(64, PrivilegedProblemCode.InvalidRequest, "operation id is required");
    if (OperatingSystem.IsWindows() && request.Operation is >= PrivilegedOperationKind.SmbDetect and <= PrivilegedOperationKind.SmbSetUserPassword)
        return await WindowsSmbNativeOperations.ExecuteAsync(request);

    try
    {
        return request.Operation switch
        {
            PrivilegedOperationKind.FileRead => await ReadFileAsync(request.Path, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileWrite => await WriteFileAsync(request.Path, request.ContentBase64, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileDelete => Delete(request.Path, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileRename => Rename(request.Path, request.NewName, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileMove => Move(request.Path, request.DestinationPath, request.Overwrite, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileCopy => Copy(request.Path, request.DestinationPath, request.Overwrite, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileUpload => await UploadAsync(request.Path, request.FileName, request.ContentBase64, policy.FileAllowedRoots),
            PrivilegedOperationKind.FileCreateDirectory => CreateDirectory(request.Path, policy.FileAllowedRoots),
            PrivilegedOperationKind.NativeServiceAction => await ApplyNativeServiceActionAsync(request.ServiceId, request.ServiceAction, policy.AllowedServiceIds),
            PrivilegedOperationKind.NginxSystemServiceAction => await ApplyNginxSystemServiceActionAsync(request.NginxServiceAction),
            PrivilegedOperationKind.NginxPackageInstall => await InstallNginxPackageAsync(request.PackageVersion),
            PrivilegedOperationKind.NginxPackageUninstall => await UninstallNginxPackageAsync(),
            PrivilegedOperationKind.NginxWriteManagedFile => await WriteNginxManagedFileAsync(request.Path, request.ContentBase64),
            PrivilegedOperationKind.NginxMoveManagedFile => MoveNginxManagedFile(request.Path, request.DestinationPath, request.Overwrite),
            PrivilegedOperationKind.NginxDeleteManagedFile => DeleteNginxManagedFile(request.Path),
            PrivilegedOperationKind.ProxyMihomoServiceAction => await ApplyProxyMihomoServiceActionAsync(request.ProxyMihomoServiceAction),
            PrivilegedOperationKind.ProxyMihomoInstallSystemService => await InstallProxyMihomoSystemServiceAsync(),
            PrivilegedOperationKind.ProxyMihomoRemoveSystemService => RemoveProxyMihomoSystemService(),
            PrivilegedOperationKind.GitPackageInstall => await InstallGitPackageAsync(),
            PrivilegedOperationKind.FirewallUfwStatus => await ReadFirewallStatusAsync(request.FirewallNumberedStatus == true),
            PrivilegedOperationKind.FirewallUfwSetEnabled => await SetFirewallEnabledAsync(request.FirewallEnabled),
            PrivilegedOperationKind.FirewallUfwSetDefaults => await SetFirewallDefaultsAsync(request.FirewallIncomingPolicy, request.FirewallOutgoingPolicy),
            PrivilegedOperationKind.FirewallUfwCreateRule => await CreateFirewallRuleAsync(request),
            PrivilegedOperationKind.FirewallUfwReplaceRule => await ReplaceFirewallRuleAsync(request),
            PrivilegedOperationKind.FirewallUfwDeleteRule => await DeleteFirewallRuleAsync(request.FirewallRuleNumber, request.FirewallCompanionRuleNumber),
            PrivilegedOperationKind.SmbDetect => await DetectSmbAsync(),
            PrivilegedOperationKind.SmbPackageInstall => await InstallSambaPackageAsync(),
            PrivilegedOperationKind.SmbServiceAction => await ApplySmbServiceActionAsync(request.SmbServiceAction),
            PrivilegedOperationKind.SmbReadManagedConfiguration => await ReadSmbManagedConfigurationAsync(),
            PrivilegedOperationKind.SmbReadUsers => await ReadSambaUsersAsync(),
            PrivilegedOperationKind.SmbApplyManagedConfiguration => await ApplySmbManagedConfigurationAsync(request.SmbShares),
            PrivilegedOperationKind.SmbSetUserEnabled => await SetSambaUserEnabledAsync(request.SmbUsername, request.FirewallEnabled),
            PrivilegedOperationKind.SmbSetUserPassword => await SetSambaUserPasswordAsync(request.SmbUsername, request.SmbPassword),
            // Windows SMB operations are intentionally rejected by this cross-platform executor.
            // The LocalSystem implementation must use compiled Windows APIs, never a command string.
            PrivilegedOperationKind.SmbApplyWindowsShare or PrivilegedOperationKind.SmbRemoveWindowsShare or PrivilegedOperationKind.SmbSetWindowsServerSecurity
                => Fail(64, PrivilegedProblemCode.UnsupportedOperation, "windows SMB API operation is unavailable"),
            _ => Fail(64, PrivilegedProblemCode.UnsupportedOperation, "unsupported operation"),
        };
    }
    catch (UnauthorizedAccessException) { return Fail(77, PrivilegedProblemCode.AccessDenied, "access denied"); }
    catch (DirectoryNotFoundException) { return Fail(2, PrivilegedProblemCode.NotFound, "target directory does not exist"); }
    catch (FileNotFoundException) { return Fail(2, PrivilegedProblemCode.NotFound, "path does not exist"); }
    catch (IOException) { return Fail(1, PrivilegedProblemCode.Conflict, "file operation failed"); }
    catch (ArgumentException) { return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid request"); }
    catch { return Fail(1, PrivilegedProblemCode.InternalError, "helper operation failed"); }
}

static async Task<PrivilegedOperationResult> ReadFileAsync(string? path, IReadOnlyList<string> roots)
{
    var canonical = ValidatePath(path, roots);
    var bytes = await File.ReadAllBytesAsync(canonical);
    if (bytes.Length > PrivilegedOperationProtocol.MaximumFileContentBytes)
        return Fail(75, PrivilegedProblemCode.ContentTooLarge, "file content is too large");
    return new(true, OutputBase64: Convert.ToBase64String(bytes));
}

static async Task<PrivilegedOperationResult> WriteFileAsync(string? path, string? contentBase64, IReadOnlyList<string> roots)
{
    var canonical = ValidatePath(path, roots);
    var content = DecodeContent(contentBase64);
    var directory = Path.GetDirectoryName(canonical);
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException();
    await File.WriteAllBytesAsync(canonical, content);
    return new(true);
}

static PrivilegedOperationResult Delete(string? path, IReadOnlyList<string> roots)
{
    var canonical = ValidatePath(path, roots);
    if (Directory.Exists(canonical)) Directory.Delete(canonical, recursive: true);
    else if (File.Exists(canonical)) File.Delete(canonical);
    else throw new FileNotFoundException();
    return new(true);
}

static PrivilegedOperationResult Rename(string? sourcePath, string? newName, IReadOnlyList<string> roots)
{
    var source = ValidatePath(sourcePath, roots);
    if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || newName.Contains(Path.DirectorySeparatorChar) || newName.Contains(Path.AltDirectorySeparatorChar))
        throw new ArgumentException("invalid file name");
    var destination = ValidatePath(Path.Combine(Path.GetDirectoryName(source)!, newName), roots);
    if (Directory.Exists(source)) new DirectoryInfo(source).MoveTo(destination);
    else if (File.Exists(source)) File.Move(source, destination);
    else throw new FileNotFoundException();
    return new(true);
}

static PrivilegedOperationResult Move(string? sourcePath, string? destinationPath, bool overwrite, IReadOnlyList<string> roots)
{
    var source = ValidatePath(sourcePath, roots);
    var destination = ValidatePath(destinationPath, roots);
    if (Directory.Exists(source))
    {
        if (Directory.Exists(destination) && overwrite) Directory.Delete(destination, recursive: true);
        Directory.Move(source, destination);
    }
    else if (File.Exists(source)) File.Move(source, destination, overwrite);
    else throw new FileNotFoundException();
    return new(true);
}

static PrivilegedOperationResult Copy(string? sourcePath, string? destinationPath, bool overwrite, IReadOnlyList<string> roots)
{
    var source = ValidatePath(sourcePath, roots);
    var destination = ValidatePath(destinationPath, roots);
    if (Directory.Exists(source))
    {
        if (Directory.Exists(destination) && overwrite) Directory.Delete(destination, recursive: true);
        CopyDirectory(source, destination, roots);
    }
    else if (File.Exists(source)) File.Copy(source, destination, overwrite);
    else throw new FileNotFoundException();
    return new(true);
}

static async Task<PrivilegedOperationResult> UploadAsync(string? targetDirectoryPath, string? fileName, string? contentBase64, IReadOnlyList<string> roots)
{
    var directory = ValidatePath(targetDirectoryPath, roots);
    if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        throw new ArgumentException("invalid file name");
    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
    await File.WriteAllBytesAsync(ValidatePath(Path.Combine(directory, fileName), roots), DecodeContent(contentBase64));
    return new(true);
}

static PrivilegedOperationResult CreateDirectory(string? path, IReadOnlyList<string> roots)
{
    var canonical = ValidatePath(path, roots);
    if (Directory.Exists(canonical)) return Fail(17, PrivilegedProblemCode.Conflict, "directory already exists");
    Directory.CreateDirectory(canonical);
    return new(true);
}

static byte[] DecodeContent(string? contentBase64)
{
    if (contentBase64 is null) throw new ArgumentException("content is required");
    if (contentBase64.Length > ((PrivilegedOperationProtocol.MaximumFileContentBytes + 2) / 3 * 4))
        throw new ArgumentException("content too large");
    var content = Convert.FromBase64String(contentBase64);
    if (content.Length > PrivilegedOperationProtocol.MaximumFileContentBytes) throw new ArgumentException("content too large");
    return content;
}

static string ValidatePath(string? path, IReadOnlyList<string> roots)
{
    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("absolute path is required");
    var canonical = Path.GetFullPath(path);
    var root = roots.FirstOrDefault(candidate => IsWithin(canonical, candidate));
    if (root is null) throw new UnauthorizedAccessException();
    EnsureNoReparsePoints(root, canonical);
    return canonical;
}

static void EnsureNoReparsePoints(string root, string path)
{
    // Existing path components must all be real directories/files. This closes the common
    // "approved root/subdir -> symlink -> /etc" traversal before performing the mutation.
    var current = root;
    ThrowIfReparsePoint(current);
    var relative = Path.GetRelativePath(root, path);
    foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
    {
        current = Path.Combine(current, segment);
        if (!File.Exists(current) && !Directory.Exists(current)) break;
        ThrowIfReparsePoint(current);
    }
}

static void ThrowIfReparsePoint(string path)
{
    if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        throw new UnauthorizedAccessException();
}

static bool IsWithin(string path, string root) => string.Equals(path, root, GetPathComparison())
    || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, GetPathComparison());

static IReadOnlyList<string> LoadAllowedRoots()
{
    // The policy file is installed root-owned beside the service configuration. Environment
    // fallback is solely for isolated Helper tests; sudo's default env_reset excludes it.
    const string policyPath = "/etc/relaxkonos/privileged-helper-roots";
    var configured = File.Exists(policyPath)
        ? File.ReadAllText(policyPath)
        : Environment.GetEnvironmentVariable("RELAXKONOS_PRIVILEGED_FILE_ROOTS") ?? string.Empty;
    return configured.Split([Path.PathSeparator, '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(line => !line.StartsWith('#'))
    .Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).Distinct(GetPathComparer()).ToArray();
}

static void CopyDirectory(string source, string destination, IReadOnlyList<string> roots)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        var target = ValidatePath(Path.Combine(destination, Path.GetFileName(file)), roots);
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException();
        File.Copy(file, target, overwrite: false);
    }
    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException();
        CopyDirectory(directory, ValidatePath(Path.Combine(destination, Path.GetFileName(directory)), roots), roots);
    }
}

static async Task<PrivilegedOperationResult> ApplyNativeServiceActionAsync(string? serviceId, PrivilegedServiceAction? action, IReadOnlyList<string> allowedServiceIds)
{
    if (string.IsNullOrWhiteSpace(serviceId) || action is null || !IsServiceId(serviceId) || !allowedServiceIds.Contains(serviceId, StringComparer.OrdinalIgnoreCase))
        return Fail(64, PrivilegedProblemCode.ResourceNotAllowed, "service action is not allowed");
    var command = action.Value switch
    {
        PrivilegedServiceAction.Start => "start",
        PrivilegedServiceAction.Stop => "stop",
        PrivilegedServiceAction.Restart => "restart",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    var fileName = OperatingSystem.IsWindows() ? "sc.exe" : "systemctl";
    var arguments = OperatingSystem.IsWindows() ? new[] { command, serviceId } : new[] { command, serviceId };
    using var process = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        },
    };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "service manager could not start");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await process.WaitForExitAsync(timeout.Token);
    return process.ExitCode == 0 ? new(true) : Fail(1, PrivilegedProblemCode.InternalError, "service action failed");
}

static bool IsServiceId(string serviceId) => serviceId.Length is > 0 and <= 256
    && serviceId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '@');

static IReadOnlyList<string> LoadAllowedServices()
{
    const string policyPath = "/etc/relaxkonos/privileged-services";
    var configured = File.Exists(policyPath) ? File.ReadAllText(policyPath)
        : Environment.GetEnvironmentVariable("RELAXKONOS_PRIVILEGED_SERVICE_IDS") ?? string.Empty;
    return configured.Split(['\r', '\n', Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(IsServiceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

static async Task<PrivilegedOperationResult> ApplyNginxSystemServiceActionAsync(NginxSystemServiceAction? action)
{
    if (!OperatingSystem.IsLinux() || action is null) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "nginx system service operation is unavailable");
    var arguments = action.Value switch
    {
        NginxSystemServiceAction.Start => new[] { "start", "nginx.service" },
        NginxSystemServiceAction.Stop => new[] { "stop", "nginx.service" },
        NginxSystemServiceAction.Restart => new[] { "restart", "nginx.service" },
        NginxSystemServiceAction.Reload => new[] { "reload", "nginx.service" },
        NginxSystemServiceAction.Enable => new[] { "enable", "nginx.service" },
        NginxSystemServiceAction.Disable => new[] { "disable", "nginx.service" },
        NginxSystemServiceAction.EnableAndStart => new[] { "enable", "--now", "nginx.service" },
        NginxSystemServiceAction.DisableAndStop => new[] { "disable", "--now", "nginx.service" },
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    return await RunFixedCommandAsync("/usr/bin/systemctl", arguments, TimeSpan.FromSeconds(30), "nginx service operation failed");
}

static async Task<PrivilegedOperationResult> ApplyProxyMihomoServiceActionAsync(ProxyMihomoServiceAction? action)
{
    if (!OperatingSystem.IsLinux() || action is null) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "proxy system service operation is unavailable");
    var arguments = action.Value switch
    {
        ProxyMihomoServiceAction.DaemonReload => new[] { "daemon-reload" },
        ProxyMihomoServiceAction.Enable => new[] { "enable", "relaxkonos-mihomo.service" },
        ProxyMihomoServiceAction.Disable => new[] { "disable", "relaxkonos-mihomo.service" },
        ProxyMihomoServiceAction.Start => new[] { "start", "relaxkonos-mihomo.service" },
        ProxyMihomoServiceAction.Stop => new[] { "stop", "relaxkonos-mihomo.service" },
        ProxyMihomoServiceAction.Restart => new[] { "restart", "relaxkonos-mihomo.service" },
        ProxyMihomoServiceAction.TryRestart => new[] { "try-restart", "relaxkonos-mihomo.service" },
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
    return await RunFixedCommandAsync("/usr/bin/systemctl", arguments, TimeSpan.FromSeconds(30), "proxy service operation failed");
}

static async Task<PrivilegedOperationResult> InstallProxyMihomoSystemServiceAsync()
{
    if (!OperatingSystem.IsLinux()) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "proxy system service operation is unavailable");
    const string unitPath = "/etc/systemd/system/relaxkonos-mihomo.service";
    const string unit = "[Unit]\nDescription=RelaxKonOS managed Mihomo\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nExecStart=/var/lib/relaxkonos/proxy/engines/mihomo/versions/current/mihomo -d /var/lib/relaxkonos/proxy/engines/mihomo/data -f /etc/relaxkonos/proxy/active.yaml\nRestart=on-failure\nRestartSec=3\nNoNewPrivileges=true\nPrivateTmp=true\n\n[Install]\nWantedBy=multi-user.target\n";
    try
    {
        var staging = unitPath + ".new";
        await File.WriteAllTextAsync(staging, unit);
        File.Move(staging, unitPath, overwrite: true);
        return new(true);
    }
    catch (IOException) { return Fail(1, PrivilegedProblemCode.InternalError, "proxy service configuration failed"); }
    catch (UnauthorizedAccessException) { return Fail(77, PrivilegedProblemCode.AccessDenied, "proxy service configuration was denied"); }
}

static PrivilegedOperationResult RemoveProxyMihomoSystemService()
{
    if (!OperatingSystem.IsLinux()) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "proxy system service operation is unavailable");
    try
    {
        const string unitPath = "/etc/systemd/system/relaxkonos-mihomo.service";
        if (File.Exists(unitPath)) File.Delete(unitPath);
        return new(true);
    }
    catch (IOException) { return Fail(1, PrivilegedProblemCode.InternalError, "proxy service removal failed"); }
    catch (UnauthorizedAccessException) { return Fail(77, PrivilegedProblemCode.AccessDenied, "proxy service removal was denied"); }
}

static async Task<PrivilegedOperationResult> InstallNginxPackageAsync(string? version)
{
    if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/apt-get")) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "nginx package operation is unavailable");
    if (!string.IsNullOrWhiteSpace(version) && !System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9][0-9A-Za-z.+:~\\-]{0,127}$"))
        return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid nginx package version");
    var update = await RunFixedCommandAsync("/usr/bin/apt-get", ["update"], TimeSpan.FromMinutes(10), "nginx package update failed");
    if (!update.Success) return update;
    var package = string.IsNullOrWhiteSpace(version) ? "nginx" : "nginx=" + version.Trim();
    return await RunFixedCommandAsync("/usr/bin/apt-get", ["install", "--yes", "--no-install-recommends", package], TimeSpan.FromMinutes(10), "nginx package install failed");
}

static Task<PrivilegedOperationResult> UninstallNginxPackageAsync() => !OperatingSystem.IsLinux() || !File.Exists("/usr/bin/apt-get")
    ? Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "nginx package operation is unavailable"))
    : RunFixedCommandAsync("/usr/bin/apt-get", ["purge", "--yes", "--auto-remove", "nginx"], TimeSpan.FromMinutes(10), "nginx package uninstall failed");

static async Task<PrivilegedOperationResult> WriteNginxManagedFileAsync(string? path, string? contentBase64)
{
    var destination = ValidateNginxManagedFile(path);
    var content = DecodeContent(contentBase64);
    var directory = Path.GetDirectoryName(destination)!;
    Directory.CreateDirectory(directory);
    var temporary = Path.Combine(directory, ".relaxkonos-write-" + Guid.NewGuid().ToString("N"));
    try
    {
        await File.WriteAllBytesAsync(temporary, content);
        File.Move(temporary, destination, overwrite: true);
        return new(true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}

static PrivilegedOperationResult MoveNginxManagedFile(string? sourcePath, string? destinationPath, bool overwrite)
{
    var source = ValidateNginxManagedFile(sourcePath);
    var destination = ValidateNginxManagedFile(destinationPath);
    if (!File.Exists(source)) throw new FileNotFoundException();
    File.Move(source, destination, overwrite);
    return new(true);
}

static PrivilegedOperationResult DeleteNginxManagedFile(string? path)
{
    var canonical = ValidateNginxManagedFile(path);
    if (!File.Exists(canonical)) throw new FileNotFoundException();
    File.Delete(canonical);
    return new(true);
}

static string ValidateNginxManagedFile(string? path)
{
    if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        throw new UnauthorizedAccessException();
    var canonical = Path.GetFullPath(path);
    const string includeRoot = "/etc/nginx/conf.d";
    var relaxkonosDirectory = Path.Combine(includeRoot, "relaxkonos.d");
    var allowed = string.Equals(canonical, Path.Combine(includeRoot, "relaxkonos.conf"), StringComparison.Ordinal)
        || IsWithin(canonical, relaxkonosDirectory)
        || Path.GetFileName(canonical).StartsWith("relaxkonos.", StringComparison.Ordinal) && IsWithin(canonical, includeRoot);
    if (!allowed || Path.GetExtension(canonical) is not (".conf" or ".json" or ".stage" or ".rollback"))
        throw new UnauthorizedAccessException();
    EnsureNoReparsePoints(includeRoot, canonical);
    return canonical;
}

static async Task<PrivilegedOperationResult> InstallGitPackageAsync()
{
    if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/apt-get")) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "git package operation is unavailable");
    var update = await RunFixedCommandAsync("/usr/bin/apt-get", ["update"], TimeSpan.FromMinutes(10), "git package update failed");
    return update.Success
        ? await RunFixedCommandAsync("/usr/bin/apt-get", ["install", "--yes", "--no-install-recommends", "git"], TimeSpan.FromMinutes(10), "git package install failed")
        : update;
}

static Task<PrivilegedOperationResult> ReadFirewallStatusAsync(bool numbered)
{
    if (!OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/ufw"))
        return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "ufw is unavailable"));
    return RunFixedCommandWithOutputAsync("/usr/sbin/ufw", numbered ? ["status", "numbered"] : ["status", "verbose"], "firewall status failed");
}

static Task<PrivilegedOperationResult> SetFirewallEnabledAsync(bool? enabled) => enabled is null
    ? Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "firewall enabled state is required"))
    : RunUfwAsync(["--force", enabled.Value ? "enable" : "disable"], "firewall state update failed");

static Task<PrivilegedOperationResult> SetFirewallDefaultsAsync(FirewallDefaultPolicy? incoming, FirewallDefaultPolicy? outgoing)
{
    if (incoming is null || outgoing is null) return Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "firewall defaults are required"));
    return SetFirewallDefaultsCoreAsync(incoming.Value, outgoing.Value);
}

static async Task<PrivilegedOperationResult> SetFirewallDefaultsCoreAsync(FirewallDefaultPolicy incoming, FirewallDefaultPolicy outgoing)
{
    var first = await RunUfwAsync(["default", FirewallPolicy(incoming), "incoming"], "firewall default update failed");
    return first.Success ? await RunUfwAsync(["default", FirewallPolicy(outgoing), "outgoing"], "firewall default update failed") : first;
}

static Task<PrivilegedOperationResult> CreateFirewallRuleAsync(PrivilegedOperationRequest request)
    => TryFirewallRuleArguments(request, out var arguments)
        ? RunUfwAsync(arguments, "firewall rule creation failed")
        : Task.FromResult(Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid firewall rule"));

static async Task<PrivilegedOperationResult> ReplaceFirewallRuleAsync(PrivilegedOperationRequest request)
{
    if (!TryFirewallRuleNumber(request.FirewallRuleNumber, out var number) || !TryFirewallRuleArguments(request, out var rule))
        return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid firewall rule replacement");
    if (request.FirewallCompanionRuleNumber is { } companion && (!TryFirewallRuleNumber(companion, out _) || companion == number))
        return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid firewall companion rule");
    var delete = await DeleteFirewallRuleAsync(number, request.FirewallCompanionRuleNumber);
    return delete.Success ? await RunUfwAsync(["insert", number.ToString(System.Globalization.CultureInfo.InvariantCulture), .. rule], "firewall rule replacement failed") : delete;
}

static async Task<PrivilegedOperationResult> DeleteFirewallRuleAsync(int? requestedNumber, int? requestedCompanion)
{
    if (!TryFirewallRuleNumber(requestedNumber, out var number)) return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid firewall rule number");
    if (requestedCompanion is { } companion && (!TryFirewallRuleNumber(companion, out _) || companion == number))
        return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid firewall companion rule");
    var numbers = requestedCompanion is { } value ? new[] { number, value }.OrderDescending().ToArray() : [number];
    foreach (var item in numbers)
    {
        var deleted = await RunUfwAsync(["--force", "delete", item.ToString(System.Globalization.CultureInfo.InvariantCulture)], "firewall rule deletion failed");
        if (!deleted.Success) return deleted;
    }
    return new(true);
}

static bool TryFirewallRuleArguments(PrivilegedOperationRequest request, out string[] arguments)
{
    arguments = [];
    if (request.FirewallRuleAction is null || request.FirewallRuleDirection is null || request.FirewallRuleProtocol is null
        || !TryFirewallEndpoint(request.FirewallSource, out var source) || !TryFirewallEndpoint(request.FirewallDestination, out var destination)
        || !TryFirewallPort(request.FirewallPort, out var port)) return false;
    arguments = [FirewallAction(request.FirewallRuleAction.Value), FirewallDirection(request.FirewallRuleDirection.Value)];
    if (request.FirewallRuleProtocol != FirewallRuleProtocol.Any) arguments = [.. arguments, "proto", FirewallProtocol(request.FirewallRuleProtocol.Value)];
    arguments = [.. arguments, "from", source, "to", destination];
    if (port == "any") return true;
    arguments = [.. arguments, "port", port];
    return true;
}

static bool TryFirewallRuleNumber(int? value, out int number) => (number = value ?? 0) is > 0 and <= 10_000;
static bool TryFirewallEndpoint(string? value, out string endpoint)
{
    endpoint = value?.Trim().ToLowerInvariant() ?? string.Empty;
    if (endpoint == "any") return true;
    return endpoint.Length <= 64 && System.Text.RegularExpressions.Regex.IsMatch(endpoint, "^[0-9a-f:.]+(/[0-9]{1,3})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
static bool TryFirewallPort(string? value, out string port)
{
    port = value?.Trim().ToLowerInvariant() ?? string.Empty;
    if (port == "any") return true;
    if (!System.Text.RegularExpressions.Regex.IsMatch(port, "^[0-9]+(:[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return false;
    var parts = port.Split(':');
    return int.TryParse(parts[0], out var first) && first is >= 1 and <= 65535
        && int.TryParse(parts[^1], out var last) && last >= first && last <= 65535;
}
static string FirewallAction(FirewallRuleAction value) => value.ToString().ToLowerInvariant();
static string FirewallDirection(FirewallRuleDirection value) => value.ToString().ToLowerInvariant();
static string FirewallProtocol(FirewallRuleProtocol value) => value.ToString().ToLowerInvariant();
static string FirewallPolicy(FirewallDefaultPolicy value) => value.ToString().ToLowerInvariant();

// SMB is a deliberately self-contained, fixed-resource Helper surface. None of these methods
// accepts executable names, service names, package names, config paths, or Samba directives.
const string SmbMainConfiguration = "/etc/samba/smb.conf";
const string SmbManagedConfiguration = "/etc/samba/relaxkonos.conf";
const string SmbMarker = "# RelaxKonOS SMB managed include - do not edit";
const string SmbInclude = "include = /etc/samba/relaxkonos.conf";

static async Task<PrivilegedOperationResult> DetectSmbAsync()
{
    if (!OperatingSystem.IsLinux()) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "SMB detection is unavailable on this platform");
    if (!IsSupportedDebianFamily()) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Linux distribution is unsupported");
    if (!File.Exists("/usr/sbin/smbd")) return SmbOutput(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.NotInstalled, null, false, false, "file-services.smb.not_installed"));
    var version = await RunFixedCommandWithOutputAsync("/usr/sbin/smbd", ["--version"], "Samba version check failed");
    var active = await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", "smbd.service"], TimeSpan.FromSeconds(10), "Samba service is inactive");
    var port = IsTcpPortListening(445);
    var state = active.Success ? (port ? FileServiceRuntimeState.Running : FileServiceRuntimeState.Failed) : FileServiceRuntimeState.Stopped;
    return SmbOutput(new FileServiceStatusDto(FileServiceProtocol.Smb, state, DecodeUtf8(version.OutputBase64)?.Trim(), active.Success, port,
        state == FileServiceRuntimeState.Running ? null : port ? "file-services.smb.service_stopped" : "file-services.smb.port_unavailable"));
}

static async Task<PrivilegedOperationResult> InstallSambaPackageAsync()
{
    if (!OperatingSystem.IsLinux() || !IsSupportedDebianFamily() || !File.Exists("/usr/bin/apt-get"))
        return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Samba installation is unavailable on this platform");
    var update = await RunFixedCommandAsync("/usr/bin/apt-get", ["update"], TimeSpan.FromMinutes(10), "Samba package update failed");
    return update.Success ? await RunFixedCommandAsync("/usr/bin/apt-get", ["install", "--yes", "--no-install-recommends", "samba"], TimeSpan.FromMinutes(10), "Samba package install failed") : update;
}

static Task<PrivilegedOperationResult> ApplySmbServiceActionAsync(SmbServiceAction? action)
{
    if (!OperatingSystem.IsLinux() || action is null || !File.Exists("/usr/bin/systemctl")) return Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Samba service operation is unavailable"));
    var verb = action.Value switch { SmbServiceAction.Start => "start", SmbServiceAction.Stop => "stop", SmbServiceAction.Restart => "restart", SmbServiceAction.Reload => "reload", _ => throw new ArgumentOutOfRangeException(nameof(action)) };
    return RunFixedCommandAsync("/usr/bin/systemctl", [verb, "smbd.service"], TimeSpan.FromSeconds(30), "Samba service operation failed");
}

static async Task<PrivilegedOperationResult> ReadSmbManagedConfigurationAsync()
{
    if (!OperatingSystem.IsLinux()) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Samba configuration is unavailable");
    if (!File.Exists(SmbManagedConfiguration)) return SmbOutput<IReadOnlyList<FileShareDto>>([]);
    try { return SmbOutput<IReadOnlyList<FileShareDto>>(ParseManagedShares(await File.ReadAllLinesAsync(SmbManagedConfiguration))); }
    catch (InvalidDataException) { return Fail(64, PrivilegedProblemCode.Conflict, "Samba managed configuration is externally modified"); }
}

static async Task<PrivilegedOperationResult> ReadSambaUsersAsync()
{
    if (!OperatingSystem.IsLinux() || !File.Exists("/etc/passwd")) return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Samba users are unavailable");
    var enabled = new HashSet<string>(StringComparer.Ordinal);
    if (File.Exists("/usr/bin/pdbedit"))
    {
        var result = await RunFixedCommandWithOutputAsync("/usr/bin/pdbedit", ["-L"], "Samba user enumeration failed");
        if (result.Success && DecodeUtf8(result.OutputBase64) is { } output)
            foreach (var line in output.Split('\n')) { var name = line.Split(':', 2)[0].Trim(); if (IsValidSmbUsername(name)) enabled.Add(name); }
    }
    var users = File.ReadLines("/etc/passwd").Select(line => line.Split(':')).Where(parts => parts.Length >= 7 && IsValidSmbUsername(parts[0]) && int.TryParse(parts[2], out var uid) && uid >= 1000 && !parts[6].Contains("nologin", StringComparison.OrdinalIgnoreCase))
        .Select(parts => new FileServiceUserDto(parts[0], enabled.Contains(parts[0]), true)).OrderBy(x => x.Username, StringComparer.Ordinal).ToArray();
    return SmbOutput<IReadOnlyList<FileServiceUserDto>>(users);
}

static async Task<PrivilegedOperationResult> ApplySmbManagedConfigurationAsync(IReadOnlyList<SmbManagedShareRequest>? requested)
{
    if (!OperatingSystem.IsLinux() || !IsSupportedDebianFamily() || !File.Exists("/usr/bin/testparm") || !File.Exists("/usr/bin/systemctl"))
        return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Samba configuration is unavailable");
    if (requested is null || requested.Count > 128 || requested.Any(InvalidSmbShare)) return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid Samba share request");
    if (requested.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Count) return Fail(17, PrivilegedProblemCode.Conflict, "duplicate Samba share name");
    if (!File.Exists(SmbMainConfiguration)) return Fail(2, PrivilegedProblemCode.NotFound, "Samba main configuration is unavailable");
    var originalMain = await File.ReadAllTextAsync(SmbMainConfiguration);
    var originalInclude = File.Exists(SmbManagedConfiguration) ? await File.ReadAllBytesAsync(SmbManagedConfiguration) : null;
    if (!TryEnsureManagedInclude(originalMain, out var candidateMain)) return Fail(64, PrivilegedProblemCode.Conflict, "Samba main configuration is not safely managed");
    var candidateInclude = SerializeManagedShares(requested);
    var staging = SmbManagedConfiguration + ".new";
    try
    {
        await File.WriteAllTextAsync(staging, candidateInclude, new System.Text.UTF8Encoding(false));
        File.Move(staging, SmbManagedConfiguration, overwrite: true);
        if (!string.Equals(originalMain, candidateMain, StringComparison.Ordinal)) await AtomicWriteTextAsync(SmbMainConfiguration, candidateMain);
        var tested = await RunFixedCommandAsync("/usr/bin/testparm", ["--suppress-prompt", SmbMainConfiguration], TimeSpan.FromSeconds(30), "Samba configuration validation failed");
        if (!tested.Success) return await RestoreSmbConfigurationAsync(originalMain, originalInclude, "Samba configuration validation failed");
        var reload = await ApplySmbServiceActionAsync(SmbServiceAction.Reload);
        if (!reload.Success || !await IsSmbHealthyAsync()) return await RestoreSmbConfigurationAsync(originalMain, originalInclude, "Samba reload or health check failed");
        return new(true);
    }
    catch (IOException) { return await RestoreSmbConfigurationAsync(originalMain, originalInclude, "Samba configuration apply failed"); }
    finally { if (File.Exists(staging)) File.Delete(staging); }
}

static async Task<PrivilegedOperationResult> SetSambaUserEnabledAsync(string? username, bool? enabled)
{
    if (!OperatingSystem.IsLinux() || !IsValidSmbUsername(username) || enabled is null || !UserExists(username!)) return Fail(64, PrivilegedProblemCode.InvalidRequest, "Samba system account is invalid");
    if (!File.Exists("/usr/bin/smbpasswd")) return Fail(2, PrivilegedProblemCode.NotFound, "Samba is not installed");
    return await RunFixedCommandAsync("/usr/bin/smbpasswd", [enabled.Value ? "-e" : "-d", username!], TimeSpan.FromSeconds(30), "Samba credential update failed");
}

static async Task<PrivilegedOperationResult> SetSambaUserPasswordAsync(string? username, string? password)
{
    if (!OperatingSystem.IsLinux() || !IsValidSmbUsername(username) || !UserExists(username!) || password is not { Length: >= 12 and <= 1024 } || password.Any(char.IsControl)) return Fail(64, PrivilegedProblemCode.InvalidRequest, "Samba credential request is invalid");
    if (!File.Exists("/usr/bin/smbpasswd")) return Fail(2, PrivilegedProblemCode.NotFound, "Samba is not installed");
    // smbpasswd's documented noninteractive stdin protocol is fixed here; the secret is not logged or returned.
    using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/smbpasswd") { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true } };
    process.StartInfo.ArgumentList.Add("-s"); process.StartInfo.ArgumentList.Add(username!);
    if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "Samba credential helper could not start");
    await process.StandardInput.WriteLineAsync(password); await process.StandardInput.WriteLineAsync(password); await process.StandardInput.DisposeAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { return Fail(124, PrivilegedProblemCode.TimedOut, "Samba credential operation timed out"); }
    return process.ExitCode == 0 ? new(true) : Fail(1, PrivilegedProblemCode.InternalError, "Samba credential update failed");
}

static async Task<bool> IsSmbHealthyAsync()
{
    var active = await RunFixedCommandAsync("/usr/bin/systemctl", ["is-active", "--quiet", "smbd.service"], TimeSpan.FromSeconds(10), "Samba service inactive");
    var test = await RunFixedCommandAsync("/usr/bin/testparm", ["--suppress-prompt", SmbMainConfiguration], TimeSpan.FromSeconds(30), "Samba configuration invalid");
    return active.Success && test.Success && IsTcpPortListening(445);
}

static async Task<PrivilegedOperationResult> RestoreSmbConfigurationAsync(string main, byte[]? include, string reason)
{
    try
    {
        await AtomicWriteTextAsync(SmbMainConfiguration, main);
        if (include is null) { if (File.Exists(SmbManagedConfiguration)) File.Delete(SmbManagedConfiguration); }
        else await AtomicWriteBytesAsync(SmbManagedConfiguration, include);
        var tested = await RunFixedCommandAsync("/usr/bin/testparm", ["--suppress-prompt", SmbMainConfiguration], TimeSpan.FromSeconds(30), "Samba rollback validation failed");
        var reload = tested.Success ? await ApplySmbServiceActionAsync(SmbServiceAction.Reload) : tested;
        return reload.Success ? Fail(1, PrivilegedProblemCode.Conflict, reason) : Fail(1, PrivilegedProblemCode.InternalError, "Samba rollback failed");
    }
    catch { return Fail(1, PrivilegedProblemCode.InternalError, "Samba rollback failed"); }
}

static bool TryEnsureManagedInclude(string main, out string candidate)
{
    candidate = main;
    var markerCount = main.Split('\n').Count(line => line.TrimEnd('\r') == SmbMarker);
    var includeCount = main.Split('\n').Count(line => line.Trim().Equals(SmbInclude, StringComparison.OrdinalIgnoreCase));
    if (markerCount > 1 || includeCount > 1 || markerCount != includeCount) return false;
    if (markerCount == 1) return true;
    var global = main.IndexOf("[global]", StringComparison.OrdinalIgnoreCase);
    if (global < 0) return false;
    var end = main.IndexOf('\n', global);
    if (end < 0) end = main.Length;
    candidate = main.Insert(end + (end < main.Length ? 1 : 0), SmbMarker + "\n" + SmbInclude + "\n");
    return true;
}

static string SerializeManagedShares(IReadOnlyList<SmbManagedShareRequest> shares)
{
    var builder = new System.Text.StringBuilder("# RelaxKonOS managed Samba include. Do not edit.\nserver min protocol = SMB2\nclient min protocol = SMB2\nmap to guest = Never\n");
    foreach (var share in shares.OrderBy(x => x.Id, StringComparer.Ordinal))
    {
        builder.Append("\n# relaxkonos-share:").Append(share.Id).Append('\n').Append('[').Append(share.Name).Append("]\npath = ").Append(share.Path).Append('\n')
            .Append("read only = ").Append(share.ReadOnly ? "yes" : "no").Append('\n').Append("available = ").Append(share.Enabled ? "yes" : "no").Append('\n')
            .Append("guest ok = ").Append(share.GuestAllowed ? "yes" : "no").Append('\n');
        if (!string.IsNullOrWhiteSpace(share.Description)) builder.Append("comment = ").Append(share.Description).Append('\n');
        var read = share.Permissions.Where(x => x.Access == "Read").Select(x => x.Principal).Order().ToArray();
        var write = share.Permissions.Where(x => x.Access == "ReadWrite").Select(x => x.Principal).Order().ToArray();
        if (read.Length > 0) builder.Append("read list = ").AppendJoin(' ', read).Append('\n');
        if (write.Length > 0) builder.Append("write list = ").AppendJoin(' ', write).Append('\n');
    }
    return builder.ToString();
}

static IReadOnlyList<FileShareDto> ParseManagedShares(string[] lines)
{
    var shares = new List<FileShareDto>(); string? id = null, name = null, path = null, description = null; bool readOnly = true, enabled = true, guest = false; var permissions = new List<FileSharePermissionDto>();
    void Commit()
    {
        if (id is null && name is null) return;
        if (id is null || name is null || path is null || !IsValidSmbShare(new(id, name, path, description, readOnly, enabled, guest, permissions.Select(x => new SmbSharePermissionRequest(x.Principal, x.Access.ToString())).ToArray()))) throw new InvalidDataException();
        shares.Add(new(id, name, path, description, readOnly, enabled, guest, permissions.ToArray(), true)); id = name = path = description = null; readOnly = true; enabled = true; guest = false; permissions.Clear();
    }
    foreach (var raw in lines)
    {
        var line = raw.Trim(); const string shareMarker = "# relaxkonos-share:"; if (line.StartsWith(shareMarker, StringComparison.Ordinal)) { Commit(); id = line[shareMarker.Length..]; continue; }
        if (id is null) continue;
        if (line.StartsWith('[') && line.EndsWith(']')) { name = line[1..^1]; continue; }
        var index = line.IndexOf('='); if (index < 1) continue; var key = line[..index].Trim(); var value = line[(index + 1)..].Trim();
        switch (key) { case "path": path = value; break; case "comment": description = value; break; case "read only": readOnly = value == "yes"; break; case "available": enabled = value == "yes"; break; case "guest ok": guest = value == "yes"; break;
            case "read list": permissions.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => new FileSharePermissionDto(x, FileShareAccess.Read))); break;
            case "write list": permissions.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => new FileSharePermissionDto(x, FileShareAccess.ReadWrite))); break; }
    }
    Commit(); return shares;
}

static bool InvalidSmbShare(SmbManagedShareRequest share) => !IsValidSmbShare(share);
static bool IsValidSmbShare(SmbManagedShareRequest share) => share.Id.Length is > 0 and <= 64 && System.Text.RegularExpressions.Regex.IsMatch(share.Id, "^[A-Za-z0-9-]+$")
    && System.Text.RegularExpressions.Regex.IsMatch(share.Name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") && Path.IsPathFullyQualified(share.Path)
    && Directory.Exists(share.Path) && (string.Equals(share.Path, "/srv/relaxkonos-shares", StringComparison.Ordinal) || share.Path.StartsWith("/srv/relaxkonos-shares/", StringComparison.Ordinal))
    && !HasUnsafeSmbText(share.Name) && !HasUnsafeSmbText(share.Path) && !HasUnsafeSmbText(share.Description)
    && (!share.GuestAllowed || share.ReadOnly)
    && share.Permissions.All(x => System.Text.RegularExpressions.Regex.IsMatch(x.Principal, "^[A-Za-z0-9._@\\\\-]{1,256}$") && x.Access is "Read" or "ReadWrite");
static bool HasUnsafeSmbText(string? value) => value is not null && (value.Any(char.IsControl) || value.Contains('=') || value.Contains('[') || value.Contains(']') || value.StartsWith('-'));
static bool IsValidSmbUsername(string? username) => username is not null && System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-z_][a-z0-9_-]{0,63}$");
static bool UserExists(string username) => File.ReadLines("/etc/passwd").Any(line => line.StartsWith(username + ":", StringComparison.Ordinal));
static bool IsSupportedDebianFamily() => File.Exists("/etc/os-release") && (File.ReadAllText("/etc/os-release").Contains("ID=debian", StringComparison.OrdinalIgnoreCase) || File.ReadAllText("/etc/os-release").Contains("ID=ubuntu", StringComparison.OrdinalIgnoreCase));
static bool IsTcpPortListening(int port) => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
static string? DecodeUtf8(string? output) { try { return output is null ? null : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output)); } catch (FormatException) { return null; } }
static PrivilegedOperationResult SmbOutput<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
static async Task AtomicWriteTextAsync(string path, string content) => await AtomicWriteBytesAsync(path, new System.Text.UTF8Encoding(false).GetBytes(content));
static async Task AtomicWriteBytesAsync(string path, byte[] bytes) { var temp = path + ".relaxkonos-" + Guid.NewGuid().ToString("N"); await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, path, overwrite: true); }
static Task<PrivilegedOperationResult> RunUfwAsync(IReadOnlyList<string> arguments, string failure) => !OperatingSystem.IsLinux() || !File.Exists("/usr/sbin/ufw")
    ? Task.FromResult(Fail(64, PrivilegedProblemCode.UnsupportedOperation, "ufw is unavailable"))
    : RunFixedCommandAsync("/usr/sbin/ufw", arguments, TimeSpan.FromSeconds(30), failure);

static async Task<PrivilegedOperationResult> RunFixedCommandWithOutputAsync(string executable, IReadOnlyList<string> arguments, string failure)
{
    using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "host operation could not start");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var output = process.StandardOutput.ReadToEndAsync(cancellation.Token);
    try { await process.WaitForExitAsync(cancellation.Token); }
    catch (OperationCanceledException) { return Fail(124, PrivilegedProblemCode.TimedOut, "host operation timed out"); }
    var text = await output;
    return process.ExitCode == 0 ? new(true, OutputBase64: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))) : Fail(1, PrivilegedProblemCode.InternalError, failure);
}

static async Task<PrivilegedOperationResult> RunFixedCommandAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, string failure)
{
    using var process = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
    process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) return Fail(69, PrivilegedProblemCode.HelperUnavailable, "host operation could not start");
    using var cancellation = new CancellationTokenSource(timeout);
    try { await process.WaitForExitAsync(cancellation.Token); }
    catch (OperationCanceledException) { return Fail(124, PrivilegedProblemCode.TimedOut, "host operation timed out"); }
    return process.ExitCode == 0 ? new(true) : Fail(1, PrivilegedProblemCode.InternalError, failure);
}

static PrivilegedOperationResult Fail(int exitCode, PrivilegedProblemCode code, string error) => new(false, exitCode, Error: error, ProblemCode: code);
static Task WriteResultAsync(PrivilegedOperationResult result) => JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result);
static async Task<PrivilegedOperationRequest?> ReadRequestAsync(Stream input)
{
    await using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    while (true)
    {
        var read = await input.ReadAsync(chunk);
        if (read == 0) break;
        if (buffer.Length + read > PrivilegedOperationProtocol.MaximumRequestBytes)
            throw new InvalidDataException("request too large");
        await buffer.WriteAsync(chunk.AsMemory(0, read));
    }
    buffer.Position = 0;
    return await JsonSerializer.DeserializeAsync<PrivilegedOperationRequest>(buffer);
}
static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
static StringComparer GetPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

[DllImport("libc")]
static extern uint geteuid();
}

/// <summary>Host-supplied allowlists for the closed-set privileged operation dispatcher.</summary>
public sealed record PrivilegedOperationPolicy(IReadOnlyList<string> FileAllowedRoots, IReadOnlyList<string> AllowedServiceIds);
