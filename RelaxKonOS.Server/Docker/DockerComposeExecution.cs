using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Docker;

/// <summary>One bounded collectible generation, shared by request and durable operation scopes.</summary>
internal sealed class DockerComposeExecution
{
    internal const string AssemblyFile = "RelaxKonOS.ServerModule.Docker.dll";
    internal const string EntryType = "RelaxKonOS.Server.Docker.DockerComposeExecutor";
    internal const string EngineControlEntryType = "RelaxKonOS.Server.Docker.DockerEngineControlExecution";
    internal const string ImageMirrorEntryType = "RelaxKonOS.Server.Docker.DockerImageMirrorExecution";
    internal const string ProxyProjectionEntryType = "RelaxKonOS.Server.Docker.DockerProxyProjection";
    internal const string StackOutcomeEntryType = "RelaxKonOS.Server.Docker.DockerStackOutcomePolicy";
    internal static DockerComposeExecution Shared { get; } = new(TimeSpan.FromSeconds(30));
    private const int MaximumGenerations = 4;
    private readonly object gate = new();
    private readonly List<WeakReference> generations = [];
    private readonly TimeSpan idleDelay;
    private readonly Func<string> resolveEntry;
    private readonly Timer timer;
    private Generation? current;
    private int active;
    private long lastRelease;
    private bool recoveryRequired;

    internal DockerComposeExecution(TimeSpan idleDelay, Func<string>? resolveEntry = null)
    {
        this.idleDelay = idleDelay;
        this.resolveEntry = resolveEntry ?? ResolveBundledPath;
        if (ExecutionContext.IsFlowSuppressed()) timer = CreateTimer();
        else
        {
            using var flow = ExecutionContext.SuppressFlow();
            timer = CreateTimer();
        }
    }

    private Timer CreateTimer() => new(_ => ReleaseIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    internal WeakReference? CurrentCode { get { lock (gate) return current?.Code; } }

    internal Lease<IDockerComposeService> Acquire(string dataDirectory) => Acquire<IDockerComposeService>(EntryType, dataDirectory);

    internal Lease<T> Acquire<T>(string entryType, object? argument = null) where T : class
    {
        lock (gate)
        {
            if (recoveryRequired) throw new DockerComposeCleanupException();
            current ??= Load();
            try
            {
                var type = current.Assembly.GetType(entryType, throwOnError: true)!;
                var executor = (T)(argument is null ? Activator.CreateInstance(type) : Activator.CreateInstance(type, argument))!;
                active++;
                return new Lease<T>(this, executor);
            }
            catch (Exception)
            {
                if (active == 0) ScheduleRelease();
                throw new InvalidDataException("Docker Compose execution could not be activated.");
            }
        }
    }

    private Generation Load()
    {
        generations.RemoveAll(reference => !reference.IsAlive);
        if (generations.Count >= MaximumGenerations)
            throw new InvalidDataException("Docker Compose execution code collection is pending.");
        var path = resolveEntry();

        var context = new ExecutionLoadContext();
        var code = new WeakReference(context);
        generations.Add(code);
        try
        {
            var assembly = ReadAssembly(context, path);
            return new Generation(context, assembly, code);
        }
        catch (Exception)
        {
            context.Unload();
            throw new InvalidDataException("Docker Compose execution could not be loaded.");
        }
    }

    internal static void ValidateEntryPoints(string path)
    {
        var context = new ExecutionLoadContext();
        try { _ = ReadAssembly(context, path); }
        finally { context.Unload(); }
    }

    private static Assembly ReadAssembly(ExecutionLoadContext context, string path)
    {
        using var stream = File.OpenRead(path);
        var assembly = context.LoadFromStream(stream);
        RequireEntry<IDockerComposeService>(EntryType, typeof(string));
        RequireEntry<IDockerEngineControlService>(EngineControlEntryType, typeof(IDockerEngineControlHost));
        RequireEntry<IDockerImageMirrorExecution>(ImageMirrorEntryType);
        RequireEntry<IDockerProxyProjection>(ProxyProjectionEntryType);
        RequireEntry<IDockerStackOutcomePolicy>(StackOutcomeEntryType);
        return assembly;

        void RequireEntry<T>(string name, params Type[] arguments)
        {
            var entry = assembly.GetType(name);
            if (entry is null || entry.IsAbstract || !typeof(T).IsAssignableFrom(entry) || entry.GetConstructor(arguments) is null)
                throw new InvalidDataException("Docker execution assembly does not implement the required module entry points.");
        }
    }

    private static string ResolveBundledPath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Docker");
        var path = Path.Combine(directory, AssemblyFile);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Docker Compose execution assembly is missing or linked.");
        return path;
    }

    private void ScheduleRelease()
    {
        lastRelease = Stopwatch.GetTimestamp();
        timer.Change(idleDelay, Timeout.InfiniteTimeSpan);
    }

    private void ReleaseIdle()
    {
        lock (gate)
        {
            if (active != 0 || current is null || recoveryRequired) return;
            var remaining = idleDelay - Stopwatch.GetElapsedTime(lastRelease);
            if (remaining > TimeSpan.Zero)
            {
                timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            generations.RemoveAll(reference => !reference.IsAlive);
            // Retain a usable generation if older contexts have not been collected yet.
            if (generations.Count >= MaximumGenerations)
            {
                timer.Change(idleDelay, Timeout.InfiniteTimeSpan);
                return;
            }
            var idle = current;
            current = null;
            idle.Context.Unload();
        }
    }

    internal sealed class Lease<T>(DockerComposeExecution owner, T executor) : IDisposable where T : class
    {
        private T? executor = executor;
        internal T Executor => executor ?? throw new ObjectDisposedException(nameof(Lease<T>));

        internal void RequireRecovery()
        {
            lock (owner.gate) owner.recoveryRequired = true;
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (executor is null) return;
                executor = null;
                if (--owner.active == 0 && !owner.recoveryRequired) owner.ScheduleRelease();
            }
        }
    }

    private sealed record Generation(ExecutionLoadContext Context, Assembly Assembly, WeakReference Code);

    private sealed class ExecutionLoadContext() : AssemblyLoadContext($"docker-compose:{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "RelaxKonOS.Server" || name.Name?.StartsWith("RelaxKonOS.ServerModule.", StringComparison.Ordinal) == true
                && name.Name != "RelaxKonOS.ServerModule.Docker.Contracts")
                throw new InvalidDataException("Docker Compose execution cannot depend on Host or another implementation.");
            return null;
        }
    }
}
