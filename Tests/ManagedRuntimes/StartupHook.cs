using System.Runtime.Loader;

// The runtime locates this global type by name when the Linux fixture enables its startup hook.
internal static class StartupHook
{
    internal const string ParserEventPrefix = "PROXY PARSER ";

    public static void Initialize()
    {
        AppDomain.CurrentDomain.AssemblyLoad += static (_, args) =>
        {
            if (args.LoadedAssembly.GetName().Name != "RelaxKonOS.ServerModule.Proxy"
                || AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is not { Name: { } name } context
                || !name.StartsWith("proxy-connections:", StringComparison.Ordinal))
                return;

            context.Unloading += static unloading => Console.Error.WriteLine($"{ParserEventPrefix}unload {unloading.Name}");
            Console.Error.WriteLine($"{ParserEventPrefix}load {name}");
        };
    }
}
