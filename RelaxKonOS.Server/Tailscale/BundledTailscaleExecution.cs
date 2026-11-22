using System.Runtime.Loader;

namespace RelaxKonOS.Server.Tailscale;

internal static class BundledTailscaleExecution
{
    private static readonly Lazy<ITailscaleExecution> Execution = new(Load);
    internal static ITailscaleExecution Instance => Execution.Value;

    private static ITailscaleExecution Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Modules", "Tailscale", "RelaxKonOS.ServerModule.Tailscale.dll");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var entry = assembly.GetType("RelaxKonOS.ServerModule.Tailscale.TailscaleExecution", throwOnError: true)!;
        return (ITailscaleExecution)Activator.CreateInstance(entry)!;
    }
}
