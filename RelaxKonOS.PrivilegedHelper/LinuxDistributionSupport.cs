namespace RelaxKonOS.PrivilegedHelper;

internal static class LinuxDistributionSupport
{
    internal static (string Distribution, string Suite)? ResolveDockerRepository(string distro, string version) => (distro, version) switch
    {
        ("debian", "12") => ("debian", "bookworm"),
        ("debian", "13") => ("debian", "trixie"),
        ("ubuntu", "22.04") or ("linuxmint", "21" or "21.1" or "21.2" or "21.3") => ("ubuntu", "jammy"),
        ("ubuntu", "24.04") or ("linuxmint", "22" or "22.1" or "22.2" or "22.3") => ("ubuntu", "noble"),
        ("ubuntu", "26.04") => ("ubuntu", "resolute"),
        _ => null
    };

    public static bool IsSquidPackageSupported() =>
        File.Exists("/usr/bin/apt-get") && File.Exists("/usr/bin/systemctl")
        && File.Exists("/etc/os-release") && IsSquidPackageSupported(File.ReadAllText("/etc/os-release"));

    internal static bool IsSquidPackageSupported(string osRelease)
    {
        // Use the repository's explicit Debian/Ubuntu/Mint version matrix. ID_LIKE alone
        // must not turn an unknown derivative into a supported package-management target.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in osRelease.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('#')) continue;
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || parts[0] is not ("ID" or "VERSION_ID")) continue;
            var value = parts[1].Trim();
            if (value.Length >= 2 && (value[0] == '\"' && value[^1] == '\"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            if (!values.TryAdd(parts[0], value)) return false;
        }
        return values.TryGetValue("ID", out var id) && values.TryGetValue("VERSION_ID", out var version)
            && RelaxKonOS.Protocol.ServerCenter.ServerHostPlatformSupport.IsSupportedLinuxSystem(id, version);
    }

    public static bool IsSambaSupported()
    {
        return File.Exists("/etc/os-release") && IsSambaSupported(File.ReadAllText("/etc/os-release"));
    }

    internal static bool IsSambaSupported(string osRelease)
    {
        var values = osRelease.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1].Trim().Trim('\"'), StringComparer.OrdinalIgnoreCase);

        return values.TryGetValue("ID", out var id) && values.TryGetValue("VERSION_ID", out var version)
            && RelaxKonOS.Protocol.ServerCenter.ServerHostPlatformSupport.IsSupportedLinuxSystem(id.ToLowerInvariant(), version);
    }
}
