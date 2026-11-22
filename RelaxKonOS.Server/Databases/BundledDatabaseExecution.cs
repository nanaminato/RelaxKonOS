using System.Runtime.Loader;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Databases;

internal static class BundledDatabaseExecution
{
    private static readonly Lazy<IDatabaseExecution> Execution = new(Load);
    internal static IDatabaseExecution Instance => Execution.Value;

    private static IDatabaseExecution Load()
    {
        // The backup scheduler belongs to the host lifetime and participates in its normal shutdown.
        var path = Path.Combine(AppContext.BaseDirectory, "Modules", "Database", "RelaxKonOS.ServerModule.Database.dll");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var entry = assembly.GetType("RelaxKonOS.ServerModule.Database.DatabaseExecution", throwOnError: true)!;
        return (IDatabaseExecution)Activator.CreateInstance(entry)!;
    }
}
