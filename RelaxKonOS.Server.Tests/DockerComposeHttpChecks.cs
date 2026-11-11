using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ImageMirrors;
using RelaxKonOS.Server.Residency;

internal static class DockerComposeHttpChecks
{
    internal static async Task RunAsync()
    {
        var leases = new TestResidentBackgroundLeases();
        var fixture = new ReadFixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton<IResidentModuleWorkLeaseSource>(leases);
        var engine = DispatchProxy.Create<IDockerEngineService, CatalogUpdateEngine>();
        var engineFixture = (CatalogUpdateEngine)(object)engine;
        engineFixture.Container = new("fixture-id", "relaxkonos-ca-fixture-db-1", "", "", "running", "", "", "", "",
            [], [], [], ["POSTGRES_PASSWORD=managed-fixture-secret"], new Dictionary<string, string>());
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton<IDockerEngineControlService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IDockerImageMirrorResolver>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<DockerStackOperationCoordinator>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IDockerComposeService>(_ => new ComposeScope(fixture));
        await using var app = builder.Build();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        app.Use(async (context, next) =>
        {
            var current = fixture;
            context.Response.OnStarting(async () =>
            {
                current.Responding.TrySetResult();
                await current.ReleaseResponse.Task.WaitAsync(ct);
            });
            try { await next(context); }
            finally { current.Exited.TrySetResult(); }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDockerEndpoints();
        await app.StartAsync(ct);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/api/v1.0/docker/") };
        fixture.ReleaseResponse.TrySetResult();
        using (var anonymous = await http.GetAsync("stacks", ct))
            TestAssert.Assert(anonymous.StatusCode == HttpStatusCode.Unauthorized && leases.Acquired == 0 && !fixture.Entered.Task.IsCompleted,
                "An unauthorized Compose request must not admit CLI work.");
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        using (var response = await http.GetAsync("containers/alias", ct))
        {
            response.EnsureSuccessStatusCode();
            var details = await response.Content.ReadFromJsonAsync<DockerContainerDetailsDto>(ct);
            TestAssert.Assert(details is { Environment.Count: 0 }, "Managed container details must suppress environment values through aliases.");
        }
        using (var response = await http.GetAsync("containers/alias/logs", ct))
            TestAssert.Assert(response.StatusCode == HttpStatusCode.Conflict
                && !engineFixture.Calls.Contains(nameof(IDockerEngineService.GetContainerLogsAsync)),
                "Managed logs can contain injected values and must be blocked before reading them.");

        foreach (var route in new[] { "stacks", "stacks/fixture/definition", "stacks/fixture/services", "stacks/preview" })
        {
            fixture = new ReadFixture();
            var request = route.EndsWith("/preview", StringComparison.Ordinal)
                ? http.PostAsJsonAsync(route, new DockerStackDefinitionDto("fixture", "services: {}"), ct) : http.GetAsync(route, ct);
            try
            {
                await fixture.Entered.Task.WaitAsync(ct);
                TestAssert.Assert(leases.Active == 1, "The real Compose task must hold one lease.");
                fixture.ReleaseRead.TrySetResult();
                await fixture.Responding.Task.WaitAsync(ct);
                TestAssert.Assert(leases.Active == 1 && !fixture.Cleaning.Task.IsCompleted, "The Compose HTTP response must hold its lease.");
                fixture.ReleaseResponse.TrySetResult();
                await fixture.Cleaning.Task.WaitAsync(ct);
                TestAssert.Assert(leases.Active == 1, "The Compose request scope must clean up before returning the lease.");
            }
            finally { fixture.ReleaseRead.TrySetResult(); fixture.ReleaseResponse.TrySetResult(); fixture.ReleaseCleanup.TrySetResult(); }
            using var response = await request;
            response.EnsureSuccessStatusCode();
            await fixture.Exited.Task.WaitAsync(ct);
            TestAssert.Assert(leases.Active == 0, "The completed Compose request must return its lease.");
        }

        fixture = new ReadFixture { FailCleanup = true };
        fixture.ReleaseRead.TrySetResult();
        fixture.ReleaseCleanup.TrySetResult();
        fixture.ReleaseResponse.TrySetResult();
        using (var response = await http.GetAsync("stacks/fixture/services", ct))
            TestAssert.Assert(response.StatusCode == HttpStatusCode.InternalServerError, "Unconfirmed native cleanup must surface a failed HTTP response.");
        await fixture.Exited.Task.WaitAsync(ct);
        TestAssert.Assert(leases.Active == 1 && leases.RecoveryRequired == 1, "Unconfirmed native cleanup must retain its original work responsibility.");
        await app.StopAsync(ct);
        Console.WriteLine("PASS DOCKER COMPOSE HTTP DRAIN: authorization, task, response, scope and unconfirmed native cleanup");
    }

    private sealed class ReadFixture
    {
        internal bool FailCleanup { get; init; }
        internal readonly TaskCompletionSource Entered = Signal(), Responding = Signal(), Cleaning = Signal(), Exited = Signal();
        internal readonly TaskCompletionSource ReleaseRead = Signal(), ReleaseResponse = Signal(), ReleaseCleanup = Signal();
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ComposeScope(ReadFixture fixture) : IDockerComposeService, IAsyncDisposable
    {
        public Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken ct) => ReadAsync<IReadOnlyList<DockerStackDto>>([]);
        public Task<DockerStackPreviewDto> PreviewAsync(DockerStackDefinitionDto definition, CancellationToken ct) => ReadAsync(new DockerStackPreviewDto(definition.Name, "fixture", [], [], []));
        public Task<DockerStackPreviewDto> PreviewManagedAsync(ManagedComposePlan plan, CancellationToken ct) => throw new NotSupportedException();
        public Task<DockerStackMutationResult> DeployManagedAsync(ManagedComposePlan plan,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> environment, CancellationToken ct) => throw new NotSupportedException();
        public Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken ct) => ReadAsync<DockerStackDefinitionDto?>(new(name, "services: {}"));
        public Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken ct) => ReadAsync<IReadOnlyList<DockerStackServiceDto>>([]);
        public Task<DockerStackMutationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken ct) => throw new NotSupportedException();
        public Task<DockerStackMutationResult> ApplyActionAsync(string name, DockerStackOperationKind kind, bool confirmed, CancellationToken ct) => throw new NotSupportedException();

        private async Task<T> ReadAsync<T>(T result)
        {
            fixture.Entered.TrySetResult();
            await fixture.ReleaseRead.Task;
            if (fixture.FailCleanup) throw new DockerComposeCleanupException();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            fixture.Cleaning.TrySetResult();
            await fixture.ReleaseCleanup.Task;
        }
    }
}
