using RelaxKonOS.Protocol.Developer;

namespace RelaxKonOS.Client.Services.Developer;

/// <summary>Local settings and installation root for the current client build.</summary>
public static class DeveloperProfile
{
#if DEBUG
    public static bool IsDebug => true;
    public static string Name { get; } = DeveloperBridgeTarget.ValidateProfile(
        Environment.GetEnvironmentVariable("RELAXKONOS_DEV_PROFILE") ?? DeveloperBridgeTarget.DefaultProfile);
    public static int Port { get; } = DeveloperBridgeTarget.ResolveDebugPort(Name, Environment.GetEnvironmentVariable("RELAXKONOS_DEV_PORT"));
#else
    public static bool IsDebug => false;
    public static string Name => "release";
    public static int Port => DeveloperBridgeTarget.ReleasePort;
#endif

    private static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Relative overrides are anchored to the executable, independent of the IDE working directory.
    internal static string DataDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("RELAXKONOS_DATA_HOME");
            var root = string.IsNullOrWhiteSpace(configured)
                ? LocalData
                : Path.GetFullPath(configured, AppContext.BaseDirectory);
            return IsDebug
                ? Path.Combine(root, "RelaxKonOS", "Debug", Name)
                : Path.Combine(root, "RelaxKonOS");
        }
    }

    public static IDisposable? Acquire()
    {
        var directory = DataDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            // Verify write access before initializing any services, including in Release.
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot write client data directory '{directory}'. Set RELAXKONOS_DATA_HOME to a writable data parent directory in your run/debug configuration.", exception);
        }
#if DEBUG
        try
        {
            return new FileStream(Path.Combine(directory, ".client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"Cannot write Debug profile lock in '{directory}'. Check permissions or set RELAXKONOS_DATA_HOME to a writable data parent directory.", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Cannot open Debug profile '{Name}' in '{directory}'. Close the other instance or choose a different RELAXKONOS_DEV_PROFILE. Check directory access if no other instance is running.", exception);
        }
#else
        return null;
#endif
    }
}
