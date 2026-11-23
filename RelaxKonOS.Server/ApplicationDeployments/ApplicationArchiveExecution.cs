using System.Reflection;
using System.Runtime.Loader;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal sealed class ApplicationArchiveExecution(Func<string> resolvePath)
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.ApplicationDeployments.dll";
    internal const string EntryType = "RelaxKonOS.Server.ApplicationDeployments.ApplicationArchiveExtractor";
    internal static ApplicationArchiveExecution Bundled { get; } = new(() =>
        Path.Combine(AppContext.BaseDirectory, "Modules", "ApplicationDeployments", AssemblyFile));

    private delegate Task<string?> PrepareArchive(Stream archive, string destination, int maximumEntries,
        long maximumExpandedBytes, int maximumPathDepth, CancellationToken cancellationToken);

    internal async Task<string?> PrepareAsync(Stream archive, string destination, int maximumEntries,
        long maximumExpandedBytes, int maximumPathDepth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new AssemblyLoadContext("ApplicationDeployments archive execution", isCollectible: true);
        try
        {
            PrepareArchive execute;
            try
            {
                execute = Load(context, resolvePath());
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ModulePackageException)
            {
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.BuildFailed);
            }
            return await execute(archive, destination, maximumEntries, maximumExpandedBytes, maximumPathDepth, cancellationToken);
        }
        finally
        {
            context.Unload();
        }
    }

    internal static void ValidateEntryPoint(string path)
    {
        var context = new AssemblyLoadContext("ApplicationDeployments archive validation", isCollectible: true);
        try { _ = Load(context, path); }
        finally { context.Unload(); }
    }

    private static PrepareArchive Load(AssemblyLoadContext context, string path)
    {
        try
        {
            using var code = File.OpenRead(path);
            var type = context.LoadFromStream(code).GetType(EntryType, throwOnError: true)!;
            var method = type.GetMethod("PrepareAsync", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(EntryType, "PrepareAsync");
            return method.CreateDelegate<PrepareArchive>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException
            or TypeLoadException or MissingMethodException or ArgumentException or AmbiguousMatchException)
        {
            throw new InvalidDataException("Application Deployments archive entry could not be loaded.", exception);
        }
    }
}
