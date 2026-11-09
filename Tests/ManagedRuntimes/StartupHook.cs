using System.Runtime.Loader;

// The runtime locates this global type by name when the Linux fixture enables its startup hook.
internal static class StartupHook
{
    internal const string ParserEventPrefix = "PROXY PARSER ";
    internal const string SubscriptionEventPrefix = "PROXY SUBSCRIPTION ";
    internal const string SubscriptionCollectionPrefix = "PROXY CODE COLLECTED ";

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("RELAXKONOS_TEST_TUN_CLEANUP") is { } cleanupRoot)
            MihomoTunCleanupBarrier.Register(cleanupRoot);
        if (Environment.GetEnvironmentVariable("RELAXKONOS_TEST_OVERVIEW_CLEANUP") is { } overviewRoot)
            MihomoTunCleanupBarrier.RegisterOverview(overviewRoot);
        if (Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SETTINGS_CLEANUP") is { } settingsRoot)
            MihomoTunCleanupBarrier.RegisterSettings(settingsRoot);
        if (Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SQUID_CLEANUP") is { } squidRoot)
            MihomoTunCleanupBarrier.RegisterSquid(squidRoot);

        AppDomain.CurrentDomain.AssemblyLoad += static (_, args) =>
        {
            if (args.LoadedAssembly.GetName().Name != "RelaxKonOS.ServerModule.Proxy"
                || AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is not { Name: { } name } context)
                return;

            var prefix = name.StartsWith("proxy-connections:", StringComparison.Ordinal) ? ParserEventPrefix
                : name.StartsWith("proxy-subscription:", StringComparison.Ordinal) ? SubscriptionEventPrefix : null;
            if (prefix is null) return;
            context.Unloading += unloading => Console.Error.WriteLine($"{prefix}unload {unloading.Name}");
            if (prefix == SubscriptionEventPrefix
                && Environment.GetEnvironmentVariable("RELAXKONOS_TEST_COLLECT_SUBSCRIPTION") == "1")
                context.Unloading += ObserveCollection;
            Console.Error.WriteLine($"{prefix}load {name}");
        };
    }

    private static void ObserveCollection(AssemblyLoadContext context)
    {
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            for (var attempt = 0; state.Code.IsAlive && attempt < 80; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
            if (!state.Code.IsAlive) Console.Error.WriteLine($"{SubscriptionCollectionPrefix}{state.Name}");
        }, (Code: new WeakReference(context), Name: context.Name), preferLocal: false);
    }
}
