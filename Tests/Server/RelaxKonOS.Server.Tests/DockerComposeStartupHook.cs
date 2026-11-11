using System.Runtime.Loader;

// The isolated full Server uses this hook to observe actual load/unload events.
internal static class StartupHook
{
    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, args) =>
        {
            var assemblyName = args.LoadedAssembly.GetName().Name;
            if (assemblyName == "Certify.ACME.Anvil")
            {
                Console.Error.WriteLine($"ACME EXECUTION sdk {AssemblyLoadContext.GetLoadContext(args.LoadedAssembly)?.Name}");
                return;
            }
            if (assemblyName is not ("RelaxKonOS.ServerModule.Docker" or "RelaxKonOS.ServerModule.Certificates" or "RelaxKonOS.ServerModule.WebServer")
                || AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is not { IsCollectible: true, Name: { } name } context)
                return;
            var prefix = assemblyName switch
            {
                "RelaxKonOS.ServerModule.Docker" => "DOCKER COMPOSE ALC",
                "RelaxKonOS.ServerModule.Certificates" => "ACME EXECUTION",
                _ => "WEBSERVER EXECUTION",
            };
            context.Unloading += unloading => Console.Error.WriteLine($"{prefix} unload {unloading.Name}");
            if (assemblyName == "RelaxKonOS.ServerModule.WebServer"
                && Environment.GetEnvironmentVariable("RELAXKONOS_TEST_WEBSERVER_COLLECT") == "1")
                context.Unloading += ObserveWebServerCollection;
            Console.Error.WriteLine($"{prefix} load {name}");
        };
    }

    private static void ObserveWebServerCollection(AssemblyLoadContext context)
    {
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            for (var attempt = 0; state.Code.IsAlive && attempt < 80; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
            if (!state.Code.IsAlive) Console.Error.WriteLine($"WEBSERVER EXECUTION collected {state.Name}");
        }, (Code: new WeakReference(context), Name: context.Name), preferLocal: false);
    }
}
