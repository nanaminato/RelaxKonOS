using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed paths, pinned archive, closed generator, no caller-supplied executable or native config.</summary>
internal static class SingBoxOperations
{
    internal static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS-Components", "sing-box")
        : "/var/lib/relaxkonos/proxy-server";
    private static string Binary => Path.Combine(Root, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
    private static string Config => Path.Combine(Root, "config.json");
    private static string Previous => Config + ".previous";
    private static string Pending => Path.Combine(Root, "pending.json");
    private static string Published => Path.Combine(Root, "published.txt");
    private const string Unit = "relaxkonos-sing-box.service";
    private const string UnitPath = "/etc/systemd/system/" + Unit;
    private sealed record Publication(string Hash, bool WasRunning);
    private sealed record Installed(string Version, string BinaryHash);
    private static readonly SemaphoreSlim Gate = new(1);
    internal static ISingBoxArchiveHost CreateArchiveHost() => OperatingSystem.IsWindows()
        ? new WindowsSingBoxArchiveHost(Root) : OperatingSystem.IsLinux()
            ? new LinuxSingBoxArchiveHost() : throw new RuntimePolicyException("runtime.platform_unsupported");

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
            // Configuration, service control and archive lifecycle all share the same cross-process instance lock.
            using var gate = new RuntimeArchiveStateStore(Path.Combine(Root, "state"), RuntimeSourceProtection.Protect).Acquire();
            if (gate.State is { } managed && (managed.Active is not null || managed.Pending is not null))
            {
                if (request.Operation == PrivilegedOperationKind.ProxyServerAction
                    && request.ProxyServer is { Action: ProxyServerAction.Stop, Material: null })
                    return Json(await SingBoxManagedStop.ExecuteAsync(Root, managed, CreateArchiveHost(), default));
                return Failure("proxy_server.managed_runtime_required");
            }
            if (request.Operation == PrivilegedOperationKind.ProxyServerStatus) return Json(await StatusAsync());
            if (request.Operation == PrivilegedOperationKind.ProxyServerAction)
            {
                if (request.ProxyServer is not { Action: { } action, Material: null } || !Enum.IsDefined(action))
                    return Failure("proxy_server.request_invalid");
                switch (action)
                {
                    case ProxyServerAction.Install:
                        await InstallAsync();
                        return Json(await StatusAsync());
                    case ProxyServerAction.Upgrade:
                        await UpgradeAsync();
                        return Json(await StatusAsync());
                    case ProxyServerAction.Recover:
                        await RecoverAsync();
                        return Json(await StatusAsync());
                    case ProxyServerAction.Uninstall:
                        await UninstallAsync(request.ProxyServer.KeepData);
                        return Json(await StatusAsync());
                    case ProxyServerAction.Stop:
                        await StopAsync();
                        return Json(await StatusAsync());
                }
                RequireInstalled();
                if (File.Exists(Pending)) return Failure("proxy_server.recovery_required");
                if (action == ProxyServerAction.Rollback)
                {
                    await RollbackAsync();
                    return Json(await StatusAsync());
                }
                if (action is ProxyServerAction.Start or ProxyServerAction.Restart)
                {
                    if ((await StatusAsync()).ProblemCode == "proxy_server.configuration_drift")
                        return Failure("proxy_server.configuration_drift");
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
                if (File.Exists(Config)) File.Copy(Config, Previous, true); else File.Delete(Previous);
                var publication = new Publication(Hash(rendered), status.Running);
                await WriteAsync(Pending, JsonSerializer.Serialize(publication));
                try
                {
                    if (status.Running) await StopAsync();
                    File.Move(candidate, Config, true);
                    if (OperatingSystem.IsWindows())
                        await WindowsComponentService.InstallAsync(ComponentKind.SingBox, Root, Binary, Config, true, appliedIdentity: publication.Hash);
                    if (status.Running) await StartAsync();
                    await WriteAsync(Published, publication.Hash);
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
        catch (RuntimePolicyException ex) { return Failure(ex.ProblemCode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or JsonException or HttpRequestException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
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
        // Administrators may stage the exact trusted archive for hosts with restricted Internet access.
        // The fixed root-owned cache receives the same size, digest and extraction checks as a download.
        var cachedArchive = Path.Combine(Root, "runtime.archive");
        using var downloadDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(4) };
        using var response = File.Exists(cachedArchive) ? null
            : await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, downloadDeadline.Token);
        response?.EnsureSuccessStatusCode();
        await using var input = response is null ? File.OpenRead(cachedArchive)
            : await response.Content.ReadAsStreamAsync(downloadDeadline.Token);
        var archive = Path.Combine(Root, "archive.download");
        try
        {
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, downloadDeadline.Token)) != 0)
                {
                    if ((total += read) > SingBoxManifest.MaximumArchiveBytes) throw new IOException("Archive exceeds limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadDeadline.Token);
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
                SingBoxNativeValidation.Parse(await RunAsync(temporary, ["version"]), SingBoxManifest.Version,
                    SingBoxManifest.PlatformName, SingBoxManifest.ArchitectureName);
                File.Move(temporary, Binary);
                await WriteAsync(Path.Combine(Root, "installed.json"), JsonSerializer.Serialize(new Installed(SingBoxManifest.Version, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Binary))))));
                if (OperatingSystem.IsLinux()) await InstallLinuxUnitAsync();
            }
            finally { File.Delete(temporary); }
        }
        finally { File.Delete(archive); }
    }

    /// <summary>Only the pinned version exists in the trust manifest, so an upgrade either repairs the runtime or is a no-op.</summary>
    private static async Task UpgradeAsync()
    {
        if (File.Exists(Binary))
        {
            try { RequireInstalled(); return; }
            catch (IOException) { File.Delete(Binary); }
        }
        File.Delete(Path.Combine(Root, "installed.json"));
        await InstallAsync();
    }

    private static async Task UninstallAsync(bool keepData)
    {
        await StopAsync();
        if (OperatingSystem.IsWindows())
        {
            await WindowsComponentService.RemoveAsync(ComponentKind.SingBox, Root);
            File.Delete(Binary);
            File.Delete(Path.Combine(Root, "installed.json"));
        }
        else
        {
            try { await SystemctlAsync("disable"); } catch (InvalidOperationException) { }
            if (File.Exists(UnitPath)) File.Delete(UnitPath);
            await RunAsync("/usr/bin/systemctl", ["daemon-reload"]);
            File.Delete(Binary);
            File.Delete(Path.Combine(Root, "installed.json"));
        }
        File.Delete(Pending);
        File.Delete(Published);
        if (keepData) return;
        // Only files this module owns. The directory itself stays: the prepared root is pinned by ACLs
        // and the running Helper still holds its operation lock inside it.
        foreach (var path in new[] { Config, Previous, Path.Combine(Root, "candidate.json"), Path.Combine(Root, "installed.json"),
                     Path.Combine(Root, "runtime.archive"), Path.Combine(Root, "archive.download") })
            File.Delete(path);
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
        var unit = $"# RelaxKonOS proxy-server\n[Unit]\nDescription=RelaxKonOS sing-box proxy server\nAfter=network-online.target\n[Service]\nDynamicUser=yes\nLoadCredential=config.json:{Config}\nExecStart={Binary} run -c %d/config.json\nRestart=on-failure\nNoNewPrivileges=yes\nPrivateTmp=yes\nProtectSystem=strict\nProtectHome=yes\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK\n[Install]\nWantedBy=multi-user.target\n";
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
        var process = installed && OperatingSystem.IsWindows() ? WindowsComponentService.Snapshot(ComponentKind.SingBox, Root) : null;
        var running = installed && (OperatingSystem.IsWindows() ? process!.Running : await IsLinuxRunningAsync());
        var hash = File.Exists(Published) ? (await File.ReadAllTextAsync(Published)).Trim() : null;
        var drift = hash is not null && (!File.Exists(Config) || Hash(await File.ReadAllTextAsync(Config)) != hash
            || running && OperatingSystem.IsWindows() && process!.AppliedIdentity != hash);
        return new(true, installed, running, File.Exists(Pending), installed ? SingBoxManifest.Version : null,
            hash, running && !drift && !File.Exists(Pending), drift ? "proxy_server.configuration_drift" : null,
            StatisticsSupported: false, BootEnabled: installed ? await BootEnabledAsync() : null);
    }

    /// <summary>Reports the host's own start-on-boot registration; it never changes it.</summary>
    private static async Task<bool?> BootEnabledAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + WindowsComponentService.Name(ComponentKind.SingBox, Root));
                // 2 = automatic, 3 = manual, 4 = disabled.
                return key?.GetValue("Start") is int start ? start is 0 or 2 : null;
            }
            var state = (await RunAsync("/usr/bin/systemctl", ["is-enabled", Unit], allowedExitCode: 1)).Trim();
            return state switch
            {
                "enabled" or "enabled-runtime" or "static" or "indirect" or "generated" => true,
                "disabled" or "masked" or "masked-runtime" => false,
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { return null; }
    }

    private static async Task<bool> IsLinuxRunningAsync()
    {
        try { return (await RunAsync("/usr/bin/systemctl", ["is-active", Unit])).Trim() == "active"; }
        catch (InvalidOperationException) { return false; }
    }
    private static async Task CheckAsync(string path)
    {
        var build = SingBoxNativeValidation.Parse(await RunAsync(Binary, ["version"]), SingBoxManifest.Version,
            SingBoxManifest.PlatformName, SingBoxManifest.ArchitectureName);
        SingBoxNativeValidation.RequireConfigurationFeatures(build, await File.ReadAllBytesAsync(path));
        await RunAsync(Binary, ["check", "-c", path]);
    }
    private static Task SystemctlAsync(string action) => RunAsync("/usr/bin/systemctl", [action, Unit]);
    private static async Task StopAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            if (WindowsComponentService.Snapshot(ComponentKind.SingBox, Root).Running) await WindowsComponentService.StopAsync(ComponentKind.SingBox, Root);
        }
        else if (File.Exists(UnitPath))
        {
            NoLinks(UnitPath);
            if (!File.ReadAllText(UnitPath).StartsWith("# RelaxKonOS proxy-server\n", StringComparison.Ordinal))
                throw new IOException("Unowned service.");
            // Stop also cancels activating jobs and pending automatic restarts after a failed start.
            await SystemctlAsync("stop");
        }
    }
    private static async Task StartAsync()
    {
        if (OperatingSystem.IsWindows()) await WindowsComponentService.StartAsync(ComponentKind.SingBox, Root);
        else if (OperatingSystem.IsLinux())
        {
            await InstallLinuxUnitAsync();
            // A failed candidate may have exhausted systemd's restart burst before compensation.
            // systemd may have garbage-collected a stopped unit; reset-failed then returns 1.
            await RunAsync("/usr/bin/systemctl", ["reset-failed", Unit], allowedExitCode: 1);
            await SystemctlAsync("start");
            await Task.Delay(1500);
            if (!await IsLinuxRunningAsync()) throw new IOException("Service did not remain running.");
        }
    }

    /// <summary>Restores the configuration file the last publication replaced; the Server owns the revision it maps to.</summary>
    private static async Task RollbackAsync()
    {
        if (!File.Exists(Previous)) throw new IOException("No previous configuration.");
        var status = await StatusAsync();
        if (status.Running) await StopAsync();
        File.Copy(Previous, Config, true);
        await CheckAsync(Config);
        var hash = Hash(await File.ReadAllTextAsync(Config));
        if (OperatingSystem.IsWindows())
            await WindowsComponentService.InstallAsync(ComponentKind.SingBox, Root, Binary, Config, true, appliedIdentity: hash);
        await WriteAsync(Published, hash);
        if (status.Running) await StartAsync();
    }

    private static async Task RecoverAsync()
    {
        if (!File.Exists(Pending)) return;
        var publication = JsonSerializer.Deserialize<Publication>(await File.ReadAllTextAsync(Pending)) ?? throw new IOException();
        await StopAsync();
        if (File.Exists(Previous))
        {
            File.Copy(Previous, Config, true);
            await CheckAsync(Config);
            if (OperatingSystem.IsWindows()) await WindowsComponentService.InstallAsync(ComponentKind.SingBox, Root, Binary, Config, true, appliedIdentity: Hash(await File.ReadAllTextAsync(Config)));
            if (publication.WasRunning) await StartAsync();
            await WriteAsync(Published, Hash(await File.ReadAllTextAsync(Config)));
        }
        else
        {
            File.Delete(Config);
            File.Delete(Published);
        }
        File.Delete(Pending);
    }

    internal static async Task<string> RunAsync(string binary, string[] arguments, int? allowedExitCode = null)
    {
        using var process = new Process { StartInfo = new(binary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(binary))!;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        foreach (var variable in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) process.StartInfo.Environment.Remove(variable);
        if (!process.Start()) throw new IOException();
        var output = ReadBoundedAsync(process.StandardOutput);
        var errors = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        await Task.WhenAll(output, errors);
        var text = await output;
        // Native diagnostics can contain secrets: never cross the privilege boundary.
        if (process.ExitCode != 0 && process.ExitCode != allowedExitCode) throw new InvalidOperationException("sing-box operation failed.");
        return text;
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int read;
        var oversized = false;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (result.Length + read > 8192) oversized = true;
            if (result.Length < 8192) result.Append(buffer, 0, Math.Min(read, 8192 - result.Length));
        }
        if (oversized) throw new IOException("sing-box output exceeds limit.");
        return result.ToString();
    }
    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)));
    private static PrivilegedOperationResult Failure(string code) => new(false, ExitCode: 1, ProblemCode: PrivilegedProblemCode.InvalidRequest, Error: code);
}
