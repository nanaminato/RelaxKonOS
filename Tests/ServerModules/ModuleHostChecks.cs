using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Residency;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.ServerModuleHost;

internal static class ModuleHostChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "module-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rid = RelaxKonOS.ModulePackaging.ModulePackagePlatform.CurrentRid;
            var priorRid = Environment.GetEnvironmentVariable("DOTNET_RUNTIME_ID");
            try
            {
                Environment.SetEnvironmentVariable("DOTNET_RUNTIME_ID", "different-distro-wrong-arch");
                check(RelaxKonOS.ModulePackaging.ModulePackagePlatform.CurrentRid == rid,
                    "Module target identity is independent of a parent runtime RID environment override");
            }
            finally { Environment.SetEnvironmentVariable("DOTNET_RUNTIME_ID", priorRid); }
            var relative = "server/" + rid + "/HostFixture.dll";
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HostFixture.dll"), path);
            var bytes = await File.ReadAllBytesAsync(path);
            var manifest = new
            {
                schemaVersion = 3,
                packageKind = "server-module",
                runtime = "module-host",
                releaseId = "fixture-host-release",
                publisherId = "fixture",
                purpose = "third-party",
                rid,
                hostApiMinimum = "2.0.0",
                hostApiMaximumExclusive = "3.0.0",
                buildId = "fixture-build",
                sourceCommit = new string('a', 40),
                dependencies = Array.Empty<object>(),
                capabilities = Array.Empty<string>(),
                privileges = Array.Empty<object>(),
                ui = new { name = "Fixture", summary = "", description = "", homepage = (string?)null, license = "MIT" },
                moduleId = "fixture-host",
                version = "1.0.0",
                entryAssembly = relative,
                entryType = "HostFixture.FixtureHostEntry",
                files = new[]
                {
                    new { path = relative, length = bytes.Length,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var output = new StringWriter();
            var exit = await ModuleHostProgram.RunAsync(["--package-root", root, "--module-id", "fixture-host",
                "--data-path", Path.Combine(root, "data")], new StringReader("health\nshutdown\n"), output);
            var text = output.ToString();
            check(exit == 0 && text.Contains("\"type\":\"ready\"", StringComparison.Ordinal)
                && text.Contains("\"healthy\":true", StringComparison.Ordinal)
                && text.Contains("\"type\":\"stopped\"", StringComparison.Ordinal),
                "ModuleHost skeleton verifies, loads and health-checks a package in isolation");

            var wrongRid = rid.EndsWith("x64", StringComparison.Ordinal) ? rid[..^3] + "arm64" : rid[..^5] + "x64";
            var wrongEntry = Path.Combine(root, "server", wrongRid, "HostFixture.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(wrongEntry)!); File.Copy(path, wrongEntry);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest).Replace(rid, wrongRid, StringComparison.Ordinal));
            var refused = new StringWriter();
            var refusedExit = await ModuleHostProgram.RunAsync(["--package-root", root, "--module-id", "fixture-host"], new StringReader("shutdown\n"), refused);
            check(refusedExit == 1 && refused.ToString().Contains("module-host-package-invalid", StringComparison.Ordinal),
                "Portable RID normalization still refuses a complete package for a different architecture");
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest));

            await using var client = new ModuleHostProcessClient(root, "fixture-host", Path.Combine(root, "process-data"));
            var ready = await client.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(ready.Url) };
            using var unauthenticated = new HttpClient { BaseAddress = new Uri(ready.Url) };
            check((await unauthenticated.GetAsync("/hello")).StatusCode == System.Net.HttpStatusCode.Unauthorized,
                "ModuleHost rejects a request without its service token");
            http.DefaultRequestHeaders.TryAddWithoutValidation(ModuleHostProtocol.ServiceTokenHeader, client.ServiceToken);
            var body = await http.GetStringAsync("/hello");
            var storage = await http.GetStringAsync("/storage");
            var health = await client.HealthAsync();
            await client.ShutdownAsync();
            check(ready.ModuleId == "fixture-host" && health.Healthy && body == "hello from module host"
                && storage == "hello from module storage",
                "Server can launch, route to and control an isolated ModuleHost over the line protocol");

            var adminId = Guid.NewGuid();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleHosts:Enabled"] = "true",
                ["ModuleHosts:ProbeIntervalSeconds"] = "1",
                ["ModuleHosts:Entries:0:ModuleId"] = "fixture-host",
                ["ModuleHosts:Entries:0:PackageRoot"] = root,
                ["ModuleHosts:Entries:0:DataPath"] = Path.Combine(root, "supervisor-data"),
            }).Build();
            var environment = new TestEnvironment(root);
            var ownerBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = Environments.Development });
            ownerBuilder.Configuration.AddConfiguration(configuration);
            var ownerModules = ServerModules.CreateManaged(new HashSet<string>());
            ownerModules.ConfigureServices(ownerBuilder, ServerModuleContext.Create(ownerBuilder.Configuration, ownerBuilder.Environment, "memory"));
            await using var ownerServices = ownerBuilder.Build();
            using var supervisor = new ModuleHostSupervisor(environment, configuration,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHostSupervisor>.Instance,
                ownerServices.Services.GetRequiredService<IResidentModuleWorkLeaseSource>(), ownerModules);
            await supervisor.StartAsync(CancellationToken.None);
            for (var attempt = 0; attempt < 100 && supervisor.Snapshot().SingleOrDefault()?.Status != "running"; attempt++)
                await Task.Delay(50);
            using var connection = supervisor.AcquireConnection("fixture-host");
            check(connection is not null
                && supervisor.Snapshot().Single().Status == "running",
                "ModuleHost supervisor starts a configured isolated process: "
                + string.Join(",", supervisor.Snapshot().Select(host => host.Status + ":" + host.ProblemCode)));
            using var supervisedHttp = new HttpClient { BaseAddress = connection!.Endpoint };
            supervisedHttp.DefaultRequestHeaders.TryAddWithoutValidation(ModuleHostProtocol.ServiceTokenHeader, connection.ServiceToken);
            check(await supervisedHttp.GetStringAsync("/hello") == "hello from module host",
                "ModuleHost supervisor exposes the isolated HTTP endpoint");
            var administrator = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", adminId.ToString()), new Claim(ClaimTypes.Role, "controller")], "test"));
            var stranger = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
            check(supervisor.CanAccess("fixture-host", administrator)
                && !supervisor.CanAccess("fixture-host", stranger),
                "ModuleHost access defaults to authenticated controllers without an administrator allowlist");
            using var unhealthy = await supervisedHttp.PostAsync("/unhealthy", null);
            unhealthy.EnsureSuccessStatusCode();
            for (var attempt = 0; attempt < 100 && supervisor.Snapshot().Single().Status == "running"; attempt++)
                await Task.Delay(50);
            using var refusedConnection = supervisor.AcquireConnection("fixture-host");
            check(supervisor.Snapshot().Single() is { Status: "faulted", ProblemCode: "module-host-unhealthy" }
                && refusedConnection is null,
                "ModuleHost supervisor detects unhealthy modules and withdraws their proxy connection");
            connection.Dispose();
            await supervisor.StopAsync(CancellationToken.None);
            await VerifyProductionScopesAsync(root, configuration, connection.ServiceToken, connection.GenerationId, check);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task VerifyProductionScopesAsync(string root, IConfiguration configuration, string oldToken, Guid oldGeneration, Action<bool, string> check)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear(); builder.Configuration.AddConfiguration(configuration);
        using var management = new ModuleManagementStore(builder.Configuration, root);
        var modules = ServerModules.CreateManaged(new HashSet<string>());
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        modules.ConfigureServices(builder, context);
        builder.Services.AddSingleton(modules); builder.Services.AddSingleton(context); builder.Services.AddSingleton(management);
        builder.Services.AddSingleton<ModuleManagementExecutor>();
        builder.Services.AddSingleton<ModuleHostSupervisor>(); builder.Services.AddSingleton<CoreModulePrivilegeBroker>();

        // A legal durable plan is fixture data for the actual callback read endpoint; no Helper action is dispatched here.
        var identity = new ModulePrivilegeIdentity("fixture-host", "1.0.0", "fixture", new string('a', 64), new string('b', 64),
            "lifetime-fixture", [ModuleCapabilityFixtures.Service("fixture.service", "restart")], ["fixture.service"]);
        var seed = new ModulePrivilegePlanStore(root, _ => identity);
        var prepared = await seed.PrepareAsync("fixture-host", "lifetime-fixture",
            new(ModuleCapabilityCatalog.ServiceManage, "restart", "fixture.service", "Read a durable fixture plan", "lifetime-plan"), "test-user");
        await using var application = builder.Build();
        application.MapServerModuleManagementEndpoints();
        var broker = application.Services.GetRequiredService<CoreModulePrivilegeBroker>();
        var supervisor = application.Services.GetRequiredService<ModuleHostSupervisor>();
        await broker.StartAsync(default);
        await supervisor.StartAsync(default);
        using var proxyBody = new BlockedBody(); using var callbackBody = new BlockedBody();
        Task? proxy = null, callback = null, stopping = null;
        try
        {
            await WaitForAsync(() => supervisor.Snapshot().SingleOrDefault()?.Status == "running");
            string token;
            using (var connection = supervisor.AcquireConnection("fixture-host"))
            {
                token = connection!.ServiceToken;
                using var stale = supervisor.AcquireAuthenticated(oldToken);
                using var current = supervisor.AcquireAuthenticated(token);
                check(stale is null && current?.GenerationId == connection.GenerationId && connection.GenerationId != oldGeneration,
                    "a fresh real ModuleHost process authenticates only its own generation's callbacks");
            }
            var proxyEndpoint = ((IEndpointRouteBuilder)application).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(endpoint => endpoint.RoutePattern.RawText == ServerModuleManagementRoutes.ModuleHostProxy);
            var brokerApplication = (WebApplication)typeof(CoreModulePrivilegeBroker)
                .GetField("_application", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(broker)!;
            var callbackEndpoint = ((IEndpointRouteBuilder)brokerApplication).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Single(endpoint => endpoint.RoutePattern.RawText == "/plans/{id:guid}");
            var staleCallback = new DefaultHttpContext { RequestServices = brokerApplication.Services };
            staleCallback.Request.Method = "GET"; staleCallback.Request.RouteValues["id"] = prepared.PlanId.ToString();
            staleCallback.Request.Headers[ModuleHostProtocol.ServiceTokenHeader] = oldToken;
            await callbackEndpoint.RequestDelegate!(staleCallback);
            check(staleCallback.Response.StatusCode == StatusCodes.Status403Forbidden,
                "the actual Broker callback endpoint rejects a token from the prior real process");
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", Guid.NewGuid().ToString()), new Claim("sid", Guid.NewGuid().ToString()),
                new Claim("jti", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "controller")], "test"));
            var denied = new DefaultHttpContext { RequestServices = application.Services };
            denied.Request.Method = "GET"; denied.Request.RouteValues["moduleId"] = "fixture-host"; denied.Request.RouteValues["path"] = "hello";
            await proxyEndpoint.RequestDelegate!(denied);
            check(denied.Response.StatusCode == StatusCodes.Status403Forbidden && ContextCount(broker) == 0,
                "the production ModuleHost proxy rejects an unauthorized principal before issuing a Broker context");

            using var client = new CancellationTokenSource();
            var http = new DefaultHttpContext { RequestServices = application.Services, User = principal, RequestAborted = client.Token };
            http.Request.Method = "GET"; http.Request.RouteValues["moduleId"] = "fixture-host"; http.Request.RouteValues["path"] = "hello";
            http.Response.Body = proxyBody;
            proxy = proxyEndpoint.RequestDelegate!(http);
            await proxyBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(ContextCount(broker) == 1 && !proxy.IsCompleted,
                "the actual proxy to a real ModuleHost holds its generation scope and opaque Broker context while CopyTo is unfinished");

            var callbackHttp = new DefaultHttpContext { RequestServices = brokerApplication.Services, RequestAborted = client.Token };
            callbackHttp.Request.Method = "GET"; callbackHttp.Request.RouteValues["id"] = prepared.PlanId.ToString();
            callbackHttp.Request.Headers[ModuleHostProtocol.ServiceTokenHeader] = token; callbackHttp.Response.Body = callbackBody;
            callback = callbackEndpoint.RequestDelegate!(callbackHttp);
            await callbackBody.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Cancel();
            check(!proxy.IsCompleted && !callback.IsCompleted,
                "disconnect notification cannot detach actual production proxy and Broker callback response tasks");

            stopping = supervisor.StopAsync(default);
            using var revoked = supervisor.AcquireAuthenticated(token);
            var coordinator = application.Services.GetRequiredService<ModuleResidencyCoordinator>();
            check(revoked is null && !stopping.IsCompleted && coordinator.Describe("fixture-host")!.ActiveLeases == 1,
                "production stop revokes the real process token while outstanding endpoint scopes retain its process lease");
            proxyBody.Release.TrySetResult(); await ObserveCancellationAsync(proxy);
            check(ContextCount(broker) == 0 && !callback.IsCompleted && !stopping.IsCompleted
                && coordinator.Describe("fixture-host")!.ActiveLeases == 1,
                "actual proxy completion releases its Broker context but cannot release a different blocked callback's process protection");
            callbackBody.Release.TrySetResult(); await ObserveCancellationAsync(callback);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            check(ContextCount(broker) == 0 && coordinator.Describe("fixture-host")!.ActiveLeases == 0,
                "the real ModuleHost process drains only after both production endpoint executions and context cleanup finish");
        }
        finally
        {
            proxyBody.Release.TrySetResult(); callbackBody.Release.TrySetResult();
            if (proxy is not null) await ObserveCancellationAsync(proxy);
            if (callback is not null) await ObserveCancellationAsync(callback);
            if (stopping is not null) await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            else await supervisor.StopAsync(default);
            await broker.StopAsync(default);
        }
    }

    private static int ContextCount(CoreModulePrivilegeBroker broker)
    {
        var contexts = typeof(CoreModulePrivilegeBroker).GetField("_contexts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(broker)!;
        return (int)contexts.GetType().GetProperty("Count")!.GetValue(contexts)!;
    }
    private static async Task WaitForAsync(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!completed()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("ModuleHost did not become ready."); await Task.Delay(20); }
    }
    private static async Task ObserveCancellationAsync(Task operation)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { }
    }
    private sealed class BlockedBody : Stream
    {
        private readonly MemoryStream _written = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written.Length;
        public override long Position { get => _written.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task; await _written.WriteAsync(buffer, CancellationToken.None); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _written.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ModuleHostChecks";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
