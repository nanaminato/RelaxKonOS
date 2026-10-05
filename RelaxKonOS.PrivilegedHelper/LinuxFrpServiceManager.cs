using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed, independently owned systemd FRP instances. No caller-supplied command or unit path.</summary>
[SupportedOSPlatform("linux")]
internal static class LinuxFrpServiceManager
{
    private const string Root = "/var/lib/relaxkonos-components/frp";
    private const string Account = "relaxkonos-frp";
    private const string Version = "v0.71.0";
    private sealed record Instance(string Version, string AppliedIdentity);
    internal static string Key(ManagedRuntimeRequest request) => request.Runtime switch {
        ManagedRuntime.Frps when request.ProfileId is null => "frps",
        ManagedRuntime.Frpc when request.ProfileId is { } id && id != Guid.Empty => "frpc-" + id.ToString("N"),
        _ => throw new ArgumentException("Invalid independent FRP identity.")
    };
    private static string Unit(string key) => "relaxkonos-" + key + ".service";
    private static string DirectoryFor(string key) => Path.Combine(Root, "instances", key);
    private static string Executable(string version, bool server) => Path.Combine(Root, "versions", version, server ? "frps" : "frpc");
    private static string UnitText(string key, Instance instance) =>
        $"# RelaxKonOS independent FRP service\n[Unit]\nDescription=RelaxKonOS {key}\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\nType=simple\nUser={Account}\nGroup={Account}\nExecStart={Executable(instance.Version, key == "frps")} -c {DirectoryFor(key)}/active.toml\nRestart=on-failure\nRestartSec=3\nNoNewPrivileges=true\nPrivateTmp=true\nProtectSystem=strict\nProtectHome=true\nReadWritePaths={DirectoryFor(key)}\nAmbientCapabilities=CAP_NET_BIND_SERVICE\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE\n\n[Install]\nWantedBy=multi-user.target\n";

