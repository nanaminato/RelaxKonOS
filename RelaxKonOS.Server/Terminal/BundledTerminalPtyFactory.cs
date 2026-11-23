using System.Runtime.Loader;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

internal static class BundledTerminalPtyFactory
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.Terminal.Execution.dll";
    internal const string EntryType = "RelaxKonOS.ServerModule.Terminal.Execution.LocalTerminalPtyFactory";

    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "Modules", "Terminal", AssemblyFile);
    internal static bool BundledAvailable => File.Exists(AssemblyPath);
    internal static IPtyFactory Create() => Create(AssemblyPath);

    internal static IPtyFactory Create(string path) =>
        (IPtyFactory)Activator.CreateInstance(ReadType(AssemblyLoadContext.Default.LoadFromAssemblyPath(path)))!;

    internal static void Verify(string path)
    {
        var context = new AssemblyLoadContext(nameof(BundledTerminalPtyFactory), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(path);
            ReadType(context.LoadFromStream(stream));
        }
        finally { context.Unload(); }
    }

    private static Type ReadType(System.Reflection.Assembly assembly)
    {
        var type = assembly.GetType(EntryType, throwOnError: true)!;
        if (!type.IsPublic || type.IsAbstract || !typeof(IPtyFactory).IsAssignableFrom(type)
            || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidDataException("Terminal execution entry does not match its host contract.");
        return type;
    }
}
