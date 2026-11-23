using System.Text.RegularExpressions;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Files;

internal static class DeploymentRuntimeLiveChecks
{
    public static async Task RunAsync(string imageId, string? desktopRunner = null, string? package = null)
    {
        if (!Regex.IsMatch(imageId, @"\Asha256:[0-9a-f]{64}\z"))
            throw new ArgumentException("Expected the immutable ID of a locally cached BusyBox image.", nameof(imageId));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var engine = new DockerCliEngineService(new(), new DisabledDockerProxyResolver(), NullLogger<DockerCliEngineService>.Instance);
        var runtime = new ApplicationDeploymentRuntime(engine, new(), new HealthClientFactory(http), NullLogger<ApplicationDeploymentRuntime>.Instance);
        var platform = await runtime.PreflightEngineAsync(ct);
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var applicationId = Guid.NewGuid();
        var name = ApplicationDeploymentValidation.ContainerName(applicationId);
        var marker = applicationId.ToString("N");
        var now = DateTimeOffset.UtcNow;
        var application = new ApplicationRecord(applicationId, name, "deployment-runtime-live", ApplicationSourceKind.Image,
            ApplicationWorkloadKind.Web, ApplicationDesiredState.Stopped, ApplicationReadinessLevel.Http,
            "/ready", 8080, port, "127.0.0.1", new(), [], [], null, null, null, null, null, null, now, now, null);
        var revision = new RevisionRecord(Guid.NewGuid(), applicationId, 1, application.SourceKind, "1.0.0",
            ApplicationDeploymentValidation.Reference(imageId), imageId, imageId, platform, null,
            application.WorkloadKind, application.ReadinessLevel, application.HealthCheckPath, "sh",
            ["sh", "-ec", $"mkdir -p /tmp/site; echo {marker} > /tmp/site/ready; exec httpd -f -p 8080 -h /tmp/site"],
            application.ContainerPort, application.HostPort, application.BindAddress, application.Limits, [], [], null,
            application.OwnerReference, DateTimeOffset.UtcNow);

        try
        {
            var created = await runtime.CreateContainerAsync(application, revision, name, [], Guid.NewGuid(), "candidate", null, ct);
            Require(created.Success, $"Create failed: {created.ProblemCode}");
            var owned = await runtime.FindOwnedContainerAsync(applicationId, name, ct)
                ?? throw new InvalidOperationException("The created container did not have the expected ownership labels.");
            var started = await runtime.StartAsync(owned.Id, ct);
            Require(started.Success, $"Start failed: {started.ProblemCode}");
            while (!await runtime.IsReadyAsync(application, owned.Id, ct, requireSuccessStatus: true))
                await Task.Delay(250, ct);
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/ready", ct);
            Require(body.Trim() == marker, "The published health endpoint returned the wrong fixture marker.");
            var stopped = await runtime.StopAsync(owned.Id, force: true, ct);
            Require(stopped.Success && !await runtime.IsReadyAsync(application, owned.Id, ct), "The stopped container remained ready.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var owned = await runtime.FindOwnedContainerAsync(applicationId, name, cleanup.Token);
            if (owned is not null)
            {
                var removed = await runtime.RemoveAsync(owned.Id, cleanup.Token);
                Require(removed.Success, $"Cleanup failed for {name}: {removed.ProblemCode}");
            }
            Require(!(await runtime.ListContainersAsync(cleanup.Token)).Any(container => container.Names == name),
                $"The fixture container {name} remains in Docker.");
        }
        Console.WriteLine("Live Application Deployments runtime create, HTTP readiness, stop and cleanup passed.");
        using var deploymentDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await VerifyHttpDeploymentAsync(engine, imageId, http, port, desktopRunner, package, deploymentDeadline.Token);
    }

    private static async Task VerifyHttpDeploymentAsync(IDockerEngineService engine, string imageId, HttpClient workload,
        int port, string? desktopRunner, string? package, CancellationToken ct)
    {
        const string cachedReference = "busybox:1.37";
        Require((await engine.InspectImageAsync(cachedReference, ct))?.Id == imageId,
            $"The local {cachedReference} tag must identify the supplied image.");
        var pullFromRegistry = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_PULL_IMAGE") == "1";
        var coldImage = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_COLD_IMAGE");
        var imageReference = coldImage ?? cachedReference;
        HashSet<string>? priorImageIds = null;
        if (coldImage is not null)
        {
            Require(pullFromRegistry && Regex.IsMatch(coldImage, @"\Abusybox:[0-9]+\.[0-9]+\.[0-9]+\z"),
                "Cold-image acceptance requires a pinned BusyBox patch version and registry pull mode.");
            Require(await engine.InspectImageAsync(coldImage, ct) is null, $"Image {coldImage} is already available locally.");
            priorImageIds = (await engine.ListImagesAsync(ct)).Select(image => image.Id).ToHashSet(StringComparer.Ordinal);
        }
        var deploymentEngine = engine;
        if (!pullFromRegistry)
        {
            deploymentEngine = DispatchProxy.Create<IDockerEngineService, CachedImageEngine>();
            ((CachedImageEngine)(object)deploymentEngine).Engine = engine;
        }
        var directory = Directory.CreateTempSubdirectory("rk-deployment-http-");
        var applicationId = Guid.Empty;
        var options = new ApplicationDeploymentOptions();
        var runtime = new ApplicationDeploymentRuntime(deploymentEngine, options, new HealthClientFactory(workload), NullLogger<ApplicationDeploymentRuntime>.Instance);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory.FullName });
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
            builder.Services.AddAuthorization(policies =>
            {
                policies.AddPolicy(ApplicationDeploymentEndpoints.ReadPolicy, policy => policy.RequireRole("controller", "reader"));
                policies.AddPolicy(ApplicationDeploymentEndpoints.ManagePolicy, policy => policy.RequireRole("controller"));
            });
            builder.Services.AddDataProtection().PersistKeysToFileSystem(directory.CreateSubdirectory("keys"));
            builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(runtime);
            builder.Services.AddSingleton<ApplicationDeploymentCatalogStore>();
            builder.Services.AddSingleton<ApplicationDeploymentSecretStore>();
            builder.Services.AddSingleton<ApplicationDeploymentStagingStore>();
            builder.Services.AddSingleton<ApplicationDeploymentOperationStore>();
            builder.Services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>();
            builder.Services.AddSingleton<ApplicationManagedComposeDispatch>();
            builder.Services.AddSingleton<ApplicationDeploymentLiveLogs>();
            builder.Services.AddSingleton(new ApplicationDeploymentImageTagCatalog(new FixtureHttpClientFactory([]),
                NullLogger<ApplicationDeploymentImageTagCatalog>.Instance));
            builder.Services.AddSingleton<ApplicationDeploymentManager>();
            builder.Services.AddSingleton<ApplicationDeploymentService>(services => new(
                services.GetRequiredService<ApplicationDeploymentCatalogStore>(), services.GetRequiredService<ApplicationDeploymentSecretStore>(),
                services.GetRequiredService<ApplicationDeploymentStagingStore>(), runtime, null!, options,
                services.GetRequiredService<IHostEnvironment>(), services.GetRequiredService<ApplicationDeploymentLiveLogs>(), null!,
                services.GetRequiredService<ILogger<ApplicationDeploymentService>>()));
            builder.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher, CatalogUpdateEventPublisher>();
            builder.Services.AddSingleton<ApplicationDeploymentCoordinator>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ApplicationDeploymentCoordinator>());
            builder.Services.AddSingleton<IFileService>(_ => throw new NotSupportedException("No staged input is used."));
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapApplicationDeploymentEndpoints();
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
            http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
            var marker = "http-fixture-" + Guid.NewGuid().ToString("N")[..12];
            var definition = new CreateApplicationRequest(marker, ApplicationSourceKind.Image, HealthCheckPath: "/ready", HostPort: port);
            using var created = await SendAsync(http, ApplicationDeploymentApiRoutes.Applications, definition, "create", ct);
            var application = (await created.Content.ReadFromJsonAsync<ApplicationDto>(RelaxKonOSJsonOptions.Default, ct))!;
            applicationId = application.Id;
            if (desktopRunner is not null && package is not null)
            {
                await EventAlertChecks.RunDesktopPackageAsync(desktopRunner, package, "RK_TEST_DEPLOYMENTS_PORT", new Uri(address).Port,
                    new("RK_TEST_DEPLOYMENTS_SUBMIT", imageReference));
                var deployed = await http.GetFromJsonAsync<ApplicationDeploymentSnapshotDto>(ApplicationDeploymentApiRoutes.Application(application.Id), RelaxKonOSJsonOptions.Default, ct);
                Require(deployed is not null && deployed.Application.CurrentRevisionId is { } activeId
                    && deployed.Revisions.Single().Id == activeId && deployed.Operations.Single() is
                    { State: DeploymentOperationState.Succeeded, Kind: DeploymentOperationKind.Deploy }, "The window did not activate exactly one revision through a completed deployment.");
                Require((await workload.GetStringAsync($"http://127.0.0.1:{port}/ready", ct)).Trim() == marker,
                    "The window-deployed workload did not serve the expected marker.");
                await VerifyPulledIdentityAsync(deployed!.Revisions.Single().ImageId);
                Console.WriteLine($"Installed Application Deployments window activated a real Docker workload (registry pull: {pullFromRegistry}).");
                return;
            }
            var request = new DeployApplicationRequest(new(ImageReference: imageReference,
                Arguments: ["sh", "-ec", $"mkdir -p /tmp/site; echo {marker} > /tmp/site/ready; exec httpd -f -p 8080 -h /tmp/site"]),
                application.UpdatedAt, Confirmed: true);
            var route = ApplicationDeploymentApiRoutes.Deploy(application.Id);
            using var submitted = await SendAsync(http, route, request, "deploy", ct);
            Require(submitted.StatusCode == HttpStatusCode.Accepted, "Deploy did not return an accepted operation.");
            var receipt = (await submitted.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default, ct))!;
            var receiptRoute = ApplicationDeploymentApiRoutes.Operation(receipt.OperationId);
            while (receipt.State is DeploymentOperationState.Queued or DeploymentOperationState.Running)
            {
                await Task.Delay(250, ct);
                receipt = (await http.GetFromJsonAsync<DeploymentOperationDto>(receiptRoute, RelaxKonOSJsonOptions.Default, ct))!;
            }
            Require(receipt.State == DeploymentOperationState.Succeeded && receipt.ApplicationId == application.Id && receipt.CompletedAt.HasValue,
                $"Deploy receipt is {receipt.State} at {receipt.Stage}: {receipt.ProblemCode}; recovery {receipt.RecoveryProblemCode}");
            using var replay = await SendAsync(http, route, request, "deploy", ct);
            var replayed = await replay.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default, ct);
            Require(replayed == receipt, "Replaying the deployment did not return the terminal receipt.");
            var snapshot = await http.GetFromJsonAsync<ApplicationDeploymentSnapshotDto>(ApplicationDeploymentApiRoutes.Application(application.Id), RelaxKonOSJsonOptions.Default, ct);
            Require(snapshot is not null && snapshot.Application.CurrentRevisionId is { } activeRevisionId
                && snapshot.Revisions.Count == 1 && snapshot.Revisions[0].Id == activeRevisionId
                && snapshot.Operations.Contains(receipt), "The application did not expose the active revision and completed deployment receipt.");
            Require((await workload.GetStringAsync($"http://127.0.0.1:{port}/ready", ct)).Trim() == marker, "The activated workload did not serve the expected marker.");
            await VerifyPulledIdentityAsync(snapshot!.Revisions.Single().ImageId);
            Console.WriteLine($"Live deployment HTTP submission, terminal receipt, replay and activated workload passed (registry pull: {pullFromRegistry}).");
        }

        finally
        {
            try
            {
                if (applicationId != Guid.Empty)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    foreach (var container in await runtime.FindApplicationContainersAsync(applicationId, cleanup.Token))
                        Require((await runtime.RemoveAsync(container.Id, cleanup.Token)).Success, $"Failed to remove fixture container {container.Id}.");
                    Require((await runtime.FindApplicationContainersAsync(applicationId, cleanup.Token)).Count == 0, "Deployment fixture containers remain.");
                }
            }
            finally
            {
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Require(directory.FullName.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                    && directory.Name.StartsWith("rk-deployment-http-", StringComparison.Ordinal), "Unexpected HTTP fixture directory.");
                directory.Delete(recursive: true);
            }
        }

        async Task VerifyPulledIdentityAsync(string? revisionImageId)
        {
            if (priorImageIds is null) return;
            var pulled = await engine.InspectImageAsync(imageReference, ct);
            Require(pulled is not null && pulled.Id == revisionImageId && !priorImageIds.Contains(pulled.Id),
                "The activated revision did not bind an image newly present in the local Docker image inventory.");
            Console.WriteLine($"Registry pulled a new local image for {imageReference} and the revision bound its ID.");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string route, T body, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: RelaxKonOSJsonOptions.Default) };
        request.Headers.Add("Idempotency-Key", key);
        var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        using (response)
            throw new InvalidOperationException($"POST {route} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    public class CachedImageEngine : DispatchProxy
    {
        public IDockerEngineService Engine { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDockerEngineService.PullImageAsync))
                return Task.FromResult(args?[0] is DockerImageOperationRequest { ImageReference: "busybox:1.37" }
                    ? new DockerOperationResult(true, "") : new DockerOperationResult(false, "docker.validation_failed"));
            return targetMethod!.Invoke(Engine, args);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HealthClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
