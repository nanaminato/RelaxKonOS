using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

internal static class QueueBoundaryChecks
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "proxy-server-queue-" + Guid.NewGuid().ToString("N"));
        var state = new State(); var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization(options =>
        { foreach (var name in new[] { "ProxyRead", "ProxyManage", "ProxyServerExport" }) options.AddPolicy(name, policy => policy.RequireAssertion(_ => true)); });
        builder.Services.AddSingleton<IProxyPlatformPaths>(new Paths(root)); builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(new Helper()); builder.Services.AddSingleton<IServerModeResolver>(new TestMode());
        builder.Services.AddSingleton<ProxyServerStore>(); builder.Services.AddSingleton<IProxyWorkLeaseSource, TestProxyWorkLeases>(); builder.Services.AddSingleton<ProxyOperationStore>(); builder.Services.AddSingleton<ProxyAuditStore>();
        var responseLeases = new TestProxyWorkLeases();
        builder.Services.AddSingleton<RelaxKonOS.Server.Residency.IResidentModuleWorkLeaseSource>(responseLeases);
        RegisterProxyGeneration(builder);
        builder.Services.AddHostedService(services => services.GetRequiredService<ProxyOperationStore>());
        builder.Services.AddScoped<ProxyServerOperationQueue>(); builder.Services.AddTransient<ProxyServerService>();
        builder.Services.AddScoped<IProxyEngineRegistry>(_ => new Engines(state));
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Hold-Read"))
                context.Response.OnStarting(async () =>
                {
                    reading.TrySetResult();
                    await releaseRead.Task.WaitAsync(budget.Token);
                });
            if (!context.Request.Headers.ContainsKey("X-Hold-Receipt"))
            {
                await next(context);
                return;
            }
            using var registration = context.RequestAborted.Register(() => aborted.TrySetResult());
            context.Response.OnStarting(async () =>
            {
                writing.TrySetResult();
                await releaseResponse.Task.WaitAsync(budget.Token);
            });
            try { await next(context); }
            finally { responseExited.TrySetResult(); }
        });
        app.UseAuthorization();
        app.MapProxyServerEndpoints();
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) }; client.DefaultRequestHeaders.Add("Idempotency-Key", "queued-config");
            using var response = await client.PutAsJsonAsync(ProxyServerRoutes.Configuration, new SaveProxyServerConfiguration(0, []));
            Require(response.StatusCode == HttpStatusCode.Accepted, "The current configuration route must retain 202 acceptance.");
            await state.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(state.Created == 1 && state.Disposed == 0, "Only the execution scope may construct the engine registry; the request does not construct a management facade.");
            using var replay = await client.PutAsJsonAsync(ProxyServerRoutes.Configuration, new SaveProxyServerConfiguration(0, []));
            Require(replay.StatusCode == HttpStatusCode.Accepted && state.Created == 1, "Replay returns the protected receipt without another execution graph.");
            Require(responseLeases.Acquired == 2, "Original and replay responses each acquire their own lease.");
            var original = await response.Content.ReadFromJsonAsync<ProxyOperationAcceptedDto>();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            using var held = new HttpRequestMessage(HttpMethod.Put, ProxyServerRoutes.Configuration)
                { Content = JsonContent.Create(new SaveProxyServerConfiguration(0, [])) };
            held.Headers.Add("X-Hold-Receipt", "1");
            var send = client.SendAsync(held, cancellation.Token);
            try
            {
                await writing.Task.WaitAsync(budget.Token);
                cancellation.Cancel();
                await aborted.Task.WaitAsync(budget.Token);
                try { using var unexpected = await send; throw new InvalidOperationException("Expected receipt client disconnect."); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                Require(responseLeases.Active == 1 && !responseExited.Task.IsCompleted,
                    "The disconnected replay keeps its response lease until execution exits.");
            }
            finally { releaseResponse.TrySetResult(); }
            await responseExited.Task.WaitAsync(budget.Token);
            Require(responseLeases.Active == 0 && state.Created == 1, "Response drain must not create another operation.");
            using var retry = await client.PutAsJsonAsync(ProxyServerRoutes.Configuration, new SaveProxyServerConfiguration(0, []), budget.Token);
            var retried = await retry.Content.ReadFromJsonAsync<ProxyOperationAcceptedDto>(budget.Token);
            Require(original is not null && retried?.OperationId == original.OperationId && state.Created == 1,
                "The retry after disconnect must return the original operation ID.");
            Console.WriteLine($"PASS PROXY HTTP RECEIPT DRAIN: operation={original!.OperationId}");
            using var readRequest = new HttpRequestMessage(HttpMethod.Get, ProxyServerRoutes.Root + "/operations/" + original.OperationId);
            readRequest.Headers.Add("X-Hold-Read", "1");
            var readingReceipt = client.SendAsync(readRequest, budget.Token);
            var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
            try
            {
                await reading.Task.WaitAsync(budget.Token);
                Require(!readingReceipt.IsCompleted && coordinator.Describe(ServerModules.ProxyId) is { ActiveLeases: 1 },
                    "An inbound operation read retains its original generation while writing the response.");
            }
            finally { releaseRead.TrySetResult(); }
            using var readReceipt = await readingReceipt;
            Require(readReceipt.IsSuccessStatusCode && state.Created == 1, "Reading the original operation does not start another executor.");
            while (coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != 0) await Task.Delay(20, budget.Token);
            var stop = app.StopAsync(); Require(!stop.IsCompleted, "Shutdown must wait for the inbound operation's async cleanup.");
            state.Release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Require(state.Disposed == 1 && (await app.Services.GetRequiredService<ProxyServerStore>().ReadAsync(default)).Configuration.Revision == 1,
                "The operation must commit once and finish its independent scope.");
            Console.WriteLine("Proxy Server queue boundary: production HTTP acceptance, no request management graph, receipt replay, async cleanup and host drain passed.");
        }
        finally
        {
            state.Release.TrySetResult(); await app.StopAsync(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    internal static void RegisterProxyGeneration(WebApplicationBuilder builder)
    {
        var ids = new HashSet<string> { ServerModules.ProxyId };
        var module = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([new ProxyWorkRuntimeFactory()],
            ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services, module,
            NullLogger<ModuleResidencyCoordinator>.Instance, residentWorkModuleIds: ids));
    }
    private static void Require(bool ok, string label) { if (!ok) throw new Exception(label); }
    private sealed class State
    {
        internal int Created, Disposed;
        internal TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Engines(State state) : IProxyEngineRegistry, IAsyncDisposable
    {
        private readonly int created = Interlocked.Increment(ref state.Created);
        public IProxyEngine? Find(string id) => null;
        public IReadOnlyList<IProxyEngine> List() => [];
        public async ValueTask DisposeAsync() { _ = created; state.Disposing.TrySetResult(); await state.Release.Task; Interlocked.Increment(ref state.Disposed); }
    }
}
