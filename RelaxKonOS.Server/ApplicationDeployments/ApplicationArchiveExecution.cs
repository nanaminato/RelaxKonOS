using System.Reflection;
using System.Runtime.Loader;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal static class ApplicationArchiveExecution
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.ApplicationDeployments.dll";
    internal const string EntryType = "RelaxKonOS.Server.ApplicationDeployments.ApplicationArchiveExtractor";

    private delegate Task<string?> ExtractArchive(Stream archive, string destination, int maximumEntries,
        long maximumExpandedBytes, int maximumPathDepth, CancellationToken cancellationToken);

    internal static async Task<string?> ExtractAsync(Stream archive, string destination, int maximumEntries,
        long maximumExpandedBytes, int maximumPathDepth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new AssemblyLoadContext("ApplicationDeployments archive execution", isCollectible: true);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Modules", "ApplicationDeployments", AssemblyFile);
            ExtractArchive execute;
            try
            {
                using var code = File.OpenRead(path);
                var type = context.LoadFromStream(code).GetType(EntryType, throwOnError: true)!;
                var method = type.GetMethod("ExtractAsync", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException(EntryType, "ExtractAsync");
                execute = method.CreateDelegate<ExtractArchive>();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException
                or TypeLoadException or MissingMethodException or ArgumentException or AmbiguousMatchException)
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
}
