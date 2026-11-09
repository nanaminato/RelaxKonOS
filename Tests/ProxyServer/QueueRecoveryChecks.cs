using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;
using RelaxKonOS.Server.Residency;

internal static class QueueRecoveryChecks
{
    internal static async Task RunAsync()
    {
        foreach (var stage in new[] { "native-pending", "domain-committed" })
        {
            var root = Directory.CreateTempSubdirectory("proxy-queue-recovery-").FullName;
            try { await VerifyAsync(root, stage); }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    internal static async Task RunWorkerAsync(string root, string stage)
    {
        await using var app = Build(root, stage);
        await app.StartAsync();
        Console.WriteLine(Address(app));
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task VerifyAsync(string root, string stage)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = budget.Token;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(QueueRecoveryChecks).Assembly.Location, "--queue-recovery-worker", root, stage })
            start.ArgumentList.Add(arg);
        Guid id;
        using (var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start first HTTP host."))
        {
            try
            {
                using var client = Client(await child.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("Missing first HTTP host."));
                id = await SubmitAsync(client, ProxyServerRoutes.Apply, "apply-original", ct);
                while (!File.Exists(Path.Combine(root, stage))) await Task.Delay(20, ct);
                Require((await ReadAsync(client, id, ct)).State == ProxyOperationState.Running, "The original HTTP operation must be live at the process barrier.");
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        await using var app = Build(root, "restarted");
        await app.StartAsync(ct);
        try
        {
            var store = app.Services.GetRequiredService<ProxyServerStore>();
            var initial = await store.ReadAsync(ct);
            Require(stage == "native-pending" ? initial.PendingPublicationOperationId == id
                : initial.QueuedPublicationCompletions.GetValueOrDefault(id) == ProxyServerPublicationOutcome.Committed,
                "The same HTTP operation ID must be bound to the protected domain evidence.");
            using var client = Client(Address(app));
            Require(await SubmitAsync(client, ProxyServerRoutes.Apply, "apply-original", ct) == id
                && (await ReadAsync(client, id, ct)).State == ProxyOperationState.Interrupted,
                "An original key replay must expose the interruption without redispatch.");
            using var denied = new HttpClient { BaseAddress = client.BaseAddress };
            using var deniedResponse = await denied.PostAsync(ProxyServerRoutes.Recovery, null, ct);
            Require(deniedResponse.StatusCode == HttpStatusCode.Unauthorized, "Recovery must require authentication.");
            Require(await SubmitAsync(client, ProxyServerRoutes.Recovery, "recover-original", ct) == id,
                "Explicit recovery must return the original operation, not enqueue a new operation.");
            var expected = stage == "native-pending" ? ProxyOperationState.Failed : ProxyOperationState.Succeeded;
            ProxyOperationDto terminal;
            do { terminal = await ReadAsync(client, id, ct); if (terminal.State != expected) await Task.Delay(20, ct); }
            while (terminal.State != expected);
            Require(terminal.ProblemCode == (expected == ProxyOperationState.Failed ? "proxy_server.config_apply_failed" : ""),
                "The original terminal receipt must describe the verified outcome.");
            Require(await SubmitAsync(client, ProxyServerRoutes.Recovery, "recover-original", ct) == id
                && await SubmitAsync(client, ProxyServerRoutes.Apply, "apply-original", ct) == id
                && await ReadAsync(client, id, ct) == terminal, "Both original keys must replay the exact terminal receipt.");
            Require(File.ReadAllLines(Path.Combine(root, "apply-count")).SequenceEqual([id.ToString("D")]), "The original apply must run once.");
            var compensation = Path.Combine(root, "compensation-count");
            Require(stage == "native-pending" ? File.ReadAllLines(compensation).SequenceEqual([id.ToString("D")]) : !File.Exists(compensation),
                "Only in-flight recovery may compensate, exactly once, under the original ID.");
            Console.WriteLine($"PASS PROXY QUEUE PROCESS RECOVERY: {stage}; original operation {id}; {terminal.State}");
        }
        finally { await app.StopAsync(); }
    }

    private static WebApplication Build(string root, string stage)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var policy in new[] { "ProxyRead", "ProxyManage", "ProxyServerExport" })
                options.AddPolicy(policy, configure => configure.RequireAuthenticatedUser());
        });
        builder.Services.AddSingleton<IProxyPlatformPaths>(new Paths(root));
        builder.Services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(root, "keys"))));
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(new FixtureHelper(root, stage));
        builder.Services.AddSingleton<IServerModeResolver, TestMode>();
        builder.Services.AddSingleton<ProxyServerStore>(); builder.Services.AddScoped<ProxyServerOperationQueue>();
        builder.Services.AddSingleton<ProxyOperationStore>(); builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddSingleton<IProxyWorkLeaseSource, TestProxyWorkLeases>();
        builder.Services.AddSingleton<IResidentModuleWorkLeaseSource, TestProxyWorkLeases>();
        QueueBoundaryChecks.RegisterProxyGeneration(builder);
        builder.Services.AddHostedService(services => services.GetRequiredService<ProxyOperationStore>());
        builder.Services.AddScoped<IProxyEngineRegistry>(_ => new FixtureEngines(root, stage));
        builder.Services.AddTransient<ProxyServerService>();
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapProxyServerEndpoints();
        return app;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private static HttpClient Client(string address)
    {
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("Authorization", "Fixture admin");
        return client;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string route, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request, ct);
        Require(response.StatusCode == HttpStatusCode.Accepted, $"Expected 202 for {route}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return (await response.Content.ReadFromJsonAsync<ProxyOperationAcceptedDto>(ct))!.OperationId;
    }

    private static async Task<ProxyOperationDto> ReadAsync(HttpClient client, Guid id, CancellationToken ct) =>
        (await client.GetFromJsonAsync<ProxyOperationDto>($"{ProxyServerRoutes.Root}/operations/{id:D}",
            RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default, ct))!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.Authorization == "Fixture admin"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "fixture-admin")], Scheme.Name)), Scheme.Name)) : AuthenticateResult.NoResult());
    }

    private sealed class FixtureEngines(string root, string stage) : IProxyEngineRegistry, IAsyncDisposable
    {
        public IProxyEngine? Find(string engineId) => null;
        public IReadOnlyList<IProxyEngine> List() => [];
        public async ValueTask DisposeAsync()
        {
            if (stage != "domain-committed") return;
            File.WriteAllText(Path.Combine(root, stage), "scope-disposing");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class FixtureHelper(string root, string stage) : IPrivilegedOperationTransport
    {
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            var path = Path.Combine(root, "native-receipt.json");
            var receipt = File.Exists(path) ? JsonSerializer.Deserialize<ProxyServerPublicationReceipt>(await File.ReadAllTextAsync(path, ct)) : null;
            var baseline = new string('0', 64);
            if (request.Operation == PrivilegedOperationKind.ProxyServerStatus)
                return Result(new ProxyServerStatus(true, true, false, receipt?.Outcome == ProxyServerPublicationOutcome.InFlight, "fixture",
                    receipt?.Outcome == ProxyServerPublicationOutcome.Committed ? receipt.CandidateHash : baseline, false, null));
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply)
            {
                var id = request.OperationId!.Value;
                var hash = Helper.Hash(request.ProxyServer!.Material!);
                await File.AppendAllTextAsync(Path.Combine(root, "apply-count"), id.ToString("D") + "\n", ct);
                var outcome = stage == "native-pending" ? ProxyServerPublicationOutcome.InFlight : ProxyServerPublicationOutcome.Committed;
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ProxyServerPublicationReceipt(id, hash, baseline, outcome)), ct);
                if (outcome == ProxyServerPublicationOutcome.InFlight)
                {
                    File.WriteAllText(Path.Combine(root, stage), "native-accepted");
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                return Result(new ProxyServerStatus(true, true, false, false, "fixture", hash, false, null));
            }
            Require(receipt is not null && receipt.OperationId == request.OperationId, "Recovery queried a different Helper operation ID.");
            if (request.Operation == PrivilegedOperationKind.ProxyServerRecoverPublication && receipt!.Outcome == ProxyServerPublicationOutcome.InFlight)
            {
                receipt = receipt with { Outcome = ProxyServerPublicationOutcome.Compensated };
                await File.AppendAllTextAsync(Path.Combine(root, "compensation-count"), receipt.OperationId.ToString("D") + "\n", ct);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt), ct);
            }
            return Result(receipt);
        }

        private static PrivilegedOperationResult Result<T>(T value) => new(true,
            OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
    }
}
