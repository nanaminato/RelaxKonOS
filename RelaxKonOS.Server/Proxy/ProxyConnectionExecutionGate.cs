using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;

namespace RelaxKonOS.Server.Proxy;

/// <summary>One reusable parser generation, kept alive through the actual response read and pipe cleanup.</summary>
internal sealed class ProxyConnectionExecutionGate
{
    internal static ProxyConnectionExecutionGate Shared { get; } = new(TimeSpan.FromSeconds(30));
    internal const int MaximumOutstandingGenerations = 4;
    private const int MaximumRecordBytes = 256 * 1024;
    private const int MaximumSnapshotBytes = 32 * 1024 * 1024;
    private readonly object gate = new();
    private readonly List<WeakReference> generations = [];
    private readonly Timer timer;
    private readonly TimeSpan idleDelay;
    private Generation? current;
    private int active;
    private long lastRelease;

    internal ProxyConnectionExecutionGate(TimeSpan idleDelay)
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

    internal WeakReference? CurrentCode
    {
        get { lock (gate) return current?.Code; }
    }

    internal async Task<int> ReadAsync(Stream stream, Action<ProxyConnectionSnapshotEntry>? project, CancellationToken ct) =>
        (await ReadSnapshotAsync(stream, project, statisticsOnly: false, ct)).Count;

    internal async Task<ProxyConnectionStatistics> ReadStatisticsAsync(Stream stream, CancellationToken ct) =>
        (await ReadSnapshotAsync(stream, null, statisticsOnly: true, ct)).Statistics;

    private async Task<(int Count, ProxyConnectionStatistics Statistics)> ReadSnapshotAsync(Stream stream,
        Action<ProxyConnectionSnapshotEntry>? project, bool statisticsOnly, CancellationToken ct)
    {
        Generation? generation = null;
        try
        {
            await using var ownedStream = stream;
            ct.ThrowIfCancellationRequested();
            generation = Acquire();
            var parser = CreateReader(generation.Reader, project, statisticsOnly);
            var pipe = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
            long consumed = 0;
            try
            {
                while (true)
                {
                    var read = await pipe.ReadAsync(ct);
                    var buffer = read.Buffer;
                    if (consumed + buffer.Length > MaximumSnapshotBytes) throw new JsonException();
                    var parsed = Parse(parser, buffer, read.IsCompleted, ct);
                    consumed += parsed;
                    if (buffer.Length - parsed > MaximumRecordBytes) throw new JsonException();
                    pipe.AdvanceTo(buffer.GetPosition(parsed), buffer.End);
                    if (read.IsCompleted) return (parser.Count, statisticsOnly ? parser.Statistics : default);
                }
            }
            finally { await pipe.CompleteAsync(); }
        }
        finally
        {
            if (generation is not null) Release();
        }
    }

    private void Release()
    {
        lock (gate)
        {
            if (--active != 0) return;
            lastRelease = Stopwatch.GetTimestamp();
            timer.Change(idleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private Generation Acquire()
    {
        lock (gate)
        {
            if (current is null)
            {
                generations.RemoveAll(reference => !reference.IsAlive);
                if (generations.Count >= MaximumOutstandingGenerations)
                    throw new InvalidDataException("Proxy connection parser code collection is pending.");
                var context = new ProxyExecutionLoadContext("connections");
                var code = new WeakReference(context);
                generations.Add(code);
                try
                {
                    var reader = context.LoadFromAssemblyPath(ProxyExecutionLoadContext.BundledAssemblyPath())
                        .GetType("RelaxKonOS.Server.Proxy.Mihomo.MihomoConnectionSnapshotReader", throwOnError: true)!;
                    current = new Generation(context, reader, code);
                }
                catch (Exception)
                {
                    context.Unload();
                    throw new InvalidDataException("Proxy connection parser could not be loaded.");
                }
            }
            active++;
            return current;
        }
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
            // Preserve a usable generation when the GC has not reclaimed older code yet.
            // Releasing the last one at the limit would make low-traffic reads depend on GC.
            generations.RemoveAll(reference => !reference.IsAlive);
            if (generations.Count >= MaximumOutstandingGenerations)
            {
                timer.Change(idleDelay, Timeout.InfiniteTimeSpan);
                return;
            }
            var idle = current;
            current = null;
            idle.Context.Unload();
        }
    }

    private static IProxyConnectionSnapshotReader CreateReader(Type type, Action<ProxyConnectionSnapshotEntry>? project, bool statisticsOnly)
    {
        try { return (IProxyConnectionSnapshotReader)Activator.CreateInstance(type, [project, statisticsOnly])!; }
        catch (Exception) { throw new InvalidDataException("Proxy connection parser could not be created."); }
    }

    private static long Parse(IProxyConnectionSnapshotReader reader, System.Buffers.ReadOnlySequence<byte> buffer, bool final, CancellationToken ct)
    {
        try { return reader.Parse(buffer, final, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception) { throw new JsonException("Invalid Proxy connection snapshot."); }
    }

    private sealed record Generation(ProxyExecutionLoadContext Context, Type Reader, WeakReference Code);
}
