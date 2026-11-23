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
    private delegate Task WriteDockerfile(string destination, ApplicationSourceKind kind, string baseImage, int port,
        string entry, bool selfContained, CancellationToken cancellationToken);

    internal Task<string?> PrepareAsync(Stream archive, string destination, int maximumEntries,
        long maximumExpandedBytes, int maximumPathDepth, CancellationToken cancellationToken)
        => ExecuteAsync<PrepareArchive, string?>("PrepareAsync", execute =>
            execute(archive, destination, maximumEntries, maximumExpandedBytes, maximumPathDepth, cancellationToken), cancellationToken);

    internal Task WriteDockerfileAsync(string destination, ApplicationSourceKind kind, string baseImage, int port,
        string entry, bool selfContained, CancellationToken cancellationToken)
        => ExecuteAsync<WriteDockerfile, bool>("WriteDockerfileAsync", async execute =>
        {
            await execute(destination, kind, baseImage, port, entry, selfContained, cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<TResult> ExecuteAsync<TEntry, TResult>(string method, Func<TEntry, Task<TResult>> invoke,
        CancellationToken cancellationToken) where TEntry : Delegate
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new AssemblyLoadContext("ApplicationDeployments archive execution", isCollectible: true);
        try
        {
            TEntry execute;
            try
            {
                execute = Bind<TEntry>(Load(context, resolvePath()), method);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ModulePackageException)
            {
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.BuildFailed);
            }
            return await invoke(execute);
        }
        finally
        {
            context.Unload();
        }
    }

    internal static void ValidateEntryPoint(string path)
    {
        var context = new AssemblyLoadContext("ApplicationDeployments archive validation", isCollectible: true);
        try
        {
            var type = Load(context, path);
            _ = Bind<PrepareArchive>(type, "PrepareAsync");
            _ = Bind<WriteDockerfile>(type, "WriteDockerfileAsync");
        }
        finally { context.Unload(); }
    }

    private static Type Load(AssemblyLoadContext context, string path)
    {
        try
        {
            using var code = File.OpenRead(path);
            return context.LoadFromStream(code).GetType(EntryType, throwOnError: true)!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException
            or TypeLoadException or ArgumentException)
        {
            throw new InvalidDataException("Application Deployments archive entry could not be loaded.", exception);
        }
    }

    private static TEntry Bind<TEntry>(Type type, string name) where TEntry : Delegate
    {
        try
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(EntryType, name);
            return method.CreateDelegate<TEntry>();
        }
        catch (Exception exception) when (exception is MissingMethodException or ArgumentException or AmbiguousMatchException)
        {
            throw new InvalidDataException("Application Deployments entry does not match its execution contract.", exception);
        }
    }
}