    public static async Task<PrivilegedOperationResult> ExecuteAsync(ManagedRuntimeRequest? request)
    {
        if (!OperatingSystem.IsLinux() || request is null || request.Runtime == ManagedRuntime.Nginx)
            return new(false, ProblemCode: PrivilegedProblemCode.UnsupportedOperation);
        if (!WindowsRuntimeConfigurationWriter.IsValidRequest(request)) return new(false, ProblemCode: PrivilegedProblemCode.InvalidRequest);
        try
        {
            if (request.Action == ManagedRuntimeAction.Install) { await InstallAsync(request); return new(true); }
            if (request.Action == ManagedRuntimeAction.Uninstall) { await RemoveAsync(); return new(true); }
            var key = Key(request);
            var directory = DirectoryFor(key);
            var metadata = Path.Combine(directory, "instance.json");
            var unitPath = Path.Combine("/etc/systemd/system", Unit(key));
            Instance? previous = File.Exists(metadata) ? JsonSerializer.Deserialize<Instance>(await File.ReadAllTextAsync(metadata)) : null;
            if (previous is not null && (!File.Exists(unitPath) || await File.ReadAllTextAsync(unitPath) != UnitText(key, previous)))
                return new(false, ProblemCode: PrivilegedProblemCode.Conflict);
            if (previous is null && File.Exists(unitPath)) return new(false, ProblemCode: PrivilegedProblemCode.Conflict);
            if (request.Action == ManagedRuntimeAction.Status)
                return new(true, ComponentProcess: previous is null ? new(false, false, false, null, []) : await SnapshotAsync(key, previous));
            if (request.Action == ManagedRuntimeAction.Stop)
            {
                if (previous is not null) await RunAsync("/usr/bin/systemctl", ["stop", Unit(key)]);
                return new(true, ComponentProcess: new(false, false, false, null, []));
            }
            if (request.Action is not (ManagedRuntimeAction.Start or ManagedRuntimeAction.Test) || request.Version != Version)
                return new(false, ProblemCode: PrivilegedProblemCode.InvalidRequest);
            var configuration = request.Runtime == ManagedRuntime.Frpc
                ? WindowsRuntimeConfigurationWriter.Client(request.Client, request.Server)
                : WindowsRuntimeConfigurationWriter.Server(request.Server, request.Client);
            if (!File.Exists(Executable(request.Version, request.Runtime == ManagedRuntime.Frps))) return new(false, ProblemCode: PrivilegedProblemCode.NotFound);
            EnsurePlain(directory); Directory.CreateDirectory(directory);
            await RunAsync("/usr/bin/chown", [$"root:{Account}", directory]);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            var candidate = Path.Combine(directory, "candidate.toml");
            await WritePrivateAsync(candidate, configuration);
            await RunAsync("/usr/sbin/runuser", ["-u", Account, "--", Executable(request.Version, request.Runtime == ManagedRuntime.Frps), "verify", "-c", candidate]);
            if (request.Action == ManagedRuntimeAction.Test) { File.Delete(candidate); return new(true); }
            var active = Path.Combine(directory, "active.toml");
            var old = File.Exists(active) ? await File.ReadAllTextAsync(active) : null;
            if (previous is not null) await RunAsync("/usr/bin/systemctl", ["stop", Unit(key)]);
            File.Move(candidate, active, true);
            var instance = new Instance(request.Version, request.AppliedIdentity!);
            try
            {
                EnsurePlain(unitPath);
                await File.WriteAllTextAsync(unitPath, UnitText(key, instance));
                await WritePrivateAsync(metadata, JsonSerializer.Serialize(instance));
                await RunAsync("/usr/bin/systemctl", ["daemon-reload"]);
                await RunAsync("/usr/bin/systemctl", ["enable", "--now", Unit(key)]);
                await Task.Delay(250);
                var snapshot = await SnapshotAsync(key, instance);
                if (!snapshot.Running) throw new IOException("FRP service failed its startup check.");
                return new(true, ComponentProcess: snapshot);
            }
            catch
            {
                await RunAsync("/usr/bin/systemctl", ["stop", Unit(key)], requireSuccess: false);
                if (previous is not null && old is not null)
                {
                    await WritePrivateAsync(active, old); await WritePrivateAsync(metadata, JsonSerializer.Serialize(previous));
                    await File.WriteAllTextAsync(unitPath, UnitText(key, previous));
                    await RunAsync("/usr/bin/systemctl", ["daemon-reload"]); await RunAsync("/usr/bin/systemctl", ["start", Unit(key)]);
                }
                throw;
            }
        }
        catch (UnauthorizedAccessException) { return new(false, ProblemCode: PrivilegedProblemCode.ResourceNotAllowed); }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or InvalidOperationException)
        { return new(false, ProblemCode: PrivilegedProblemCode.Conflict); }
    }

    private static async Task InstallAsync(ManagedRuntimeRequest request)
    {
        if (request.Version != Version) throw new ArgumentException("Unconfigured FRP release.");
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => throw new InvalidOperationException() };
        var hash = architecture == "amd64" ? "84f27e39f11169f7adcef8e8b70c9329de17747b1f14dad9fb95eef5682ea716" : "f33c293c275d8fc68c654b6fba8f10b2551d6463d09a9fc9cffb7227eae82266";
        var destination = Path.Combine(Root, "versions", Version);
        EnsurePlain(destination);
        if (File.Exists(Path.Combine(destination, ".verified-package")) && await File.ReadAllTextAsync(Path.Combine(destination, ".verified-package")) == hash) return;
        if (!File.ReadLines("/etc/passwd").Any(line => line.StartsWith(Account + ":", StringComparison.Ordinal)))
            await RunAsync("/usr/sbin/useradd", ["--system", "--user-group", "--home-dir", Root, "--shell", "/usr/sbin/nologin", Account]);
        var account = File.ReadLines("/etc/passwd").Single(line => line.StartsWith(Account + ":", StringComparison.Ordinal)).Split(':');
        if (!int.TryParse(account[2], out var uid) || uid <= 0 || uid >= 1000
            || !int.TryParse(account[3], out var groupId) || groupId <= 0
            || !account[6].EndsWith("/nologin", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        foreach (var directory in new[] { Path.GetDirectoryName(Root)!, Root, Path.Combine(Root, "versions"), Path.Combine(Root, "instances") })
        {
            EnsurePlain(directory); Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var archive = destination + ".tar.gz";
        if (request.ArchivePath is { } source)
        {
            var allowedRoot = (await File.ReadAllTextAsync("/etc/relaxkonos/frp-archive-root")).Trim();
            var canonical = (await RunAsync("/usr/bin/realpath", ["-e", source])).Trim();
            if (Path.GetDirectoryName(canonical) != allowedRoot || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(canonical), "^\\.archive-[a-f0-9]{32}$")) throw new UnauthorizedAccessException();
            if (new FileInfo(canonical).Length > 128L * 1024 * 1024) throw new UnauthorizedAccessException();
            File.Copy(canonical, archive, true);
        }
        else
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            await using var input = await client.GetStreamAsync($"https://github.com/fatedier/frp/releases/download/{Version}/frp_{Version[1..]}_linux_{architecture}.tar.gz");
            await using var output = File.Create(archive);
            var buffer = new byte[81920]; long total = 0;
            for (int read; (read = await input.ReadAsync(buffer)) != 0;) { if ((total += read) > 128L * 1024 * 1024) throw new IOException(); await output.WriteAsync(buffer.AsMemory(0, read)); }
        }
        try
        {
            await using var input = File.OpenRead(archive);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
            input.Position = 0;
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            Directory.CreateDirectory(destination);
            for (TarEntry? entry; (entry = tar.GetNextEntry()) is not null;)
            {
                if (entry.Name != $"frp_{Version[1..]}_linux_{architecture}/frpc" && entry.Name != $"frp_{Version[1..]}_linux_{architecture}/frps") continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null || entry.Length > 128L * 1024 * 1024) throw new IOException();
                var path = Path.Combine(destination, Path.GetFileName(entry.Name));
                EnsurePlain(path);
                await using (var output = File.Create(path)) await entry.DataStream.CopyToAsync(output);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            if (!File.Exists(Path.Combine(destination, "frpc")) || !File.Exists(Path.Combine(destination, "frps"))) throw new IOException();
            await File.WriteAllTextAsync(Path.Combine(destination, ".verified-package"), hash);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }
    private static async Task<ManagedProcessSnapshot> SnapshotAsync(string key, Instance instance)
    {
        var state = await RunAsync("/usr/bin/systemctl", ["show", Unit(key), "--property=ActiveState,MainPID,ActiveEnterTimestamp"]);
        var fields = state.Split('\n').Where(line => line.Contains('=')).ToDictionary(line => line[..line.IndexOf('=')], line => line[(line.IndexOf('=') + 1)..]);
        int.TryParse(fields.GetValueOrDefault("MainPID"), out var pid);
        var running = fields.GetValueOrDefault("ActiveState") == "active" && pid > 0;
        DateTimeOffset? started = null;
        if (running)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                running = !process.HasExited && new FileInfo($"/proc/{pid}/exe").ResolveLinkTarget(true)?.FullName == Executable(instance.Version, key == "frps");
                if (running) started = process.StartTime.ToUniversalTime();
            }
            catch (ArgumentException) { running = false; }
        }
        var log = await RunAsync("/usr/bin/journalctl", ["--unit", Unit(key), "--lines=200", "--output=cat", "--no-pager", "--since", started?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "now"], requireSuccess: false);
        var connected = log.Contains("login to server success", StringComparison.OrdinalIgnoreCase);
        var failed = log.Contains("login to server failed", StringComparison.OrdinalIgnoreCase) || log.Contains("authentication failed", StringComparison.OrdinalIgnoreCase);
        return new(running, running && connected && !failed, failed, started,
            log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(_ => new TunnelLogEntryDto(DateTimeOffset.UtcNow, "information", failed ? "FRP authentication failed." : connected ? "FRP connected." : "FRP runtime activity.")).ToArray(), running ? instance.AppliedIdentity : null);
    }
    private static async Task RemoveAsync()
    {
        var root = Path.Combine(Root, "instances");
        if (Directory.Exists(root)) foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var key = Path.GetFileName(directory);
            if (key != "frps" && !System.Text.RegularExpressions.Regex.IsMatch(key, "^frpc-[a-f0-9]{32}$")) throw new UnauthorizedAccessException();
            var instance = JsonSerializer.Deserialize<Instance>(await File.ReadAllTextAsync(Path.Combine(directory, "instance.json"))) ?? throw new IOException();
            var path = Path.Combine("/etc/systemd/system", Unit(key));
            if (await File.ReadAllTextAsync(path) != UnitText(key, instance)) throw new UnauthorizedAccessException();
            await RunAsync("/usr/bin/systemctl", ["disable", "--now", Unit(key)]); File.Delete(path);
        }
        await RunAsync("/usr/bin/systemctl", ["daemon-reload"]);
        EnsurePlain(Root);
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
    private static async Task WritePrivateAsync(string path, string content)
    {
        EnsurePlain(path); await File.WriteAllTextAsync(path, content);
        await RunAsync("/usr/bin/chown", [$"root:{Account}", path]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }
    private static void EnsurePlain(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException();
    }
    private static async Task<string> RunAsync(string executable, string[] arguments, bool requireSuccess = true)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new IOException("FRP service operation timed out."); }
        await error;
        if (requireSuccess && process.ExitCode != 0) throw new IOException("FRP service operation failed.");
        return await output;
    }
}
