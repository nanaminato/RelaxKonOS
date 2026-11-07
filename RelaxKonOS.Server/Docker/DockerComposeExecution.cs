using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Docker;

/// <summary>One bounded collectible generation, shared by request and durable operation scopes.</summary>
internal sealed class DockerComposeExecution
{
    internal static DockerComposeExecution Shared { get; } = new(TimeSpan.FromSeconds(30));
    private const int MaximumGenerations = 4;
    private readonly object gate = new();
    private readonly List<WeakReference> generations = [];
    private readonly TimeSpan idleDelay;
    private readonly Timer timer;
    private Generation? current;
    private int active;
    private long lastRelease;
    private bool recoveryRequired;

    internal DockerComposeExecution(TimeSpan idleDelay)
    {
        this.idleDelay = idleDelay;
        if (ExecutionContext.IsFlowSuppressed()) timer = CreateTimer();
        else
        {
            using var flow = ExecutionContext.SuppressFlow();
            timer = CreateTimer();
        }
    }

    private Timer CreateTimer() => new(_ => ReleaseIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    internal WeakReference? CurrentCode { get { lock (gate) return current?.Code; } }

    internal Lease Acquire(string dataDirectory)
    {
        lock (gate)
        {
            if (recoveryRequired) throw new DockerComposeCleanupException();
            current ??= Load();
            try
            {
                var executor = (IDockerComposeService)Activator.CreateInstance(current.Executor, dataDirectory)!;
                active++;
                return new Lease(this, executor);
            }
            catch (Exception)
            {
                if (active == 0) ScheduleRelease();
                throw new InvalidDataException("Bundled Docker Compose execution could not be activated.");
            }
        }
    }

    private Generation Load()
    {
        generations.RemoveAll(reference => !reference.IsAlive);
        if (generations.Count >= MaximumGenerations)
            throw new InvalidDataException("Docker Compose execution code collection is pending.");
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Docker");
        var path = Path.Combine(directory, "RelaxKonOS.ServerModule.Docker.dll");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled Docker Compose execution assembly is missing or linked.");

        var context = new ExecutionLoadContext();
        var code = new WeakReference(context);
        generations.Add(code);
        try
        {
            var executor = context.LoadFromAssemblyPath(path).GetType("RelaxKonOS.Server.Docker.DockerComposeExecutor", throwOnError: true)!;
            return new Generation(context, executor, code);
        }
        catch (Exception)
        {
            context.Unload();
            throw new InvalidDataException("Bundled Docker Compose execution could not be loaded.");
        }
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

    internal sealed class Lease(DockerComposeExecution owner, IDockerComposeService executor) : IDisposable
    {
        private IDockerComposeService? executor = executor;
        internal IDockerComposeService Executor => executor ?? throw new ObjectDisposedException(nameof(Lease));

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

    private sealed record Generation(ExecutionLoadContext Context, Type Executor, WeakReference Code);

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
