using System.Runtime.Loader;

// The runtime locates this global type by name when the Linux fixture enables its startup hook.
internal static class StartupHook
{
    internal const string ParserEventPrefix = "PROXY PARSER ";
    internal const string SubscriptionEventPrefix = "PROXY SUBSCRIPTION ";

    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, args) =>
        {
            if (args.LoadedAssembly.GetName().Name != "RelaxKonOS.ServerModule.Proxy"
                || AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is not { Name: { } name } context)
                return;

            var prefix = name.StartsWith("proxy-connections:", StringComparison.Ordinal) ? ParserEventPrefix
                : name.StartsWith("proxy-subscription:", StringComparison.Ordinal) ? SubscriptionEventPrefix : null;
            if (prefix is null) return;
            context.Unloading += unloading => Console.Error.WriteLine($"{prefix}unload {unloading.Name}");
            Console.Error.WriteLine($"{prefix}load {name}");
        };
    }
}
