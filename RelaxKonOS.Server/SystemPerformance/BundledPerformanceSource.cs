using System.Runtime.Loader;

namespace RelaxKonOS.Server.SystemPerformance;

internal static class BundledPerformanceSource
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.SystemPerformance.dll";
    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "Modules", "SystemPerformance", AssemblyFile);
    internal static bool BundledAvailable => File.Exists(AssemblyPath);

    internal static string EntryType(bool windows) => windows
        ? "RelaxKonOS.Server.SystemPerformance.WindowsPerformanceSource"
        : "RelaxKonOS.Server.SystemPerformance.LinuxPerformanceSource";

    internal static ISystemPerformanceSource Create() => Create(AssemblyPath);

    internal static ISystemPerformanceSource Create(string path)
    {
        var windows = OperatingSystem.IsWindows();
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var type = ReadType(assembly, windows);
        return (ISystemPerformanceSource)(windows ? Activator.CreateInstance(type)
            : Activator.CreateInstance(type, (Func<int, int>)SystemMonitor.LinuxProcessMetadata.CountThreads))!;
    }

    internal static void Verify(string path, bool windows)
    {
        var context = new AssemblyLoadContext(nameof(BundledPerformanceSource), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(path);
            ReadType(context.LoadFromStream(stream), windows);
        }
        finally { context.Unload(); }
    }

    private static Type ReadType(System.Reflection.Assembly assembly, bool windows)
    {
        var type = assembly.GetType(EntryType(windows), throwOnError: true)!;
        if (!type.IsPublic || type.IsAbstract || !typeof(ISystemPerformanceSource).IsAssignableFrom(type)
            || type.GetConstructor(windows ? [] : [typeof(Func<int, int>)]) is null)
            throw new InvalidDataException("Performance execution entry does not match its host contract.");
        return type;
    }
}
