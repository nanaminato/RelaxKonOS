using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace RelaxKonOS.Server.Certificate;

/// <summary>One bounded collectible generation, shared by request and durable operation scopes.</summary>
internal sealed class AcmeExecution
{
    internal static AcmeExecution Shared { get; } = new(TimeSpan.FromSeconds(30));
    private const int MaximumGenerations = 4;
    private readonly object gate = new();
    private readonly List<WeakReference> generations = [];
    private readonly TimeSpan idleDelay;
    private readonly Timer timer;
    private Generation? current;
    private int active;
    private long lastRelease;

    internal AcmeExecution(TimeSpan idleDelay)
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

    internal Lease Acquire(AcmeExecutionPorts ports, ILogger logger)
    {
        lock (gate)
        {
            current ??= Load();
            try
            {
                var executor = (IAcmeService)Activator.CreateInstance(current.Executor, ports, logger)!;
                active++;
                return new Lease(this, executor);
            }
            catch (Exception)
            {
                if (active == 0) ScheduleRelease();
                throw new InvalidDataException("Bundled ACME execution could not be activated.");
            }
        }
    }

    private Generation Load()
    {
        generations.RemoveAll(reference => !reference.IsAlive);
        if (generations.Count >= MaximumGenerations)
            throw new InvalidDataException("ACME execution code collection is pending.");
        var directory = Path.Combine(AppContext.BaseDirectory, "Modules", "Certificates");
        var path = Path.Combine(directory, "RelaxKonOS.ServerModule.Certificates.dll");
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Bundled ACME execution assembly is missing or linked.");

        var context = new ExecutionLoadContext(directory);
        var code = new WeakReference(context);
        generations.Add(code);
        try
        {
            var executor = context.LoadFromAssemblyPath(path).GetType("RelaxKonOS.Server.Certificate.AnvilAcmeExecutor", throwOnError: true)!;
            return new Generation(context, executor, code);
        }
        catch (Exception)
        {
            context.Unload();
            throw new InvalidDataException("Bundled ACME execution could not be loaded.");
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

    internal sealed class Lease(AcmeExecution owner, IAcmeService executor) : IDisposable
    {
        private IAcmeService? executor = executor;
        internal IAcmeService Executor => executor ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            lock (owner.gate)
            {
                if (executor is null) return;
                ((IDisposable)executor).Dispose();
                executor = null;
                if (--owner.active == 0) owner.ScheduleRelease();
            }
        }
    }

    private sealed record Generation(ExecutionLoadContext Context, Type Executor, WeakReference Code);

    private sealed class ExecutionLoadContext(string directory) : AssemblyLoadContext($"acme:{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "RelaxKonOS.Server" || name.Name?.StartsWith("RelaxKonOS.ServerModule.", StringComparison.Ordinal) == true
                && name.Name != "RelaxKonOS.ServerModule.Certificates.Contracts")
                throw new InvalidDataException("ACME execution cannot depend on Host or another implementation.");
            // Json.NET queries TypeDescriptor for SDK resource types. Its framework caches must
            // share the SDK lifetime or they retain those types in the default context.
            if (name.Name == "System.ComponentModel.TypeConverter")
                return LoadFromAssemblyPath(typeof(System.ComponentModel.TypeDescriptor).Assembly.Location);
            if (name.Name is "Certify.ACME.Anvil" or "BouncyCastle.Cryptography" or "Newtonsoft.Json")
            {
                var dependency = Path.Combine(directory, name.Name + ".dll");
                if (!File.Exists(dependency) || (File.GetAttributes(dependency) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Bundled ACME dependency is missing or linked.");
                return LoadFromAssemblyPath(dependency);
            }
            return null;
        }
    }
}
