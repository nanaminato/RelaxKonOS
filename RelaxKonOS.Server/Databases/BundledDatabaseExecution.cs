using System.Runtime.Loader;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Databases;

internal static class BundledDatabaseExecution
{
    private static readonly Lazy<IDatabaseExecution> Execution = new(Load);
    internal static IDatabaseExecution Instance => Execution.Value;

    private static string AssemblyPath => Path.Combine(AppContext.BaseDirectory, "Modules", "Database", "RelaxKonOS.ServerModule.Database.dll");
    internal static bool BundledAvailable => File.Exists(AssemblyPath);

    private static IDatabaseExecution Load()
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(AssemblyPath);
        var entry = assembly.GetType("RelaxKonOS.ServerModule.Database.DatabaseExecution", throwOnError: true)!;
        return (IDatabaseExecution)Activator.CreateInstance(entry)!;
    }
}
