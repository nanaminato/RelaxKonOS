using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.WebServer;

/// <summary>One bounded collectible generation, shared by request and durable operation scopes.</summary>
internal sealed class WebServerExecutionCode
{
    internal static WebServerExecutionCode Shared { get; } = new(TimeSpan.FromSeconds(30));
    private const int MaximumGenerations = 4;
    private readonly object gate = new();
    private readonly List<WeakReference> generations = [];
    private readonly TimeSpan idleDelay;
    private readonly Timer timer;
    private Generation? current;
    private int active;
    private long lastRelease;

    internal WebServerExecutionCode(TimeSpan idleDelay)
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

    internal Lease<T> Acquire<T>(string executorType, object ports) where T : class
    {
        lock (gate)
        {
            current ??= Load();
            try
            {
                var type = current.Assembly.GetType(executorType, throwOnError: true)!;
                var executor = (T)Activator.CreateInstance(type, ports)!;
                active++;
                return new Lease<T>(this, executor);
            }
            catch (Exception)
            {
                if (active == 0) ScheduleRelease();
                throw new InvalidDataException("Bundled WebServer execution could not be activated.");
            }
        }
    }

    private Generation Load()
    {
        generations.RemoveAll(reference => !reference.IsAlive);
        if (generations.Count >= MaximumGenerations)
            throw new InvalidDataException("WebServer execution code collection is pending.");
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "WebServer");
        var path = Path.Combine(directory, "RelaxKonOS.ServerModule.WebServer.dll");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled WebServer execution assembly is missing or linked.");

        var context = new ExecutionLoadContext();
        var code = new WeakReference(context);
        generations.Add(code);
        try
        {
            return new Generation(context, context.LoadFromAssemblyPath(path), code);
        }
        catch (Exception)
        {
            context.Unload();
            throw new InvalidDataException("Bundled WebServer execution could not be loaded.");
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
            if (active != 0 || current is null) return;
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

    internal sealed class Lease<T>(WebServerExecutionCode owner, T executor) : IDisposable where T : class
    {
        private T? executor = executor;
        internal T Executor => executor ?? throw new ObjectDisposedException(nameof(Lease<T>));

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (executor is null) return;
                executor = null;
                if (--owner.active == 0) owner.ScheduleRelease();
            }
        }
    }

    private sealed record Generation(ExecutionLoadContext Context, Assembly Assembly, WeakReference Code);

    private sealed class ExecutionLoadContext() : AssemblyLoadContext($"webserver-execution:{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "RelaxKonOS.Server" || name.Name?.StartsWith("RelaxKonOS.ServerModule.", StringComparison.Ordinal) == true
                && name.Name != "RelaxKonOS.ServerModule.WebServer.Contracts")
                throw new InvalidDataException("WebServer execution cannot depend on Host or another implementation.");
            return null;
        }
    }
}
