using System.Reflection;
using System.Runtime.Loader;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal sealed class ApplicationArchiveExecution(Func<string> resolvePath)
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.ApplicationDeployments.dll";
    internal const string EntryType = "RelaxKonOS.Server.ApplicationDeployments.ApplicationArchiveExtractor";
    private static string BundledPath => Path.Combine(AppContext.BaseDirectory, "Modules", "ApplicationDeployments", AssemblyFile);
    internal static bool BundledAvailable => File.Exists(BundledPath);
    internal static ApplicationArchiveExecution Bundled { get; } = new(() => BundledPath);

    private delegate Task<(string? EntryPoint, string? Problem)> PrepareBuildContext(Stream archive, string destination,
        int maximumEntries, long maximumExpandedBytes, int maximumPathDepth, ApplicationSourceKind kind,
        string? requestedImage, string? runtimeVersion, string? programEntry, bool selfContained,
        ApplicationWorkloadKind workloadKind, int port, string selfContainedImage, string webImage, string workerImage,
        CancellationToken cancellationToken);

    internal async Task<string> PrepareBuildContextAsync(Stream archive, string destination, DeploymentPlan plan,
        ApplicationWorkloadKind workloadKind, int port, ApplicationDeploymentOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new AssemblyLoadContext("ApplicationDeployments archive execution", isCollectible: true);
        try
        {
            PrepareBuildContext execute;
            try
            {
                execute = Bind(Load(context, resolvePath()));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ModulePackageException)
            {
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.BuildFailed);
            }
            var result = await execute(archive, destination, options.MaximumArchiveEntries, options.MaximumExpandedBytes,
                options.MaximumPathDepth, plan.SourceKind, plan.BaseImage, plan.RuntimeVersion, plan.ProgramEntry,
                plan.SelfContained, workloadKind, port, options.DotNetSelfContainedBaseImage, options.DotNetWebBaseImage,
                options.DotNetWorkerBaseImage, cancellationToken);
            if (result.Problem is { } problem)
                throw new ApplicationDeploymentException(problem, problem == ApplicationDeploymentProblemCodes.BuildFailed ? 409 : 400);
            return result.EntryPoint ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.BuildFailed);
        }
        finally
        {
            context.Unload();
        }
    }

    internal void ValidateEntryPoint() => ValidateEntryPoint(resolvePath());

    internal static void ValidateEntryPoint(string path)
    {
        var context = new AssemblyLoadContext("ApplicationDeployments archive validation", isCollectible: true);
        try
        {
            var type = Load(context, path);
            _ = Bind(type);
            _ = Bind<Action<string, IReadOnlySet<string>, Guid?>>(type, nameof(PruneBuildContexts));
        }
        finally { context.Unload(); }
    }

    internal void PruneBuildContexts(string root, IReadOnlySet<string> active, Guid? deletedApplication = null)
    {
        var context = new AssemblyLoadContext("ApplicationDeployments build context retention", isCollectible: true);
        try
        {
            var prune = Bind<Action<string, IReadOnlySet<string>, Guid?>>(Load(context, resolvePath()), nameof(PruneBuildContexts));
            prune(root, active, deletedApplication);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ModulePackageException) { }
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

    private static PrepareBuildContext Bind(Type type) => Bind<PrepareBuildContext>(type, nameof(PrepareBuildContextAsync));

    private static T Bind<T>(Type type, string methodName) where T : Delegate
    {
        try
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(EntryType, methodName);
            return method.CreateDelegate<T>();
        }
        catch (Exception exception) when (exception is MissingMethodException or ArgumentException or AmbiguousMatchException)
        {
            throw new InvalidDataException("Application Deployments entry does not match its execution contract.", exception);
        }
    }
}
