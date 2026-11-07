using System.Runtime.Loader;

// The isolated full Server uses this hook to observe actual load/unload events.
internal static class StartupHook
{
    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, args) =>
        {
            if (args.LoadedAssembly.GetName().Name != "RelaxKonOS.ServerModule.Docker"
                || AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is not { IsCollectible: true, Name: { } name } context)
                return;
            context.Unloading += static unloading => Console.Error.WriteLine($"DOCKER COMPOSE ALC unload {unloading.Name}");
            Console.Error.WriteLine($"DOCKER COMPOSE ALC load {name}");
        };
    }
}
