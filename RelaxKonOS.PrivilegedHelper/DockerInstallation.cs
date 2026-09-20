using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private const string DockerAccessUserPolicyPath = "/etc/relaxkonos/docker-access-user";

    // Fixed Ubuntu provider. No request value can choose a package, source, path or command.
    static async Task<PrivilegedOperationResult> InstallDockerEngineAsync()
    {
        if (!OperatingSystem.IsLinux()) return DockerFailure(PrivilegedProblemCode.UnsupportedOperation);
        var accessUser = ReadDockerAccessUser();
        if (accessUser is null) return DockerFailure(PrivilegedProblemCode.AccessDenied);
        var release = File.ReadAllLines("/etc/os-release").Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1].Trim('"'));
        if (!release.TryGetValue("ID", out var distro) || distro != "ubuntu"
            || !release.TryGetValue("VERSION_ID", out var version) || version is not ("22.04" or "24.04"))
            return DockerFailure(PrivilegedProblemCode.UnsupportedOperation);
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => null };
        if (architecture is null) return DockerFailure(PrivilegedProblemCode.UnsupportedOperation);
        const string sourcePath = "/etc/apt/sources.list.d/relaxkonos-docker.sources";
        const string keyPath = "/etc/apt/keyrings/relaxkonos-docker.asc";
        var suite = version == "22.04" ? "jammy" : "noble";
        var source = $"# RelaxKonOS managed Docker Engine\nTypes: deb\nURIs: https://download.docker.com/linux/ubuntu\nSuites: {suite}\nComponents: stable\nArchitectures: {architecture}\nSigned-By: {keyPath}\n";
        foreach (var path in new[] { "/etc/apt", "/etc/apt/sources.list.d", "/etc/apt/keyrings", sourcePath, keyPath })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return DockerFailure(PrivilegedProblemCode.ResourceNotAllowed);
        if (File.Exists(sourcePath) && await File.ReadAllTextAsync(sourcePath) != source)
            return DockerFailure(PrivilegedProblemCode.ResourceNotAllowed);
        // Refuse external engines and conflicting packages. Never uninstall someone else's runtime.
        foreach (var package in new[] { "docker.io", "docker-compose", "docker-compose-v2", "docker-doc", "docker-buildx", "podman-docker", "containerd", "runc" })
        {
            var installed = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query", ["-W", "-f=${db:Status-Status}", package], "package probe failed");
            if (installed.Success && installed.OutputBase64 is { } encoded
                && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Trim() == "installed")
                return DockerFailure(PrivilegedProblemCode.Conflict);
        }
        if (!File.Exists(sourcePath) && (File.Exists("/usr/bin/docker") || Directory.Exists("/var/lib/docker") && Directory.EnumerateFileSystemEntries("/var/lib/docker").Any()))
            return DockerFailure(PrivilegedProblemCode.Conflict);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync("https://download.docker.com/linux/ubuntu/gpg", HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 65536)
            return DockerFailure(PrivilegedProblemCode.InternalError);
        await using var input = await response.Content.ReadAsStreamAsync();
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            if (bytes.Length + count > 65536) return DockerFailure(PrivilegedProblemCode.ContentTooLarge);
            bytes.Write(buffer, 0, count);
        }
        var key = bytes.ToArray();
        if (!System.Text.Encoding.ASCII.GetString(key).StartsWith("-----BEGIN PGP PUBLIC KEY BLOCK-----", StringComparison.Ordinal))
            return DockerFailure(PrivilegedProblemCode.InvalidRequest);
        Directory.CreateDirectory("/etc/apt/keyrings");
        await AtomicWriteBytesAsync(keyPath, key);
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await AtomicWriteBytesAsync(sourcePath, System.Text.Encoding.UTF8.GetBytes(source));
        var update = await RunAptAsync(["update"], TimeSpan.FromMinutes(10), "docker package update failed");
        if (!update.Success) return update;
        var install = await RunAptAsync(["install", "--yes", "--no-install-recommends", "docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin"], TimeSpan.FromMinutes(10), "docker package install failed");
        if (!install.Success) return install;
        var start = await RunFixedCommandAsync("/usr/bin/systemctl", ["enable", "--now", "docker.service"], TimeSpan.FromSeconds(60), "docker service failed");
        if (!start.Success) return start;
        var verification = await RunFixedCommandAsync("/usr/bin/docker", ["--host", "unix:///var/run/docker.sock", "info", "--format", "{{.ServerVersion}}"], TimeSpan.FromSeconds(30), "docker engine verification failed");
        if (!verification.Success) return verification;
        var grant = await RunFixedCommandAsync("/usr/sbin/usermod", ["--append", "--groups", "docker", accessUser], TimeSpan.FromSeconds(30), "docker access grant failed");
        if (!grant.Success) return grant;
        // The Server process has already started without the new supplementary group. Do not
        // run its direct Docker health check until systemd has started it again with that group.
        return new(false, ProblemCode: PrivilegedProblemCode.RestartRequired, Error: "restart RelaxKonOS Server to apply Docker access");
    }

    // The daemon unit is a Helper constant. A request selects only the action, so engine control
    // cannot be redirected at another unit the way a caller-supplied service id could.
    private const string DockerServiceUnit = "docker.service";

    static async Task<PrivilegedOperationResult> ApplyDockerEngineServiceActionAsync(PrivilegedServiceAction? action)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/systemctl"))
            return DockerFailure(PrivilegedProblemCode.UnsupportedOperation);
        if (action is not (PrivilegedServiceAction.Start or PrivilegedServiceAction.Stop or PrivilegedServiceAction.Restart))
            return DockerFailure(PrivilegedProblemCode.InvalidRequest);
        var command = action switch
        {
            PrivilegedServiceAction.Start => "start",
            PrivilegedServiceAction.Stop => "stop",
            _ => "restart",
        };
        // Unlike the proxy drop-in this changes no unit file, so it never enables or disables the
        // daemon: the host's boot policy stays exactly as the administrator left it.
        return await RunFixedCommandAsync("/usr/bin/systemctl", [command, DockerServiceUnit], TimeSpan.FromSeconds(120), "docker service action failed");
    }

    // Docker Desktop ignores daemon.json proxies, but a native Linux daemon reads its proxy from
    // the unit's start-up environment. This drop-in is therefore the only supported Linux
    // mechanism, and both its path and its contents are owned by the Helper.
    private const string DockerProxyDropInDirectory = "/etc/systemd/system/docker.service.d";
    private const string DockerProxyDropInPath = DockerProxyDropInDirectory + "/http-proxy.conf";
    private const int MaximumProxyValueLength = 512;
    private const int MaximumProxyBypassLength = 1024;

    static async Task<PrivilegedOperationResult> ConfigureDockerEngineProxyAsync(DockerProxyConfiguration? configuration)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/systemctl"))
            return DockerFailure(PrivilegedProblemCode.UnsupportedOperation);
        if (configuration is null || !TryResolveDockerProxyValues(configuration, out var httpProxy, out var httpsProxy, out var noProxy))
            return DockerFailure(PrivilegedProblemCode.InvalidRequest);
        if (!IsManageableProxyTarget()) return DockerFailure(PrivilegedProblemCode.ResourceNotAllowed);

        try
        {
            if (configuration.Enabled)
            {
                var content = SerializeDockerProxyDropIn(httpProxy, httpsProxy, noProxy);
                Directory.CreateDirectory(DockerProxyDropInDirectory);
                File.SetUnixFileMode(DockerProxyDropInDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                await AtomicWriteTextAsync(DockerProxyDropInPath, content);
                File.SetUnixFileMode(DockerProxyDropInPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                // Read the drop-in back rather than probing the unit environment: systemctl would
                // echo the value, and a proxy URL may embed a credential.
                if (await File.ReadAllTextAsync(DockerProxyDropInPath) != content)
                    return DockerFailure(PrivilegedProblemCode.InternalError);
            }
            else if (File.Exists(DockerProxyDropInPath))
            {
                File.Delete(DockerProxyDropInPath);
            }

            var reload = await RunFixedCommandAsync("/usr/bin/systemctl", ["daemon-reload"], TimeSpan.FromSeconds(60), "docker proxy daemon reload failed");
            if (!reload.Success) return reload;
            // try-restart is a no-op for a stopped daemon, which picks the drop-in up on its next
            // start; restarting a daemon nobody asked to run would be a surprise host change.
            return await RunFixedCommandAsync("/usr/bin/systemctl", ["try-restart", "docker.service"], TimeSpan.FromSeconds(120), "docker service restart failed");
        }
        catch (UnauthorizedAccessException) { return DockerFailure(PrivilegedProblemCode.AccessDenied); }
        catch (IOException) { return DockerFailure(PrivilegedProblemCode.InternalError); }
    }

    /// <summary>Re-validates every value. The Server sends trimmed values, but the Helper never trusts that.</summary>
    private static bool TryResolveDockerProxyValues(DockerProxyConfiguration configuration, out string httpProxy, out string httpsProxy, out string noProxy)
    {
        httpProxy = configuration.HttpProxy?.Trim() ?? string.Empty;
        httpsProxy = configuration.HttpsProxy?.Trim() ?? string.Empty;
        noProxy = configuration.NoProxy?.Trim() ?? string.Empty;
        if (!configuration.Enabled) return httpProxy.Length == 0 && httpsProxy.Length == 0 && noProxy.Length == 0;
        if (!IsValidProxyUrl(httpProxy) || httpsProxy.Length > 0 && !IsValidProxyUrl(httpsProxy)) return false;
        if (httpsProxy.Length == 0) httpsProxy = httpProxy;
        return IsValidProxyBypassList(noProxy);
    }

    private static bool IsValidProxyUrl(string value) => value.Length is > 0 and <= MaximumProxyValueLength
        && value.All(IsSafeUnitValueCharacter)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(uri.Host)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>A bypass list is a comma-separated set of host, domain, or CIDR tokens.</summary>
    private static bool IsValidProxyBypassList(string value)
    {
        if (value.Length > MaximumProxyBypassLength) return false;
        if (value.Length == 0) return true;
        return value.Split(',').All(token => token.Length is > 0 and <= 255
            && token.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or ':' or '*' or '/' or '[' or ']'));
    }

    /// <summary>Rejects quotes, backslashes, control characters, and non-ASCII, so a value cannot
    /// terminate the systemd unit line or inject a second directive.</summary>
    private static bool IsSafeUnitValueCharacter(char character) => character is >= '!' and <= '~' && character is not ('"' or '\\');

    private static string SerializeDockerProxyDropIn(string httpProxy, string httpsProxy, string noProxy)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("# RelaxKonOS managed Docker daemon proxy - do not edit\n");
        builder.Append("[Service]\n");
        builder.Append("Environment=\"HTTP_PROXY=").Append(EscapeUnitValue(httpProxy)).Append("\"\n");
        builder.Append("Environment=\"HTTPS_PROXY=").Append(EscapeUnitValue(httpsProxy)).Append("\"\n");
        if (noProxy.Length > 0) builder.Append("Environment=\"NO_PROXY=").Append(EscapeUnitValue(noProxy)).Append("\"\n");
        return builder.ToString();
    }

    /// <summary>systemd treats '%' as a specifier introducer, so a literal percent must be doubled.</summary>
    private static string EscapeUnitValue(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    private static bool IsManageableProxyTarget()
    {
        try
        {
            return !HasReparsePoint(DockerProxyDropInDirectory) && !HasReparsePoint(DockerProxyDropInPath);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasReparsePoint(string path) => (File.Exists(path) || Directory.Exists(path))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string? ReadDockerAccessUser()
    {
        try
        {
            if (!File.Exists(DockerAccessUserPolicyPath)
                || (File.GetAttributes(DockerAccessUserPolicyPath) & FileAttributes.ReparsePoint) != 0) return null;
            var user = File.ReadAllText(DockerAccessUserPolicyPath).Trim();
            return IsValidLinuxUser(user) ? user : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool IsValidLinuxUser(string value) => value.Length is >= 1 and <= 32
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    static PrivilegedOperationResult DockerFailure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}
