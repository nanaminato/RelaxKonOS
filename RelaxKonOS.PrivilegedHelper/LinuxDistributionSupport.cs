namespace RelaxKonOS.PrivilegedHelper;

internal static class LinuxDistributionSupport
{
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
            && ((id.Equals("debian", StringComparison.OrdinalIgnoreCase) && version == "12")
                || (id.Equals("ubuntu", StringComparison.OrdinalIgnoreCase) && version is "22.04" or "24.04" or "26.04"));
    }
}
