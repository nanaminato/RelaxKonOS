namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Single source of truth for where Compose state lives on the host. Both the Compose source store and
/// the stack operation ledger derive their location from here, so an operation record can never point
/// at a directory the executor would not use.
/// </summary>
internal static class DockerComposePaths
{
    /// <summary>
    /// Absolute directory for persisted Compose sources. A relative configured path is rejected rather
    /// than resolved, so runtime data never silently lands in the application/source directory.
    /// </summary>
    public static string ResolveDataDirectory(IHostEnvironment environment, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException("DockerCompose:DataDirectory must be an absolute path.");
            return Path.GetFullPath(configured);
        }

        // Development must be usable from a checkout without creating generated files in it.
        if (environment.IsDevelopment())
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localData))
                return Path.Combine(localData, "RelaxKonOS", "docker-compose");
        }

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "docker-compose");
        if (OperatingSystem.IsLinux())
            return "/var/lib/relaxkonos/docker-compose";

        throw new PlatformNotSupportedException("RelaxKonOS Docker Compose storage supports Windows and Linux hosts only.");
    }

    /// <summary>The durable operation ledger. It lives next to the sources it describes.</summary>
    public static string LedgerPath(IHostEnvironment environment, string? configured) =>
        Path.Combine(ResolveDataDirectory(environment, configured), "stack-operations.json");
}
