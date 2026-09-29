using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Elevated runtime owner shared by the Windows service and console host. Never invokes UAC.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsManagedRuntimeHost
{
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private const string IntegrityFile = ".helper-integrity.json";
    private const string ManagedMarker = "RelaxKonOS owns this Nginx installation. Do not move this marker.\n";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, OwnedProcess> Processes = new(StringComparer.Ordinal);
    private static WindowsManagedRuntimePolicy? _lastPolicy;
    private static volatile bool _stopping;

    public static async Task<PrivilegedOperationResult> ExecuteAsync(WindowsManagedRuntimeRequest? request, WindowsManagedRuntimePolicy? policy)
    {
        if (request is null || policy is null || !WindowsRuntimeConfigurationWriter.IsValidRequest(request)) return Failure(PrivilegedProblemCode.InvalidRequest);
        await Gate.WaitAsync();
        try
        {
            if (_stopping) return Failure(PrivilegedProblemCode.HelperUnavailable);
            policy.Validate();
            WindowsManagedRuntimePolicy.RequireNoLinks(policy.NginxRoot);
            WindowsManagedRuntimePolicy.RequireNoLinks(policy.PrivateRoot);
            _lastPolicy = policy;
            if (request.Runtime == WindowsManagedRuntime.Nginx) return await NginxAsync(request, policy);
            return await FrpAsync(request, policy);
        }
        catch (UnauthorizedAccessException) { return Failure(PrivilegedProblemCode.ResourceNotAllowed); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or FormatException or ArgumentException)
        { return Failure(PrivilegedProblemCode.InvalidRequest); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        { return Failure(PrivilegedProblemCode.HelperUnavailable); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Failure(PrivilegedProblemCode.InternalError); }
        finally { Gate.Release(); }
    }

    public static async Task<PrivilegedOperationResult> FileAsync(PrivilegedOperationRequest request, WindowsManagedRuntimePolicy? policy)
    {
        if (policy is null) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        var clean = new PrivilegedOperationRequest(request.Operation, Path: request.Path,
            DestinationPath: request.DestinationPath, Overwrite: request.Overwrite, ContentBase64: request.ContentBase64,
            OperationId: request.OperationId, Correlation: request.Correlation, Version: request.Version);
        if (request != clean) return Failure(PrivilegedProblemCode.InvalidRequest);
        await Gate.WaitAsync();
        try
        {
            policy.Validate();
            RequireProtectedPath(policy.NginxRoot, directory: true);
            var path = AllowedNginxFile(request.Path, policy);
            switch (request.Operation)
            {
                case PrivilegedOperationKind.NginxWriteManagedFile:
                    var bytes = Convert.FromBase64String(request.ContentBase64 ?? "");
                    if (bytes.Length > PrivilegedOperationProtocol.MaximumFileContentBytes) return Failure(PrivilegedProblemCode.ContentTooLarge);
                    if (path.Contains(".conf", StringComparison.OrdinalIgnoreCase)) ValidateNginxText(Encoding.UTF8.GetString(bytes), policy);
                    ProtectDirectory(Path.GetDirectoryName(path)!, policy);
                    await AtomicWriteAsync(path, bytes);
                    break;
                case PrivilegedOperationKind.NginxMoveManagedFile:
                    File.Move(path, AllowedNginxFile(request.DestinationPath, policy), request.Overwrite);
                    break;
                case PrivilegedOperationKind.NginxDeleteManagedFile:
                    File.Delete(path);
                    break;
                default: return Failure(PrivilegedProblemCode.InvalidRequest);
            }
            return new(true);
        }
        catch (UnauthorizedAccessException) { return Failure(PrivilegedProblemCode.ResourceNotAllowed); }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException)
        { return Failure(PrivilegedProblemCode.InvalidRequest); }
        finally { Gate.Release(); }
    }

    private static string AllowedNginxFile(string? path, WindowsManagedRuntimePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new UnauthorizedAccessException();
        var canonical = Path.GetFullPath(path);
        // Config and site metadata only; binary, DLL, integrity and package changes have dedicated verbs.
        if (!WindowsManagedRuntimePolicy.Contains(Path.Combine(policy.NginxRoot, "conf"), canonical)
            || canonical == Path.Combine(policy.NginxRoot, "conf")
            || Regex.IsMatch(Path.GetFileName(canonical), "\\.(exe|dll|ps1|bat|cmd)$", RegexOptions.IgnoreCase)
            || Path.GetFileName(canonical).StartsWith(".helper-", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        if (Path.GetFileName(canonical).Equals("mime.types", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
        WindowsManagedRuntimePolicy.RequireNoLinks(canonical);
        return canonical;
    }

    private static async Task<PrivilegedOperationResult> NginxAsync(WindowsManagedRuntimeRequest request, WindowsManagedRuntimePolicy policy)
    {
        if (request.ProfileId is not null || request.Client is not null || request.Server is not null) return Failure(PrivilegedProblemCode.InvalidRequest);
        var root = policy.NginxRoot;
        if (request.Action == WindowsManagedRuntimeAction.Install)
        {
            if (request.Version is not { Length: <= 32 } || !Regex.IsMatch(request.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+$")) return Failure(PrivilegedProblemCode.InvalidRequest);
            if (Directory.Exists(root) || File.Exists(root)) return Failure(PrivilegedProblemCode.Conflict);
            var parent = Path.GetDirectoryName(root)!;
            ProtectDirectory(parent, policy);
            var staging = Path.Combine(parent, ".nginx-" + Guid.NewGuid().ToString("N"));
            ProtectDirectory(staging, policy);
            try
            {
                var archive = Path.Combine(staging, "official.zip");
                await DownloadAsync($"https://nginx.org/download/nginx-{request.Version}.zip", archive);
                if (request.ArchivePath is not null)
                {
                    var supplied = AllowedArchive(request.ArchivePath, policy);
                    var suppliedHash = await HashAsync(supplied);
                    var officialHash = await HashAsync(archive);
                    if (!CryptographicOperations.FixedTimeEquals(suppliedHash, officialHash)) return Failure(PrivilegedProblemCode.AccessDenied);
                }
                var extracted = Path.Combine(staging, "package");
                ExtractNginx(archive, extracted, request.Version!, policy);
                var package = Path.Combine(extracted, "nginx-" + request.Version);
                ProtectOwnedTree(package, policy);
                await SaveIntegrityAsync(package, ["nginx.exe"]);
                await File.WriteAllTextAsync(Path.Combine(package, ".relaxkonos-managed"), ManagedMarker);
                Directory.Move(package, root);
                return new(true);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        if (request.Version is not null || request.ArchivePath is not null) return Failure(PrivilegedProblemCode.InvalidRequest);
        if (request.Action == WindowsManagedRuntimeAction.Status)
            return new(true, NginxRunning: NginxRunning(root));
        if (!await VerifyIntegrityAsync(root, "nginx.exe")) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        if (request.Action == WindowsManagedRuntimeAction.Uninstall)
        {
            if (NginxRunning(root) && !(await NginxCommandAsync(root, ["-s", "quit"])).Success) return Failure(PrivilegedProblemCode.Conflict);
            for (var i = 0; i < 40 && NginxRunning(root); i++) await Task.Delay(100);
            if (NginxRunning(root)) return Failure(PrivilegedProblemCode.Conflict);
            RequireTreeWithoutLinks(root);
            Directory.Delete(root, recursive: true);
            return new(true);
        }
        foreach (var config in Directory.EnumerateFiles(Path.Combine(root, "conf"), "*.conf", SearchOption.AllDirectories))
        {
            WindowsManagedRuntimePolicy.RequireNoLinks(config);
            ValidateNginxText(await File.ReadAllTextAsync(config), policy);
        }
        return request.Action switch
        {
            WindowsManagedRuntimeAction.Test => await NginxCommandAsync(root, ["-t"]),
            WindowsManagedRuntimeAction.Stop => !NginxRunning(root) ? new(true) : await NginxCommandAsync(root, ["-s", "quit"]),
            WindowsManagedRuntimeAction.Reload => await NginxCommandAsync(root, ["-s", "reload"]),
            WindowsManagedRuntimeAction.Start => await StartNginxAsync(root),
            WindowsManagedRuntimeAction.Restart => await RestartNginxAsync(root),
            _ => Failure(PrivilegedProblemCode.InvalidRequest),
        };
    }

    private static async Task<PrivilegedOperationResult> FrpAsync(WindowsManagedRuntimeRequest request, WindowsManagedRuntimePolicy policy)
    {
        if (request.Runtime == WindowsManagedRuntime.Frpc && request.Action is not (WindowsManagedRuntimeAction.Install or WindowsManagedRuntimeAction.Uninstall)
            && (request.ProfileId is null || request.ProfileId == Guid.Empty)) return Failure(PrivilegedProblemCode.InvalidRequest);
        if (request.Runtime == WindowsManagedRuntime.Frps && request.ProfileId is not null) return Failure(PrivilegedProblemCode.InvalidRequest);
        var key = request.Runtime == WindowsManagedRuntime.Frps ? "frps" : request.ProfileId?.ToString("N") ?? "frpc";
        var releases = Path.Combine(policy.PrivateRoot, "frp", "versions");
        if (request.Action == WindowsManagedRuntimeAction.Install)
        {
            if (request.Runtime != WindowsManagedRuntime.Frpc || request.Client is not null || request.Server is not null || request.ProfileId is not null) return Failure(PrivilegedProblemCode.InvalidRequest);
            var pin = policy.FrpReleases.SingleOrDefault(x => x.Version == request.Version && x.Rid == WindowsManagedRuntimePolicy.Rid);
            if (pin is null) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
            ProtectDirectory(policy.PrivateRoot, policy);
            ProtectDirectory(releases, policy);
            var destination = Path.Combine(releases, pin.Version);
            if (Directory.Exists(destination)) return await VerifyIntegrityAsync(destination, "frpc.exe") && await VerifyIntegrityAsync(destination, "frps.exe") ? new(true) : Failure(PrivilegedProblemCode.ResourceNotAllowed);
            var stage = Path.Combine(releases, ".install-" + Guid.NewGuid().ToString("N"));
            ProtectDirectory(stage, policy);
            try
            {
                var archive = Path.Combine(stage, "release.zip");
                if (request.ArchivePath is { } supplied)
                {
                    await using var input = File.OpenRead(AllowedArchive(supplied, policy));
                    await using var output = CreateProtectedFile(archive);
                    await CopyBoundedAsync(input, output);
                }
                else await DownloadAsync(pin.Url, archive);
                var archiveHash = await HashAsync(archive);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pin.Sha256), archiveHash)) return Failure(PrivilegedProblemCode.AccessDenied);
                var extracted = Path.Combine(stage, "package");
                ProtectDirectory(extracted, policy);
                ExtractFrp(archive, extracted, policy);
                ProtectOwnedTree(extracted, policy);
                await SaveIntegrityAsync(extracted, ["frpc.exe", "frps.exe"]);
                Directory.Move(extracted, destination);
                return new(true);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
        }
        if (request.ArchivePath is not null) return Failure(PrivilegedProblemCode.InvalidRequest);
        if (request.Action == WindowsManagedRuntimeAction.Uninstall)
        {
            if (request.Runtime != WindowsManagedRuntime.Frpc || request.ProfileId is not null || request.Client is not null || request.Server is not null) return Failure(PrivilegedProblemCode.InvalidRequest);
            foreach (var id in Processes.Keys.ToArray()) await StopProcessAsync(id);
            if (Directory.Exists(releases)) { RequireTreeWithoutLinks(releases); Directory.Delete(releases, recursive: true); }
            return new(true);
        }
        if (request.Action == WindowsManagedRuntimeAction.Status)
            return new(true, WindowsProcess: Snapshot(key));
        if (request.Action == WindowsManagedRuntimeAction.Stop)
        {
            await StopProcessAsync(key);
            return new(true, WindowsProcess: Snapshot(key));
        }
        if (request.Action is not (WindowsManagedRuntimeAction.Start or WindowsManagedRuntimeAction.Test)) return Failure(PrivilegedProblemCode.InvalidRequest);
        if (!policy.FrpReleases.Any(x => x.Version == request.Version && x.Rid == WindowsManagedRuntimePolicy.Rid)) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        var release = Path.Combine(releases, request.Version!);
        var binary = request.Runtime == WindowsManagedRuntime.Frpc ? "frpc.exe" : "frps.exe";
        if (!await VerifyIntegrityAsync(release, binary)) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        var config = request.Runtime == WindowsManagedRuntime.Frpc
            ? WindowsRuntimeConfigurationWriter.Client(request.Client, request.Server)
            : WindowsRuntimeConfigurationWriter.Server(request.Server, request.Client);
        var configRoot = Path.Combine(policy.PrivateRoot, "frp", "config", key);
        ProtectDirectory(Path.GetDirectoryName(configRoot)!, policy, privateConfiguration: true);
        ProtectDirectory(configRoot, policy, privateConfiguration: true);
        var candidate = Path.Combine(configRoot, ".candidate.toml");
        await AtomicWriteAsync(candidate, Encoding.UTF8.GetBytes(config));
        var executable = Path.Combine(release, binary);
        var verified = await RunAsync(executable, release, ["verify", "-c", candidate]);
        if (!verified.Success || request.Action == WindowsManagedRuntimeAction.Test) { File.Delete(candidate); return verified; }
        // Verify before stopping the last valid instance; retain its configuration for rollback.
        var active = Path.Combine(configRoot, "active.toml");
        var previous = File.Exists(active) ? await File.ReadAllBytesAsync(active) : null;
        var old = Processes.TryGetValue(key, out var existing) ? existing.Executable : null;
        await StopProcessAsync(key);
        File.Move(candidate, active, overwrite: true);
        if (await StartProcessAsync(key, executable, active)) return new(true, WindowsProcess: Snapshot(key));
        if (previous is not null && old is not null)
        {
            await AtomicWriteAsync(active, previous);
            await StartProcessAsync(key, old, active);
        }
        return Failure(PrivilegedProblemCode.InternalError);
    }

    private static string AllowedArchive(string path, WindowsManagedRuntimePolicy policy)
    {
        if (!Path.IsPathFullyQualified(path) || !policy.ArchiveRoots.Any(root => WindowsManagedRuntimePolicy.Contains(root, path))) throw new UnauthorizedAccessException();
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumArchiveBytes) throw new UnauthorizedAccessException();
        return Path.GetFullPath(path);
    }

    private static async Task DownloadAsync(string url, string destination)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = CreateProtectedFile(destination);
        await CopyBoundedAsync(input, output);
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer);
            if (count == 0) return;
            if ((total += count) > MaximumArchiveBytes) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, count));
        }
    }
    private static void ExtractNginx(string archivePath, string destination, string version, WindowsManagedRuntimePolicy policy)
    {
        using var archive = ZipFile.OpenRead(archivePath); long total = 0;
        foreach (var entry in archive.Entries)
        {
            SafeEntry(entry, ref total);
            if (!entry.FullName.StartsWith("nginx-" + version + "/", StringComparison.Ordinal)) throw new InvalidDataException();
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!WindowsManagedRuntimePolicy.Contains(destination, target)) throw new InvalidDataException();
            if (entry.FullName.EndsWith('/')) { ProtectDirectory(target, policy); continue; }
            if (Regex.IsMatch(target, "\\.(exe|dll|cmd|bat|ps1)$", RegexOptions.IgnoreCase)
                && entry.FullName != $"nginx-{version}/nginx.exe") throw new InvalidDataException();
            ProtectDirectory(Path.GetDirectoryName(target)!, policy);
            using var input = entry.Open();
            using var output = CreateProtectedFile(target);
            input.CopyTo(output);
        }
        if (!File.Exists(Path.Combine(destination, "nginx-" + version, "nginx.exe"))
            || !File.Exists(Path.Combine(destination, "nginx-" + version, "conf", "nginx.conf"))) throw new InvalidDataException();
    }
    private static void ExtractFrp(string archivePath, string destination, WindowsManagedRuntimePolicy policy)
    {
        using var archive = ZipFile.OpenRead(archivePath); long total = 0; var found = new HashSet<string>();
        foreach (var entry in archive.Entries)
        {
            SafeEntry(entry, ref total);
            var leaf = Path.GetFileName(entry.FullName);
            if (leaf is not ("frpc.exe" or "frps.exe")) continue;
            if (!found.Add(leaf)) throw new InvalidDataException();
            using var input = entry.Open();
            using var output = CreateProtectedFile(Path.Combine(destination, leaf));
            input.CopyTo(output);
        }
        if (found.Count != 2) throw new InvalidDataException();
    }
    private static void SafeEntry(ZipArchiveEntry entry, ref long total)
    {
        if (entry.FullName.Length == 0 || entry.FullName.Contains('\\') || entry.FullName.Contains(':')
            || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Any(x => x is "." or "..")
            || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000
            || entry.Length < 0 || (total += entry.Length) > MaximumArchiveBytes) throw new InvalidDataException();
    }
    private static async Task SaveIntegrityAsync(string root, string[] binaries)
    {
        var hashes = new Dictionary<string, string>();
        foreach (var binary in binaries) hashes[binary] = Convert.ToHexString(await HashAsync(Path.Combine(root, binary)));
        await AtomicWriteAsync(Path.Combine(root, IntegrityFile), JsonSerializer.SerializeToUtf8Bytes(hashes));
    }
    private static async Task<bool> VerifyIntegrityAsync(string root, string binary)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(root);
        var manifest = Path.Combine(root, IntegrityFile); var executable = Path.Combine(root, binary);
        if (!File.Exists(manifest) || !File.Exists(executable)) return false;
        WindowsManagedRuntimePolicy.RequireNoLinks(manifest); WindowsManagedRuntimePolicy.RequireNoLinks(executable);
        RequireProtectedPath(root, directory: true);
        RequireProtectedPath(manifest, directory: false);
        RequireProtectedPath(executable, directory: false);
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(manifest));
        if (hashes is null || !hashes.TryGetValue(binary, out var expected)) return false;
        var actual = await HashAsync(executable);
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual);
    }
    private static async Task<byte[]> HashAsync(string path)
    { await using var input = File.OpenRead(path); return await SHA256.HashDataAsync(input); }
    private static void ProtectDirectory(string path, WindowsManagedRuntimePolicy policy, bool privateConfiguration = false)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        var parentDirectory = Path.GetDirectoryName(path);
        if (parentDirectory is not null && !Directory.Exists(parentDirectory)) ProtectDirectory(parentDirectory, policy, privateConfiguration);
        for (var parent = Path.GetDirectoryName(path); parent is not null
            && (WindowsManagedRuntimePolicy.Contains(policy.PrivateRoot, parent) || WindowsManagedRuntimePolicy.Contains(policy.NginxRoot, parent));
            parent = Path.GetDirectoryName(parent))
            ApplyDirectoryProtection(parent, policy, privateConfiguration
                && WindowsManagedRuntimePolicy.Contains(Path.Combine(policy.PrivateRoot, "frp", "config"), parent));
        ApplyDirectoryProtection(path, policy, privateConfiguration);
    }
    private static void ApplyDirectoryProtection(string path, WindowsManagedRuntimePolicy policy, bool privateConfiguration)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), administrators })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!privateConfiguration)
            foreach (var sid in policy.ReaderSids.Distinct())
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (directory.Exists) directory.SetAccessControl(security);
        else directory.Create(security);
    }
    private static void ProtectOwnedTree(string root, WindowsManagedRuntimePolicy policy)
    {
        ProtectDirectory(root, policy);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            ProtectDirectory(path, policy);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            SetAdministratorFileOwner(path);
    }
    private static void SetAdministratorFileOwner(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        file.SetAccessControl(security);
    }
    private static FileStream CreateProtectedFile(string path)
    {
        var security = new FileSecurity();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), administrators })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 81920, FileOptions.None, security);
    }
    private static void RequireProtectedPath(string path, bool directory)
    {
        FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!administrators.Equals(owner) && !system.Equals(owner)) throw new UnauthorizedAccessException();
        const FileSystemRights writable = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writable) != 0
                && !administrators.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new UnauthorizedAccessException();
    }
    private static void RequireTreeWithoutLinks(string root)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) WindowsManagedRuntimePolicy.RequireNoLinks(path);
    }
    private static async Task AtomicWriteAsync(string path, byte[] bytes)
    {
        WindowsManagedRuntimePolicy.RequireNoLinks(path);
        var stage = path + ".helper-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = CreateProtectedFile(stage)) await output.WriteAsync(bytes);
            File.Move(stage, path, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    private static async Task<PrivilegedOperationResult> NginxCommandAsync(string root, string[] arguments)
        => await RunAsync(Path.Combine(root, "nginx.exe"), root, new[] { "-p", root.Replace('\\', '/') + "/", "-c", "conf/nginx.conf" }.Concat(arguments).ToArray());
    private static async Task<PrivilegedOperationResult> StartNginxAsync(string root)
    {
        if (NginxRunning(root)) return new(true);
        var test = await NginxCommandAsync(root, ["-t"]);
        if (!test.Success) return test;
        var process = new Process { StartInfo = StartInfo(Path.Combine(root, "nginx.exe"), root, ["-p", root.Replace('\\', '/') + "/", "-c", "conf/nginx.conf"], redirect: false) };
        try
        {
            if (!process.Start()) return Failure(PrivilegedProblemCode.InternalError);
            for (var i = 0; i < 50; i++) { if (NginxRunning(root)) return new(true); await Task.Delay(100); }
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            return Failure(PrivilegedProblemCode.InternalError);
        }
        finally { process.Dispose(); }
    }
    private static async Task<PrivilegedOperationResult> RestartNginxAsync(string root)
    {
        if (NginxRunning(root))
        {
            var stopped = await NginxCommandAsync(root, ["-s", "quit"]);
            if (!stopped.Success) return stopped;
            for (var i = 0; i < 50 && NginxRunning(root); i++) await Task.Delay(100);
            if (NginxRunning(root)) return Failure(PrivilegedProblemCode.Conflict);
        }
        return await StartNginxAsync(root);
    }
    private static bool NginxRunning(string root)
    {
        try
        {
            var pidFile = Path.Combine(root, "logs", "nginx.pid"); WindowsManagedRuntimePolicy.RequireNoLinks(pidFile);
            if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid)) return false;
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.MainModule?.FileName, Path.Combine(root, "nginx.exe"), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static ProcessStartInfo StartInfo(string executable, string directory, string[] arguments, bool redirect = true)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = redirect, RedirectStandardError = redirect };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
    private static async Task<PrivilegedOperationResult> RunAsync(string executable, string directory, string[] arguments)
    {
        using var process = new Process { StartInfo = StartInfo(executable, directory, arguments) };
        if (!process.Start()) return Failure(PrivilegedProblemCode.InternalError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Drain output without storing it: privileged diagnostics may include configuration secrets.
        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try { await process.WaitForExitAsync(deadline.Token); await Task.WhenAll(output, error); return process.ExitCode == 0 ? new(true) : Failure(PrivilegedProblemCode.InvalidRequest); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); return Failure(PrivilegedProblemCode.TimedOut); }
    }
    private static async Task<bool> StartProcessAsync(string key, string executable, string configuration)
    {
        var process = new Process { StartInfo = StartInfo(executable, Path.GetDirectoryName(executable)!, ["-c", configuration]) };
        var owned = new OwnedProcess(process, executable);
        process.OutputDataReceived += (_, args) => owned.Log(args.Data);
        process.ErrorDataReceived += (_, args) => owned.Log(args.Data);
        if (!process.Start()) { process.Dispose(); return false; }
        owned.StartedAt = DateTimeOffset.UtcNow;
        Processes[key] = owned;
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await Task.Delay(250);
        if (!process.HasExited) return true;
        await StopProcessAsync(key);
        return false;
    }
    private static async Task StopProcessAsync(string key)
    {
        if (!Processes.TryRemove(key, out var owned)) return;
        try { if (!owned.Process.HasExited) { owned.Process.Kill(entireProcessTree: true); await owned.Process.WaitForExitAsync(); } }
        finally { owned.Process.Dispose(); }
    }
    private static WindowsManagedProcessSnapshot Snapshot(string key)
    {
        return Processes.TryGetValue(key, out var process)
            ? new(!process.Process.HasExited, process.Connected && !process.Process.HasExited, process.AuthenticationFailed, process.StartedAt, process.Logs.ToArray())
            : new(false, false, false, null, []);
    }
    public static async Task StopForHelperShutdownAsync()
    {
        _stopping = true;
        await Gate.WaitAsync();
        try
        {
            foreach (var key in Processes.Keys.ToArray()) await StopProcessAsync(key);
            if (_lastPolicy is { } policy && NginxRunning(policy.NginxRoot)
                && await VerifyIntegrityAsync(policy.NginxRoot, "nginx.exe")) await NginxCommandAsync(policy.NginxRoot, ["-s", "quit"]);
        }
        finally { Gate.Release(); }
    }
    internal static void ValidateNginxText(string text, WindowsManagedRuntimePolicy policy)
    {
        WindowsRuntimeConfigurationWriter.ValidateNginx(text, policy.NginxRoot);
    }
    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
    private sealed class OwnedProcess(Process process, string executable)
    {
        public Process Process { get; } = process;
        public string Executable { get; } = executable;
        public DateTimeOffset? StartedAt { get; set; }
        public volatile bool Connected;
        public volatile bool AuthenticationFailed;
        public ConcurrentQueue<TunnelLogEntryDto> Logs { get; } = new();
        public void Log(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (raw.Contains("login to server success", StringComparison.OrdinalIgnoreCase)) Connected = true;
            if (raw.Contains("login to server failed", StringComparison.OrdinalIgnoreCase) || raw.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)) { AuthenticationFailed = true; Connected = false; }
            // Only fixed summaries cross the privileged boundary; no raw token-bearing FRP output.
            var message = AuthenticationFailed ? "FRP authentication failed." : Connected ? "FRP connected." : "FRP runtime activity.";
            Logs.Enqueue(new(DateTimeOffset.UtcNow, "information", message));
            while (Logs.Count > 200) Logs.TryDequeue(out _);
        }
    }
}
