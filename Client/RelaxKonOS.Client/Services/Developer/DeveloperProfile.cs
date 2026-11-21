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
    internal static string DataDirectory { get; } = Path.GetFullPath(Path.Combine(
        Environment.GetEnvironmentVariable("RELAXKONOS_DEV_DATA_HOME") ?? LocalData, "RelaxKonOS", "Debug", Name));
#else
    public static bool IsDebug => false;
    public static string Name => "release";
    public static int Port => DeveloperBridgeTarget.ReleasePort;
    internal static string DataDirectory => Path.Combine(LocalData, "RelaxKonOS");
#endif

    private static string LocalData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static IDisposable? Acquire()
    {
#if DEBUG
        Directory.CreateDirectory(DataDirectory);
        try
        {
            return new FileStream(Path.Combine(DataDirectory, ".client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Cannot open Debug profile '{Name}'. Choose a different RELAXKONOS_DEV_PROFILE for another instance.", exception);
        }
#else
        return null;
#endif
    }
}
