using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed paths, pinned archive, closed generator, no caller-supplied executable or native config.</summary>
internal static class SingBoxOperations
{
    private static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS-Components", "sing-box")
        : "/var/lib/relaxkonos/proxy-server";
    private static string Binary => Path.Combine(Root, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
    private static string Config => Path.Combine(Root, "config.json");
    private static string Pending => Path.Combine(Root, "pending.json");
    private const string Unit = "relaxkonos-sing-box.service";
    private const string UnitPath = "/etc/systemd/system/" + Unit;
    private sealed record Publication(string Hash, bool WasRunning);
    private sealed record Installed(string Version, string BinaryHash);
    private static readonly SemaphoreSlim Gate = new(1);

    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version, ProxyServer: request.ProxyServer);
        if (clean != request || request.Operation is PrivilegedOperationKind.ProxyServerStatus or PrivilegedOperationKind.ProxyServerGenerateKeys && request.ProxyServer is not null)
            return Failure("proxy_server.request_invalid");
        if (SingBoxManifest.Current is null) return request.Operation == PrivilegedOperationKind.ProxyServerStatus
            ? Json(new ProxyServerStatus(false, false, false, false, null, null, false, "proxy_server.platform_unsupported"))
            : Failure("proxy_server.platform_unsupported");
        await Gate.WaitAsync();
        try
        {
            if (request.Operation == PrivilegedOperationKind.ProxyServerStatus && !Directory.Exists(Root))
                return Json(new ProxyServerStatus(true, false, false, false, null, null, false, null));
            PrepareRoot();
            // Also serializes Linux one-shot Helper processes.
            using var gate = new FileStream(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (request.Operation == PrivilegedOperationKind.ProxyServerStatus) return Json(await StatusAsync());
            if (request.Operation == PrivilegedOperationKind.ProxyServerAction)
            {
                if (request.ProxyServer is not { Action: { } action, Material: null } || !Enum.IsDefined(action)) return Failure("proxy_server.request_invalid");
                if (action == ProxyServerAction.Install) { await InstallAsync(); return Json(await StatusAsync()); }
                if (action == ProxyServerAction.Recover) { await RecoverAsync(); return Json(await StatusAsync()); }
                if (action == ProxyServerAction.Stop) { await StopAsync(); return Json(await StatusAsync()); }
                RequireInstalled();
                if (File.Exists(Pending)) return Failure("proxy_server.recovery_required");
                if (action is ProxyServerAction.Start or ProxyServerAction.Restart)
                {
                    if (!File.Exists(Config)) return Failure("proxy_server.configuration_missing");
                    await CheckAsync(Config);
                    if (action == ProxyServerAction.Restart) await StopAsync();
                    await StartAsync();
                }
                else if (OperatingSystem.IsWindows()) await WindowsComponentService.SetStartupAsync(ComponentKind.SingBox, Root, action == ProxyServerAction.Enable);
                else await SystemctlAsync(action == ProxyServerAction.Enable ? "enable" : "disable");
                return Json(await StatusAsync());
            }
            RequireInstalled();
            if (request.Operation == PrivilegedOperationKind.ProxyServerGenerateKeys)
            {
                var output = await RunAsync(Binary, ["generate", "reality-keypair"]);
                var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var privateKey = lines.Single(l => l.StartsWith("PrivateKey: ", StringComparison.Ordinal))[12..];
                var publicKey = lines.Single(l => l.StartsWith("PublicKey: ", StringComparison.Ordinal))[11..];
                if (!SingBoxConfiguration.Key(privateKey) || !SingBoxConfiguration.Key(publicKey)) return Failure("proxy_server.keys_invalid");
                return Json(new ProxyServerRealityKey(Guid.Empty, privateKey, publicKey, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()));
            }
            if (request.ProxyServer is not { Action: null, Material: { } material }) return Failure("proxy_server.request_invalid");
            var rendered = SingBoxConfiguration.Render(material);
            foreach (var inbound in material.Configuration.Inbounds.Where(i => i.Enabled && i.Protocol == ProxyServerProtocol.VlessReality))
            {
                var addresses = await Dns.GetHostAddressesAsync(inbound.HandshakeHost).WaitAsync(TimeSpan.FromSeconds(10));
                if (addresses.Length == 0 || addresses.Any(a => !SingBoxConfiguration.PublicAddress(a))) return Failure("proxy_server.handshake_address_forbidden");
            }
            var candidate = Path.Combine(Root, "candidate.json");
            await WriteAsync(candidate, rendered);
            try
            {
                await CheckAsync(candidate);
                if (request.Operation == PrivilegedOperationKind.ProxyServerValidate) return Json(new ProxyServerValidation(true, []));
                if (File.Exists(Pending)) return Failure("proxy_server.recovery_required");
                var status = await StatusAsync();
                if (status.ProblemCode == "proxy_server.configuration_drift") return Failure(status.ProblemCode);
                var previous = Config + ".previous";
                if (File.Exists(Config)) File.Copy(Config, previous, true); else File.Delete(previous);
                var publication = new Publication(Hash(rendered), status.Running);
                await WriteAsync(Pending, JsonSerializer.Serialize(publication));
                try
                {
                    if (status.Running) await StopAsync();
                    File.Move(candidate, Config, true);
                    if (OperatingSystem.IsWindows())
                        await WindowsComponentService.InstallAsync(ComponentKind.SingBox, Root, Binary, Config, appliedIdentity: publication.Hash);
                    if (status.Running) await StartAsync();
                    await WriteAsync(Path.Combine(Root, "published.txt"), publication.Hash);
                    File.Delete(Pending);
                    return Json(await StatusAsync());
                }
                catch
                {
                    try { await RecoverAsync(); }
                    catch { return Failure("proxy_server.recovery_required"); }
                    return Failure("proxy_server.config_apply_failed");
                }
            }
            finally { File.Delete(candidate); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or JsonException or HttpRequestException or TimeoutException or System.ComponentModel.Win32Exception)
        { return Failure("proxy_server.runtime_failed"); }
        finally { Gate.Release(); }
    }

    private static void PrepareRoot()
    {
        NoLinks(Root);
        if (OperatingSystem.IsWindows()) WindowsComponentService.ProtectDirectory(Root);
        else
        {
            Directory.CreateDirectory(Root);
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(Root)) NoLinks(entry);
    }

    private static void NoLinks(string path)
    {
        for (var entry = new FileInfo(Path.GetFullPath(path)); entry is not null; entry = entry.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.Exists(entry.FullName) || Directory.Exists(entry.FullName)) && (File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked proxy-server path.");
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static async Task WriteAsync(string path, string text)
    {
        var temporary = path + ".new";
        NoLinks(path); NoLinks(temporary);
        var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var output = new FileStream(temporary, options))
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(text));
            output.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static void RequireInstalled()
    {
        var installed = JsonSerializer.Deserialize<Installed>(File.ReadAllText(Path.Combine(Root, "installed.json"))) ?? throw new IOException();
        if (installed.Version != SingBoxManifest.Version || !File.Exists(Binary)
            || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Binary))) != installed.BinaryHash) throw new IOException("Runtime integrity failure.");
    }

    private static async Task InstallAsync()
    {
        if (File.Exists(Binary)) { RequireInstalled(); return; }
        var release = SingBoxManifest.Current!;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(4) };
        using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        var archive = Path.Combine(Root, "archive.download");
        try
        {
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer)) != 0)
                {
                    if ((total += read) > SingBoxManifest.MaximumArchiveBytes) throw new IOException("Archive exceeds limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            if (!Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Archive integrity failure.");
            var temporary = Binary + ".new";
            File.Delete(temporary);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var zip = ZipFile.OpenRead(archive);
                    var entry = zip.Entries.Single(e => e.FullName == $"sing-box-{SingBoxManifest.Version}-windows-{release.Architecture}/sing-box.exe");
                    if (entry.Length > SingBoxManifest.MaximumArchiveBytes) throw new IOException();
                    await using var binaryInput = entry.Open();
                    await CopyBinaryAsync(binaryInput, temporary);
                }
                else
                {
                    await using var file = File.OpenRead(archive);
                    await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    using var tar = new TarReader(gzip);
                    TarEntry? entry; var found = false;
                    while ((entry = await tar.GetNextEntryAsync()) is not null)
                    {
                        if (entry.Name != $"sing-box-{SingBoxManifest.Version}-linux-{release.Architecture}/sing-box") continue;
                        if (found || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) throw new IOException();
                        await CopyBinaryAsync(entry.DataStream, temporary); found = true;
                    }
                    if (!found) throw new IOException("Missing runtime.");
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                var version = await RunAsync(temporary, ["version"]);
                if (!version.StartsWith("sing-box version " + SingBoxManifest.Version + "\n", StringComparison.Ordinal)
                    && !version.StartsWith("sing-box version " + SingBoxManifest.Version + "\r\n", StringComparison.Ordinal)) throw new IOException("Unexpected runtime version.");
                File.Move(temporary, Binary);
                await WriteAsync(Path.Combine(Root, "installed.json"), JsonSerializer.Serialize(new Installed(SingBoxManifest.Version, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Binary))))));
                if (OperatingSystem.IsLinux()) await InstallLinuxUnitAsync();
            }
            finally { File.Delete(temporary); }
        }
        finally { File.Delete(archive); }
    }

    private static async Task CopyBinaryAsync(Stream input, string path)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[65536]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer)) != 0)
        {
            if ((total += read) > SingBoxManifest.MaximumArchiveBytes) throw new IOException();
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task InstallLinuxUnitAsync()
    {
        NoLinks(UnitPath);
        if (File.Exists(UnitPath) && !File.ReadAllText(UnitPath).StartsWith("# RelaxKonOS proxy-server\n", StringComparison.Ordinal)) throw new IOException("Unowned service.");
        // DynamicUser supplies a dedicated unprivileged identity; systemd loads the private configuration as a credential.
        var unit = $"# RelaxKonOS proxy-server\n[Unit]\nDescription=RelaxKonOS sing-box proxy server\nAfter=network-online.target\n[Service]\nDynamicUser=yes\nLoadCredential=config.json:{Config}\nExecStart={Binary} run -c %d/config.json\nRestart=on-failure\nNoNewPrivileges=yes\nPrivateTmp=yes\nProtectSystem=strict\nProtectHome=yes\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX\n[Install]\nWantedBy=multi-user.target\n";
        // The binary must be traversable by the DynamicUser; secrets stay 0600 and are loaded by PID 1.
        File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        await WriteAsync(UnitPath, unit);
        File.SetUnixFileMode(UnitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await RunAsync("/usr/bin/systemctl", ["daemon-reload"]);
    }

    private static async Task<ProxyServerStatus> StatusAsync()
    {
        var installed = File.Exists(Binary) && File.Exists(Path.Combine(Root, "installed.json"));
        if (installed) RequireInstalled();
        var running = installed && (OperatingSystem.IsWindows()
            ? WindowsComponentService.Snapshot(ComponentKind.SingBox, Root).Running
            : await IsLinuxRunningAsync());
        var publishedPath = Path.Combine(Root, "published.txt");
        var hash = File.Exists(publishedPath) ? (await File.ReadAllTextAsync(publishedPath)).Trim() : null;
        var drift = hash is not null && (!File.Exists(Config) || Hash(await File.ReadAllTextAsync(Config)) != hash);
        return new(true, installed, running, File.Exists(Pending), installed ? SingBoxManifest.Version : null,
            hash, running && !drift && !File.Exists(Pending), drift ? "proxy_server.configuration_drift" : null);
    }

    private static async Task<bool> IsLinuxRunningAsync()
    {
        try { return (await RunAsync("/usr/bin/systemctl", ["is-active", Unit])).Trim() == "active"; }
        catch (InvalidOperationException) { return false; }
    }
    private static Task CheckAsync(string path) => RunAsync(Binary, ["check", "-c", path]);
    private static Task SystemctlAsync(string action) => RunAsync("/usr/bin/systemctl", [action, Unit]);
    private static async Task StopAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            if (WindowsComponentService.Snapshot(ComponentKind.SingBox, Root).Running) await WindowsComponentService.StopAsync(ComponentKind.SingBox, Root);
        }
        else if (await IsLinuxRunningAsync()) await SystemctlAsync("stop");
    }
    private static async Task StartAsync()
    {
        if (OperatingSystem.IsWindows()) await WindowsComponentService.StartAsync(ComponentKind.SingBox, Root);
        else
        {
            await InstallLinuxUnitAsync();
            await SystemctlAsync("start");
            await Task.Delay(1500);
            if (!await IsLinuxRunningAsync()) throw new IOException("Service did not remain running.");
        }
    }

    private static async Task RecoverAsync()
    {
        if (!File.Exists(Pending)) return;
        var publication = JsonSerializer.Deserialize<Publication>(await File.ReadAllTextAsync(Pending)) ?? throw new IOException();
        await StopAsync();
        if (File.Exists(Config + ".previous"))
        {
            File.Copy(Config + ".previous", Config, true);
            await CheckAsync(Config);
            if (OperatingSystem.IsWindows()) await WindowsComponentService.InstallAsync(ComponentKind.SingBox, Root, Binary, Config, appliedIdentity: Hash(await File.ReadAllTextAsync(Config)));
            if (publication.WasRunning) await StartAsync();
        }
        else File.Delete(Config);
        File.Delete(Pending);
    }

    private static async Task<string> RunAsync(string binary, string[] arguments)
    {
        using var process = new Process { StartInfo = new(binary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) process.StartInfo.Environment.Remove(variable);
        if (!process.Start()) throw new IOException();
        var output = ReadBoundedAsync(process.StandardOutput);
        var errors = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        await errors;
        var text = await output;
        // Native diagnostics can contain secrets: never cross the privilege boundary.
        if (process.ExitCode != 0) throw new InvalidOperationException("sing-box operation failed.");
        return text;
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 8192) result.Append(buffer, 0, Math.Min(read, 8192 - result.Length));
        return result.ToString();
    }
    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static PrivilegedOperationResult Failure(string code) => new(false, ExitCode: 1, ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: code);
}
