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
using RelaxKonOS.Server.Startup;

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
            Require(!(await new ApplicationContainerObservation(engine).ListAsync(cleanup.Token)).Any(container => container.Names == name),
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
        var dotNetArchive = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_DOTNET_ARCHIVE");
        var javaArchive = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_JAVA_ARCHIVE");
        Require(dotNetArchive is null || javaArchive is null, "Select one archive fixture.");
        var archivePath = dotNetArchive ?? javaArchive;
        var buildArchive = archivePath is not null || Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_BUILD_ARCHIVE") == "1";
        var concurrent = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_CONCURRENT_ARCHIVE") == "1";
        Require(!concurrent || (buildArchive && desktopRunner is null), "Concurrent archive acceptance uses HTTP submission.");
        var sourceKind = dotNetArchive is not null ? ApplicationSourceKind.DotNetPublish : javaArchive is not null ? ApplicationSourceKind.JavaJar
            : buildArchive ? ApplicationSourceKind.PythonProject : ApplicationSourceKind.Image;
        var healthPath = archivePath is not null ? "/healthz" : "/ready";
        Require(archivePath is null || File.Exists(archivePath), "The selected acceptance archive must exist.");
        var installedRoot = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_INSTALLED_ROOT");
        var coldImage = Environment.GetEnvironmentVariable("RK_TEST_DEPLOYMENTS_COLD_IMAGE");
        Require(!buildArchive || coldImage is null, "Archive build acceptance does not use cold image deployment mode.");
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
        if (buildArchive)
            deploymentEngine = new DockerCliEngineService(new() { BuildRoots = [Path.Combine(directory.FullName, new ApplicationDeploymentOptions().RootDirectory, "build")] },
                new DisabledDockerProxyResolver(), NullLogger<DockerCliEngineService>.Instance);
        ConcurrentArchiveEngine? concurrencyProbe = null;
        if (concurrent)
        {
            var synchronized = DispatchProxy.Create<IDockerEngineService, ConcurrentArchiveEngine>();
            concurrencyProbe = (ConcurrentArchiveEngine)(object)synchronized;
            concurrencyProbe.Engine = deploymentEngine;
            deploymentEngine = synchronized;
        }
        var applicationId = Guid.Empty;
        var applicationsToClean = new List<(Guid Id, string Name)>();
        var marker = "http-fixture-" + Guid.NewGuid().ToString("N")[..12];
        var cleanupRuntime = new ApplicationDeploymentRuntime(engine, new(), new HealthClientFactory(workload), NullLogger<ApplicationDeploymentRuntime>.Instance);
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
            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IDockerEngineService>(deploymentEngine);
            builder.Services.AddSingleton<IDockerProxyResolver, DisabledDockerProxyResolver>();
            builder.Services.AddSingleton<IOutboundProxyHttpClientFactory, OutboundProxyHttpClientFactory>();
            builder.Services.AddSingleton<IWebServerManager>(new WebServerManager([]));
            builder.Services.AddApplicationDeploymentState();
            builder.Services.AddBackupRecoveryState();
            var moduleContext = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            var installed = installedRoot is null ? [] : InstalledModuleCatalog.Load(Path.GetFullPath(installedRoot));
            if (installedRoot is not null)
                Require(installed.OfType<InstalledApplicationDeploymentsModule>().SingleOrDefault() is
                    { InstalledVersion: "1.0.0", TrustState: "installed" }, "Expected the restored signed deployment execution version.");
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.ApplicationDeploymentsId }, installed);
            modules.ConfigureServices(builder, moduleContext);
            builder.Services.AddSingleton<RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher, CatalogUpdateEventPublisher>();
            builder.Services.AddSingleton<IFileService>(_ => throw new NotSupportedException("No staged input is used."));
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            modules.MapEndpoints(app, moduleContext);
            await app.StartAsync(ct);
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
                http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
                var definition = new CreateApplicationRequest(marker, sourceKind, HealthCheckPath: healthPath, HostPort: port);
                using var created = await SendAsync(http, ApplicationDeploymentApiRoutes.Applications, definition, "create", ct);
                var application = (await created.Content.ReadFromJsonAsync<ApplicationDto>(RelaxKonOSJsonOptions.Default, ct))!;
                applicationId = application.Id;
                applicationsToClean.Add((application.Id, marker));
                if (desktopRunner is not null && package is not null)
                {
                    var desktopSource = imageReference;
                    if (archivePath is not null)
                        desktopSource = "archive:" + Path.GetFullPath(archivePath);
                    else if (buildArchive)
                    {
                        var path = Path.Combine(directory.FullName, "python-fixture.zip");
                        using var archive = CreatePythonFixture(marker);
                        await using var file = File.Create(path);
                        await archive.CopyToAsync(file, ct);
                        desktopSource = "archive:" + path;
                    }
                    await EventAlertChecks.RunDesktopPackageAsync(desktopRunner, package, "RK_TEST_DEPLOYMENTS_PORT", new Uri(address).Port,
                        new("RK_TEST_DEPLOYMENTS_SUBMIT", desktopSource));
                    var deployed = await http.GetFromJsonAsync<ApplicationDeploymentSnapshotDto>(ApplicationDeploymentApiRoutes.Application(application.Id), RelaxKonOSJsonOptions.Default, ct);
                    Require(deployed is not null && deployed.Application.CurrentRevisionId is { } activeId
                        && deployed.Revisions.Single().Id == activeId && deployed.Operations.Single() is
                        { State: DeploymentOperationState.Succeeded, Kind: DeploymentOperationKind.Deploy }, "The window did not activate exactly one revision through a completed deployment.");
                    await VerifyWorkloadResponseAsync();
                    await VerifyPulledIdentityAsync(deployed!.Revisions.Single().ImageId);
                    if (buildArchive) VerifyBuiltRevision(deployed.Revisions.Single(), deployed.Operations.Single().OperationId);
                    Console.WriteLine($"Installed Application Deployments window activated a real Docker workload (registry pull: {pullFromRegistry}).");
                    return;
                }
                var source = buildArchive ? await UploadArchiveAsync(http, marker, archivePath, ct) : new DeploymentSourceInputDto(ImageReference: imageReference,
                    Arguments: ["sh", "-ec", $"mkdir -p /tmp/site; echo {marker} > /tmp/site/ready; exec httpd -f -p 8080 -h /tmp/site"]);
                if (concurrent)
                {
                    var secondMarker = marker + "-other";
                    var secondPortProbe = new TcpListener(IPAddress.Loopback, 0);
                    secondPortProbe.Start();
                    var secondPort = ((IPEndPoint)secondPortProbe.LocalEndpoint).Port;
                    secondPortProbe.Stop();
                    Require(secondPort != port, "Concurrent fixture endpoints need distinct ports.");
                    using var secondCreated = await SendAsync(http, ApplicationDeploymentApiRoutes.Applications,
                        definition with { Name = secondMarker, HostPort = secondPort }, "create-second", ct);
                    var second = (await secondCreated.Content.ReadFromJsonAsync<ApplicationDto>(RelaxKonOSJsonOptions.Default, ct))!;
                    applicationsToClean.Add((second.Id, secondMarker));
                    await Task.WhenAll(VerifyConcurrentDeploymentAsync(application, marker, port), VerifyConcurrentDeploymentAsync(second, secondMarker, secondPort));
                    Console.WriteLine("Two concurrent deployments used isolated Docker contexts, and retention preserved the active build.");
                    var contextRoot = Path.Combine(directory.FullName, new ApplicationDeploymentOptions().RootDirectory, "build");
                    var secondContexts = Directory.GetDirectories(contextRoot, $"{second.Id:N}-*");
                    Require(secondContexts.Length == 1 && Directory.GetDirectories(contextRoot, $"{application.Id:N}-*").Length == 1,
                        "Both applications must have a retained context before deleting one.");
                    using var delete = new HttpRequestMessage(HttpMethod.Delete, ApplicationDeploymentApiRoutes.Application(application.Id))
                    {
                        Content = JsonContent.Create(new DeleteApplicationRequest(Confirmed: true), options: RelaxKonOSJsonOptions.Default),
                    };
                    delete.Headers.Add("Idempotency-Key", "delete-first-archive");
                    using var deleted = await http.SendAsync(delete, ct);
                    Require(deleted.StatusCode == HttpStatusCode.Accepted, "Delete did not return an accepted operation.");
                    var deletion = (await deleted.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default, ct))!;
                    deletion = await WaitForReceiptAsync(http, deletion, ct);
                    Require(deletion is { State: DeploymentOperationState.Succeeded, Kind: DeploymentOperationKind.Delete }
                        && Directory.GetDirectories(contextRoot, $"{application.Id:N}-*").Length == 0
                        && File.Exists(Path.Combine(secondContexts[0], "Dockerfile"))
                        && (await cleanupRuntime.FindApplicationContainersAsync(application.Id, ct)).Count == 0,
                        "Deleting the first workload must remove its own container and context while preserving the second context.");
                    await VerifyWorkloadResponseAsync(secondPort);
                    Console.WriteLine("The signed execution package removed the deleted application's build context and preserved the other live workload.");
                    return;

                    async Task VerifyConcurrentDeploymentAsync(ApplicationDto target, string name, int hostPort)
                    {
                        using var response = await SendAsync(http, ApplicationDeploymentApiRoutes.Deploy(target.Id),
                            new DeployApplicationRequest(source, target.UpdatedAt, Confirmed: true), "deploy-" + name, ct);
                        var submitted = (await response.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default, ct))!;
                        var receipt = await WaitForReceiptAsync(http, submitted, ct);
                        concurrencyProbe!.OnReceipt(target.Id, receipt.OperationId);
                        Require(receipt.State == DeploymentOperationState.Succeeded, $"Concurrent deployment {name} failed: {receipt.ProblemCode}");
                        var snapshot = (await http.GetFromJsonAsync<ApplicationDeploymentSnapshotDto>(
                            ApplicationDeploymentApiRoutes.Application(target.Id), RelaxKonOSJsonOptions.Default, ct))!;
                        VerifyBuiltRevision(snapshot.Revisions.Single(), receipt.OperationId, target.Id, name);
                        await VerifyWorkloadResponseAsync(hostPort);
                    }
                }
                var request = new DeployApplicationRequest(source, application.UpdatedAt, Confirmed: true);
                var route = ApplicationDeploymentApiRoutes.Deploy(application.Id);
                using var submitted = await SendAsync(http, route, request, "deploy", ct);
                Require(submitted.StatusCode == HttpStatusCode.Accepted, "Deploy did not return an accepted operation.");
                var receipt = (await submitted.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default, ct))!;
                receipt = await WaitForReceiptAsync(http, receipt, ct);
                Require(receipt.State == DeploymentOperationState.Succeeded && receipt.ApplicationId == application.Id && receipt.CompletedAt.HasValue,
                    $"Deploy receipt is {receipt.State} at {receipt.Stage}: {receipt.ProblemCode}; recovery {receipt.RecoveryProblemCode}");
                using var replay = await SendAsync(http, route, request, "deploy", ct);
                var replayed = await replay.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default, ct);
                Require(replayed == receipt, "Replaying the deployment did not return the terminal receipt.");
                var snapshot = await http.GetFromJsonAsync<ApplicationDeploymentSnapshotDto>(ApplicationDeploymentApiRoutes.Application(application.Id), RelaxKonOSJsonOptions.Default, ct);
                Require(snapshot is not null && snapshot.Application.CurrentRevisionId is { } activeRevisionId
                    && snapshot.Revisions.Count == 1 && snapshot.Revisions[0].Id == activeRevisionId
                    && snapshot.Operations.Contains(receipt), "The application did not expose the active revision and completed deployment receipt.");
                await VerifyWorkloadResponseAsync();
                await VerifyPulledIdentityAsync(snapshot!.Revisions.Single().ImageId);
                if (buildArchive)
                {
                    VerifyBuiltRevision(snapshot.Revisions.Single(), receipt.OperationId);
                    Console.WriteLine($"The staged {sourceKind} archive built and served its HTTP fixture (installed execution: {installedRoot is not null}).");
                }
                Console.WriteLine($"Live deployment HTTP submission, terminal receipt, replay and activated workload passed (registry pull: {pullFromRegistry}).");
            }
            finally { await app.StopAsync(); }
        }

        finally
        {
            try
            {
                foreach (var (cleanupId, cleanupName) in applicationsToClean)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    foreach (var container in await cleanupRuntime.FindApplicationContainersAsync(cleanupId, cleanup.Token))
                        Require((await cleanupRuntime.RemoveAsync(container.Id, cleanup.Token)).Success, $"Failed to remove fixture container {container.Id}.");
                    Require((await cleanupRuntime.FindApplicationContainersAsync(cleanupId, cleanup.Token)).Count == 0, "Deployment fixture containers remain.");
                    if (buildArchive)
                    {
                        var repository = $"relaxkonos-ad/{cleanupName}";
                        var images = await engine.ListImagesAsync(cleanup.Token);
                        foreach (var image in images.Where(image => image.Repository == repository))
                        {
                            if (concurrent)
                                await RemoveFixtureTagAsync($"{image.Repository}:{image.Tag}", cleanup.Token);
                            else
                            {
                                Require(images.Count(candidate => candidate.Id == image.Id) == 1, "The fixture image has another tag.");
                                Require((await engine.DeleteImageAsync(image.Id, new(image.Id, Confirmed: true), cleanup.Token)).Success,
                                    $"Failed to remove built fixture image {image.Id}.");
                            }
                        }
                        Require(!(await engine.ListImagesAsync(cleanup.Token)).Any(image => image.Repository == repository), "Build fixture image remains.");
                    }
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

        async Task VerifyWorkloadResponseAsync(int? hostPort = null)
        {
            var endpoint = $"http://127.0.0.1:{hostPort ?? port}";
            Require((await workload.GetStringAsync(endpoint + healthPath, ct)).Trim() == (archivePath is null ? marker : "ok"),
                "The activated workload did not serve the expected readiness response.");
            if (dotNetArchive is not null)
                Require((await workload.GetStringAsync(endpoint, ct)).Contains("relaxkonos-ad-dotnet-web", StringComparison.Ordinal),
                    "The activated workload did not serve the .NET sample.");
            if (javaArchive is not null)
                Require((await workload.GetStringAsync(endpoint, ct)).Contains("relaxkonos-ad-java-http", StringComparison.Ordinal),
                    "The activated workload did not serve the Java sample.");
        }

        void VerifyBuiltRevision(ApplicationRevisionDto revision, Guid operationId, Guid? targetId = null, string? targetName = null)
        {
            Require(revision.ImageReference.StartsWith($"relaxkonos-ad/{targetName ?? marker}:", StringComparison.Ordinal)
                && revision.EntryPoint == (sourceKind switch
                {
                    ApplicationSourceKind.DotNetPublish => "dotnet DotNetWebDemo.dll",
                    ApplicationSourceKind.JavaJar => "java -jar /app/app.jar",
                    _ => "python -m fixture",
                }),
                "The active revision did not bind the image and entry point built from the staged archive.");
            var context = Path.Combine(directory.FullName, new ApplicationDeploymentOptions().RootDirectory, "build",
                $"{targetId ?? applicationId:N}-{operationId:N}");
            Require(File.Exists(Path.Combine(context, "Dockerfile")), "The build context was not retained under its application and operation.");
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

    private static async Task<DeploymentOperationDto> WaitForReceiptAsync(HttpClient http, DeploymentOperationDto receipt, CancellationToken ct)
    {
        var route = ApplicationDeploymentApiRoutes.Operation(receipt.OperationId);
        while (receipt.State is DeploymentOperationState.Queued or DeploymentOperationState.Running)
        {
            await Task.Delay(250, ct);
            receipt = (await http.GetFromJsonAsync<DeploymentOperationDto>(route, RelaxKonOSJsonOptions.Default, ct))!;
        }
        return receipt;
    }

    private static async Task RemoveFixtureTagAsync(string imageReference, CancellationToken ct)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            ArgumentList = { "image", "rm", imageReference },
        }) ?? throw new InvalidOperationException("Could not start Docker fixture cleanup.");
        await process.WaitForExitAsync(ct);
        Require(process.ExitCode == 0, $"Failed to remove fixture image tag {imageReference}.");
    }

    private static async Task<DeploymentSourceInputDto> UploadArchiveAsync(HttpClient http, string marker, string? archivePath, CancellationToken ct)
    {
        using Stream archive = archivePath is null ? CreatePythonFixture(marker) : File.OpenRead(archivePath);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(archive), "file", "fixture.zip");
        using var upload = await http.PostAsync(ApplicationDeploymentApiRoutes.Uploads, form, ct);
        upload.EnsureSuccessStatusCode();
        var staged = await upload.Content.ReadFromJsonAsync<DeploymentStagedFileDto>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("The upload returned no staged archive.");
        return new(ArchiveReferenceId: staged.ReferenceId, ProgramEntry: archivePath is null ? "fixture" : null);
    }

    private static MemoryStream CreatePythonFixture(string marker)
    {
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var requirements = zip.CreateEntry("publish/requirements.txt").Open()) requirements.Write("# Standard library only\n"u8);
            using var program = new StreamWriter(zip.CreateEntry("publish/fixture.py").Open());
            program.Write($$"""
                from http.server import BaseHTTPRequestHandler, HTTPServer

                class Handler(BaseHTTPRequestHandler):
                    def do_GET(self):
                        self.send_response(200 if self.path == '/ready' else 404)
                        self.end_headers()
                        self.wfile.write(b'{{marker}}')

                HTTPServer(('0.0.0.0', 8080), Handler).serve_forever()
                """);
        }
        archive.Position = 0;
        return archive;
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

    public class ConcurrentArchiveEngine : DispatchProxy
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? firstContext;
        public IDockerEngineService Engine { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IDockerEngineService.BuildImageAsync)
                ? BuildAsync((DockerBuildRequest)args![0]!, (bool)args[1]!, (CancellationToken)args[2]!, (Action<string>?)args[3])
                : targetMethod.Invoke(Engine, args);

        private async Task<DockerOperationResult> BuildAsync(DockerBuildRequest request, bool includeBuildOutput,
            CancellationToken ct, Action<string>? onOutput)
        {
            bool isFirst;
            lock (gate)
            {
                isFirst = firstContext is null;
                if (isFirst) firstContext = request.ContextDirectory;
                else
                {
                    var distinct = !string.Equals(firstContext, request.ContextDirectory, StringComparison.Ordinal);
                    if (distinct)
                    {
                        var root = Path.GetDirectoryName(firstContext!)!;
                        Directory.SetLastWriteTimeUtc(firstContext!, DateTime.UtcNow.AddDays(-1));
                        for (var index = 0; index < 21; index++)
                            Directory.CreateDirectory(Path.Combine(root, $"retention-fixture-{index}"));
                        Directory.SetLastWriteTimeUtc(request.ContextDirectory, DateTime.UtcNow.AddHours(1));
                    }
                    ready.TrySetResult(distinct);
                }
            }
            Require(await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct), "Concurrent Docker builds received the same build context.");
            if (isFirst)
            {
                await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
                Require(File.Exists(Path.Combine(request.ContextDirectory, "Dockerfile")), "Retention removed the active Docker context.");
                Require(Directory.GetDirectories(Path.GetDirectoryName(request.ContextDirectory)!, "retention-fixture-*").Length < 21,
                    "Retention did not remove any completed fixture contexts.");
                Directory.SetLastWriteTimeUtc(request.ContextDirectory, DateTime.UtcNow.AddHours(1));
            }
            return await Engine.BuildImageAsync(request, includeBuildOutput, ct, onOutput);
        }

        public void OnReceipt(Guid applicationId, Guid operationId)
        {
            lock (gate)
                if (Path.GetFileName(firstContext) != $"{applicationId:N}-{operationId:N}")
                    secondCompleted.TrySetResult();
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
