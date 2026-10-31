using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

public interface IModuleStreamingTestService
{
    Stream Open(CancellationToken cancellationToken);
    Task<Stream> OpenAsync(CancellationToken cancellationToken);
    ValueTask<Stream> OpenValueAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<int> Items(CancellationToken cancellationToken);
}
public interface IUnsafeConcreteStream { MemoryStream Open(); }
public interface IUnsafeDeferredTask { Task<IAsyncEnumerable<int>> Items(); }
public interface IUnsafeHttpResult { IResult Open(); }

internal static class ModuleStreamingChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using (var fixture = new Fixture())
        {
            var sequence = fixture.Proxy.Items(default);
            await using (var unopened = sequence.GetAsyncEnumerator()) { }
            check(fixture.Factory.Created == 0, "creating or discarding an unenumerated module sequence does not load a runtime or reserve a lease");
            using var cancelled = new CancellationTokenSource();
            await using (var enumerator = sequence.GetAsyncEnumerator(cancelled.Token))
            {
                check(await enumerator.MoveNextAsync() && enumerator.Current == 1 && fixture.Leases == 1,
                    "module sequence acquires its runtime on enumeration and retains protection after yielding an item");
                check(await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) == "module-active-work",
                    "a suspended module enumerator blocks memory release");
                var pendingItem = enumerator.MoveNextAsync().AsTask(); cancelled.Cancel();
                try { await pendingItem; throw new Exception("Cancelled enumeration continued"); }
                catch (OperationCanceledException) { }
            }
            check(fixture.Leases == 0 && fixture.Factory.Last!.Service.EnumeratorsDisposed == 1,
                "enumerator cancellation propagates to the implementation and disposes it before returning the module lease");
            await fixture.Coordinator.TrySleepAsync(Fixture.Id, default);
            await using (var enumerator = sequence.GetAsyncEnumerator())
            { await enumerator.MoveNextAsync(); }
            check(fixture.Factory.Created == 2 && fixture.Leases == 0,
                "explicit repeated enumeration creates a new generation when needed and early disposal returns its lease");
            await using (var enumerator = sequence.GetAsyncEnumerator())
            {
                await enumerator.MoveNextAsync(); fixture.Factory.Last!.Service.FinishSequence = true;
                check(!await enumerator.MoveNextAsync() && fixture.Leases == 0,
                    "natural sequence completion cleans up its enumerator and returns module protection");
            }
            foreach (var open in new Func<Stream>[] {
                () => fixture.Proxy.Open(default), () => fixture.Proxy.OpenAsync(default).GetAwaiter().GetResult(),
                () => fixture.Proxy.OpenValueAsync(default).GetAwaiter().GetResult() })
            {
                var stream = open();
                check(fixture.Leases == 1 && await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) == "module-active-work",
                    "Stream, Task<Stream> and ValueTask<Stream> transfer ownership beyond method completion");
                await stream.DisposeAsync();
                check(fixture.Leases == 0, "stream cleanup returns transferred ownership exactly once");
            }
            var held = await fixture.Proxy.OpenAsync(default);
            var source = fixture.Factory.Last!.Service.LastStream!;
            source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var read = held.ReadAsync(new byte[1].AsMemory()).AsTask();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await held.DisposeAsync();
            check(fixture.Leases == 1 && await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) == "module-active-work",
                "stream disposal cannot release the module beneath an outstanding read operation");
            source.Pending.SetResult();
            check(await read == 1 && fixture.Leases == 0 && source.Disposals == 1,
                "the final outstanding stream operation returns ownership after cleanup completes");
            await held.DisposeAsync();
            check(source.Disposals == 1, "repeated stream cleanup does not replay underlying Dispose");
            foreach (var rejected in new Action[] {
                () => ModuleServiceProxy<IUnsafeConcreteStream>.Create(fixture.Coordinator, Fixture.Id),
                () => ModuleServiceProxy<IUnsafeDeferredTask>.Create(fixture.Coordinator, Fixture.Id),
                () => ModuleServiceProxy<IUnsafeHttpResult>.Create(fixture.Coordinator, Fixture.Id) })
            {
                try { rejected(); throw new Exception("Unsafe ownership contract accepted"); }
                catch (InvalidOperationException) { check(true, "module gate refuses a deferred ownership contract it cannot safely transfer"); }
            }
        }
        await using (var fixture = new Fixture())
        {
            var stream = await fixture.Proxy.OpenAsync(default);
            fixture.Factory.Last!.Service.LastStream!.FailDispose = true;
            try { await stream.DisposeAsync(); throw new Exception("Stream cleanup failure hidden"); }
            catch (IOException) { }
            check(fixture.Leases == 0 && await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) == "module-recovery-blocked",
                "failed stream cleanup quarantines its original runtime instead of reporting it idle");
            try { await fixture.Proxy.OpenAsync(default); throw new Exception("Quarantined stream runtime loaded"); }
            catch (ModuleUnavailableException) { }
            check(fixture.Factory.Created == 1, "failed stream cleanup cannot start a second module executor");
        }
        await using (var fixture = new Fixture())
        {
            await using var enumerator = fixture.Proxy.Items(default).GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
            fixture.Factory.Last!.Service.FailEnumeratorDispose = true;
            try { await enumerator.DisposeAsync(); throw new Exception("Enumerator cleanup failure hidden"); }
            catch (IOException) { }
            check(fixture.Leases == 0 && await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) == "module-recovery-blocked",
                "failed enumerator cleanup preserves runtime and dependency quarantine");
        }
        await VerifyHttpAsync(check);
    }

    private static async Task VerifyHttpAsync(Action<bool, string> check)
    {
        await using var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Proxy);
        builder.Services.AddAuthentication("module-test").AddScheme<AuthenticationSchemeOptions, ModuleTestAuthentication>("module-test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/module-stream", async (IModuleStreamingTestService service, CancellationToken ct) => Results.Stream(await service.OpenAsync(ct))).RequireAuthorization();
        app.Urls.Add("http://127.0.0.1:0"); await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new SocketsHttpHandler { MaxResponseDrainSize = 0 })
            { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        check((await client.GetAsync("/module-stream")).StatusCode == HttpStatusCode.Unauthorized && fixture.Factory.Created == 0,
            "an unauthenticated streaming request cannot create a module runtime");
        fixture.Factory.Http = true;
        client.DefaultRequestHeaders.Add("test-user", ModuleTestAuthentication.Admin);
        using (var response = await client.GetAsync("/module-stream", HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var source = fixture.Factory.Last!.Service.LastStream!;
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(fixture.Leases == 1 && await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) == "module-active-work",
                "a real HTTP stream remains leased while the response body is still transferring after the handler returns");
            source.Pending!.SetResult();
            check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 42 }), "the HTTP stream transfers its original bytes once");
        }
        await ZeroLeases(fixture);
        check(await fixture.Coordinator.TrySleepAsync(Fixture.Id, default) is null,
            "completed HTTP response cleanup permits module release");
        using (var response = await client.GetAsync("/module-stream", HttpCompletionOption.ResponseHeadersRead))
        {
            await fixture.Factory.Last!.Service.LastStream!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            response.Dispose();
        }
        await ZeroLeases(fixture);
        check(fixture.Factory.Last!.Service.LastStream!.Disposals == 1,
            "client stream abandonment cancels body transfer and disposes the stream before its module lease ends");
        await app.StopAsync();
    }
    private static async Task ZeroLeases(Fixture fixture)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        { if (fixture.Leases == 0) return; await Task.Delay(10); }
        throw new Exception("Module stream lease did not end");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Id = "module.stream";
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
        internal readonly Factory Factory = new();
        internal ModuleResidencyCoordinator Coordinator { get; }
        internal IModuleStreamingTestService Proxy { get; }
        internal int Leases => Coordinator.Describe(Id)!.ActiveLeases;
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder();
            var ids = new HashSet<string> { Id };
            Coordinator = new([Factory], ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), _services,
                ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"), _services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>());
            Proxy = ModuleServiceProxy<IModuleStreamingTestService>.Create(Coordinator, Id);
        }
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); await _services.DisposeAsync(); }
    }
    private sealed class Factory : IServerModuleRuntimeFactory
    {
        public string ModuleId => Fixture.Id;
        internal int Created; internal bool Http; internal Runtime? Last;
        public ValueTask<IServerModuleRuntime> CreateAsync(IServiceProvider services, ServerModuleContext context, CancellationToken ct)
        { Created++; return ValueTask.FromResult<IServerModuleRuntime>(Last = new Runtime(Http)); }
    }
    private sealed class Runtime : IServerModuleRuntime
    {
        internal bool Disposed; internal Service Service;
        internal Runtime(bool http) => Service = new(this, http);
        public string ModuleId => Fixture.Id;
        public object Resolve(Type type) => Service;
        public ValueTask<ModuleUnloadBlocker?> CanUnloadAsync(CancellationToken ct) => ValueTask.FromResult<ModuleUnloadBlocker?>(null);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Service(Runtime owner, bool http) : IModuleStreamingTestService
    {
        internal ControlledStream? LastStream; internal int EnumeratorsDisposed; internal bool FailEnumeratorDispose; internal bool FinishSequence;
        public Stream Open(CancellationToken ct) => LastStream = new ControlledStream(owner, http);
        public Task<Stream> OpenAsync(CancellationToken ct) => Task.FromResult(Open(ct));
        public ValueTask<Stream> OpenValueAsync(CancellationToken ct) => ValueTask.FromResult(Open(ct));
        public IAsyncEnumerable<int> Items(CancellationToken ct) => new Sequence(this, ct);
        private sealed class Sequence(Service service, CancellationToken source) : IAsyncEnumerable<int>
        {
            public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken ct = default) => new Enumerator(service, source, ct);
        }
        private sealed class Enumerator(Service service, CancellationToken source, CancellationToken ct) : IAsyncEnumerator<int>
        {
            private int _index; private bool _disposed;
            public int Current => _index;
            public async ValueTask<bool> MoveNextAsync()
            {
                source.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
                if (_index++ == 0) return true;
                if (service.FinishSequence) return false;
                await Task.Delay(Timeout.Infinite, ct); return false;
            }
            public ValueTask DisposeAsync()
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true; service.EnumeratorsDisposed++;
                if (service.FailEnumeratorDispose) throw new IOException("injected enumerator cleanup failure");
                return ValueTask.CompletedTask;
            }
        }
    }
    private sealed class ControlledStream(Runtime owner, bool http) : Stream
    {
        internal TaskCompletionSource? Pending = http ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Disposals; internal bool FailDispose; private int _reads;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (owner.Disposed) throw new Exception("module disposed during stream read");
            if (http && _reads++ == 0) { buffer.Span[0] = 42; return 1; }
            if (Pending is { } pending) { Entered.TrySetResult(); await pending.Task.WaitAsync(ct); }
            if (owner.Disposed) throw new Exception("module disposed beneath pending stream operation");
            if (http) return 0;
            buffer.Span[0] = 42; return 1;
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { Disposals++; if (FailDispose) throw new IOException("injected stream cleanup failure"); } base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
