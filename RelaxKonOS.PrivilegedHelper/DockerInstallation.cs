using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    // Fixed Ubuntu provider. No request value can choose a package, source, path or command.
    static async Task<PrivilegedOperationResult> InstallDockerEngineAsync()
    {
        if (!OperatingSystem.IsLinux()) return DockerFailure(PrivilegedProblemCode.UnsupportedOperation);
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
        return await RunFixedCommandAsync("/usr/bin/docker", ["--host", "unix:///var/run/docker.sock", "info", "--format", "{{.ServerVersion}}"], TimeSpan.FromSeconds(30), "docker engine verification failed");
    }
    static PrivilegedOperationResult DockerFailure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}
