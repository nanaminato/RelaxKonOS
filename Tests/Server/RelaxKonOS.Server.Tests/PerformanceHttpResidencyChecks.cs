using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.SystemPerformance;

internal static class PerformanceHttpResidencyChecks
{
    internal static async Task RunAsync()
    {
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS HTTP RESIDENCY {++count}: {text}"); }
        await using (var f = new Fixture())
        {
            Check(f.Endpoint(SystemMonitorApiRoutes.PerformanceInfo).Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Any(m => m.StatusCode == 200 && m.Type == typeof(PerformanceInfoDto))
                && f.Endpoint(SystemMonitorApiRoutes.PerformanceHistory).Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Any(m => m.StatusCode == 200 && m.Type == typeof(IReadOnlyList<PerformanceRealtimeSnapshotDto>)),
                "Wrapped info and history endpoints retain their exact successful response metadata");
            using var request = new CancellationTokenSource();
            var context = f.Context(request.Token);
            var stream = new ObservedStream(() => f.Active); context.Response.Body = stream;
            await new SystemPerformanceResult(Results.Ok(new { value = "protected" })).ExecuteAsync(context);
            Check(Encoding.UTF8.GetString(stream.ToArray()).Contains("protected") && stream.MinimumLeases == 1,
                "Actual Results.Ok JSON writes execute while a real coordinator lease is held");
            Check(f.Active == 0 && context.RequestAborted == request.Token, "Successful JSON execution releases protection and restores the original request token");
            context = f.Context(request.Token); stream = new ObservedStream(() => f.Active); context.Response.Body = stream;
            await new SystemPerformanceResult(Results.File(new byte[] { 1, 2, 3, 4 }, "image/png")).ExecuteAsync(context);
            Check(stream.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }) && stream.MinimumLeases == 1 && context.Response.ContentType == "image/png",
                "Actual Results.File byte transfer executes under work protection without changing content");
            Check(f.Active == 0 && context.RequestAborted == request.Token, "File completion releases its lease and restores the caller token");
            var blocked = new ControlledResult(); context = f.Context(request.Token);
            var execution = new SystemPerformanceResult(blocked).ExecuteAsync(context); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active == 1 && !execution.IsCompleted, "An unfinished response execution owns its actual work lease");
            request.Cancel();
            Check(blocked.Token.IsCancellationRequested && !execution.IsCompleted && f.Active == 1,
                "Client cancellation signals the response but cannot detach a non-cooperative result Task");
            blocked.Done.TrySetResult();
            try { await execution; throw new Exception("Cancelled response succeeded"); } catch (OperationCanceledException) { }
            Check(f.Active == 0 && context.RequestAborted == request.Token, "Actual cancelled response completion returns protection and restores the original token");
        }
        await using (var f = new Fixture())
        {
            using var request = new CancellationTokenSource(); var context = f.Context(request.Token); var blocked = new ControlledResult();
            var execution = new SystemPerformanceResult(blocked).ExecuteAsync(context); await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = f.Coordinator.DisposeAsync().AsTask();
            Check(blocked.Token.IsCancellationRequested && !shutdown.IsCompleted && f.Active == 1 && !execution.IsCompleted,
                "Coordinator stop signals the response token and waits for the real unfinished execution");
            blocked.Done.TrySetResult();
            try { await execution; throw new Exception("Stopped response succeeded"); } catch (OperationCanceledException) { }
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active == 0 && context.RequestAborted == request.Token, "Coordinator drain follows actual response completion rather than cancellation notification");
            var refused = new ControlledResult();
            try { await new SystemPerformanceResult(refused).ExecuteAsync(f.Context()); throw new Exception("Closed coordinator accepted response"); }
            catch (ModuleUnavailableException) { }
            Check(!refused.Entered.Task.IsCompleted, "Closed coordinator refuses response execution without invoking the inner result");
        }
        await using (var f = new Fixture())
        {
            using var request = new CancellationTokenSource(); var context = f.Context(request.Token); var failed = new ControlledResult();
            failed.Done.TrySetException(new IOException("test write"));
            try { await new SystemPerformanceResult(failed).ExecuteAsync(context); throw new Exception("Write failure hidden"); } catch (IOException) { }
            Check(f.Active == 0 && context.RequestAborted == request.Token, "Response failure propagates while returning its lease and restoring the original request token");
            request.Cancel(); var cancelled = new ControlledResult(); var acquired = f.Source.Acquisitions;
            try { await new SystemPerformanceResult(cancelled).ExecuteAsync(context); throw new Exception("Precancelled result accepted"); } catch (OperationCanceledException) { }
            Check(!cancelled.Entered.Task.IsCompleted && f.Source.Acquisitions == acquired, "Precancelled response performs neither lease admission nor inner execution");
        }
        await using (var f = new Fixture())
        {
            var denied = f.KillContext(true);
            await f.KillEndpoint.RequestDelegate!(denied);
            Check(denied.Response.StatusCode == 403 && f.Processes.Kills == 0 && f.Source.Acquisitions == 0,
                "Actual process endpoint rejects insufficient elevation before business or response lease admission");
            f.Processes.BeforeReturn = () => f.Coordinator.DisposeAsync().AsTask();
            var response = f.KillContext(false);
            try { await f.KillEndpoint.RequestDelegate!(response); throw new Exception("Stopped result accepted"); } catch (ModuleUnavailableException) { }
            Check(f.Processes.Kills == 1 && response.Response.Body.Length == 0,
                "Response admission failure after a termination receipt cannot replay the actual endpoint business operation");
        }
        Console.WriteLine($"Performance HTTP residency checks passed: {count}");
    }

    private sealed class ControlledResult : IResult
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        public async Task ExecuteAsync(HttpContext context)
        { Token = context.RequestAborted; Entered.TrySetResult(); await Done.Task; }
    }
    private sealed class ObservedStream(Func<int> leases) : MemoryStream
    {
        internal int MinimumLeases = int.MaxValue;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { MinimumLeases = Math.Min(MinimumLeases, leases()); return base.WriteAsync(buffer, ct); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { MinimumLeases = Math.Min(MinimumLeases, leases()); return base.WriteAsync(buffer, offset, count, ct); }
    }
    private sealed class Source(ISystemPerformanceWorkLeaseSource inner) : ISystemPerformanceWorkLeaseSource
    {
        internal int Acquisitions;
        public ValueTask<IModuleWorkLease> AcquireAsync(CancellationToken ct)
        { Interlocked.Increment(ref Acquisitions); return inner.AcquireAsync(ct); }
    }
    private sealed class Processes : IProcessService
    {
        internal int Kills;
        internal Func<Task>? BeforeReturn;
        public async Task<KillProcessResultDto> KillAsync(int id, DateTimeOffset start, bool elevated = false, CancellationToken ct = default)
        { Kills++; if (BeforeReturn is { } action) await action(); return new(true, false, "", null); }
        public Task<ProcessPageDto> QueryAsync(int page, int size, string? filter, string? sort, bool descending, bool elevated = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProcessIconContent?> GetIconAsync(int id, DateTimeOffset start, bool elevated = false, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Grants : IHostElevationSessionStore
    {
        public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target) => false;
        public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target, bool descendants, string method, string? correlationId = null) => throw new NotSupportedException();
        public void Revoke(ClaimsPrincipal principal) => throw new NotSupportedException();
    }
    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal readonly Processes Processes = new();
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal Source Source => (Source)_app.Services.GetRequiredService<ISystemPerformanceWorkLeaseSource>();
        internal int Active => Coordinator.Describe(ServerModules.SystemPerformanceId)?.ActiveLeases ?? 0;
        internal RouteEndpoint KillEndpoint { get; }
        internal RouteEndpoint Endpoint(string route) => ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == route);
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.SystemPerformanceId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            builder.Services.RemoveAll<ISystemPerformanceWorkLeaseSource>();
            builder.Services.AddSingleton<ISystemPerformanceWorkLeaseSource>(sp => new Source(new SystemPerformanceWorkLeaseSource(sp.GetRequiredService<ModuleResidencyCoordinator>())));
            builder.Services.AddSingleton<IProcessService>(Processes); builder.Services.AddSingleton<IHostElevationSessionStore, Grants>();
            _app = builder.Build(); _app.MapSystemMonitorEndpoints();
            KillEndpoint = ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(endpoint => endpoint.RoutePattern.RawText == SystemMonitorApiRoutes.ProcessKill);
        }
        internal DefaultHttpContext Context(CancellationToken request = default)
        {
            var context = new DefaultHttpContext { RequestServices = _app.Services, RequestAborted = request };
            context.Response.Body = new MemoryStream(); return context;
        }
        internal DefaultHttpContext KillContext(bool elevated)
        {
            var context = Context(); context.Request.Method = "DELETE"; context.Request.RouteValues["id"] = "42";
            var body = Encoding.UTF8.GetBytes("{\"expectedStartTime\":\"2026-10-01T00:00:00Z\",\"elevated\":" + (elevated ? "true" : "false") + "}");
            context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length;
            context.Request.Body = new MemoryStream(body); context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
            return context;
        }
        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
