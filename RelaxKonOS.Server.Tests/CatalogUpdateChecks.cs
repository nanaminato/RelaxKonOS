using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Files;

internal static class CatalogUpdateChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-catalog-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyAsync(string root)
    {
        var environment = new TestHostEnvironment(root);
        var options = new ApplicationDeploymentOptions();
        var catalog = new ApplicationDeploymentCatalogStore(environment, options);
        var secrets = new ApplicationDeploymentSecretStore(environment, options, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))), catalog);
        var operations = new ApplicationDeploymentOperationStore(environment, options);
        var engine = DispatchProxy.Create<IDockerEngineService, CatalogUpdateEngine>();
        var controlled = (CatalogUpdateEngine)(object)engine;
        var clients = new FixtureHttpClientFactory([]);
        var runtime = new ApplicationDeploymentRuntime(engine, options, clients, NullLogger<ApplicationDeploymentRuntime>.Instance);
        var manager = new ApplicationDeploymentManager(catalog, secrets, operations, runtime, options,
            new ApplicationDeploymentImageTagCatalog(clients, NullLogger<ApplicationDeploymentImageTagCatalog>.Instance));
        var target = ApplicationCatalog.Require("personal-site", "1.0.0");
        var bound = target.Bind(new InstallCatalogApplicationRequest(target.Id, target.Version, "new-site", 8088, [], true));
        Check(bound.Definition.HostPort == 8088 && bound.Definition.BindAddress == "127.0.0.1" && bound.Definition.ContainerPort == 80,
            "Catalog install must bind the explicitly selected host port for real HTTP readiness.");
        Expect(() => target.Bind(new(target.Id, target.Version, "new-site", 0, [], true)), ApplicationDeploymentProblemCodes.CatalogFieldInvalid);
        var oldInstall = """{"templateId":"personal-site","templateVersion":"1.0.0","name":"new-site","fields":[],"confirmed":true}""";
        try { JsonSerializer.Deserialize<InstallCatalogApplicationRequest>(oldInstall, RelaxKonOSJsonOptions.Default); throw new Exception("Versionless host port install accepted."); }
        catch (JsonException) { }
        var app = await manager.CreateAsync(new("upgrade-site", ApplicationSourceKind.Image, ContainerPort: 80, HostPort: 9080,
            Limits: new(2, 1024L * 1024 * 1024 + 1, 1024), Volumes: target.Volumes,
            Configuration: [new("TEXT", " custom=value "), new("TOKEN", "hidden_value", true)], SiteId: "Site01"), "catalog-test", default, target);
        catalog.Update(app.Id, before => before with { CatalogTemplateVersion = "0.9.0" });
        var before = catalog.Find(app.Id)!;
        var oldRevision = catalog.AddRevision(new(Guid.NewGuid(), app.Id, 0, before.SourceKind, "1.0",
            ApplicationDeploymentValidation.Reference("old-input"), "nginx:1.26.0-alpine", "sha256:old", "linux/amd64", null,
            before.WorkloadKind, before.ReadinessLevel, before.HealthCheckPath, "image default", [], before.ContainerPort,
            before.HostPort, before.BindAddress, before.Limits, before.Volumes, before.Configuration, before.SiteId, before.OwnerReference,
            DateTimeOffset.UtcNow, target.Id, "0.9.0"), out _);
        catalog.Activate(app.Id, oldRevision.Id, DateTimeOffset.Parse("2026-10-01T00:00:00.1234567+00:00"));
        before = catalog.Find(app.Id)!;
        var preview = manager.CatalogUpdatePreview(app.Id, target.Version);
        Check(preview.Blockers.Count == 0 && preview.ExpectedUpdatedAt == before.UpdatedAt && preview.ExpectedRevisionId == oldRevision.Id,
            "A preview must bind exact definition ticks and current revision.");
        Check(preview.CurrentTemplateVersion == "0.9.0" && preview.Target.Version == "1.0.0" && preview.CurrentImageReference == oldRevision.ImageReference && preview.TargetImageReference == target.ImageReference,
            "Diff must expose actual image and exact template versions, including an unavailable installed version.");
        Check(catalog.Find(app.Id) == before && operations.Read().Length == 0 && !JsonSerializer.Serialize(preview).Contains("hidden_value"),
            "Preview and catalog refresh must not mutate the instance or expose secret values.");
        Check(ApplicationCatalogUpdates.Preview(before with { ContainerPort = 8080 }, oldRevision, target, false).Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateDefinitionIncompatible),
            "Changed listening ports must require explicit definition editing.");
        Check(ApplicationCatalogUpdates.Preview(before with { Volumes = [] }, oldRevision, target, false).Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateDefinitionIncompatible),
            "Missing data mounts cannot be recreated silently.");
        var withSecret = ApplicationCatalog.Require("file-service", "1.0.0");
        Check(ApplicationCatalogUpdates.Preview(before with { CatalogTemplateId = withSecret.Id, Volumes = [.. withSecret.Volumes.Select(v => new ApplicationVolumeRecord(v.Name, v.ContainerPath, v.ReadOnly))] }, oldRevision, withSecret, false)
            .Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateDefinitionIncompatible), "New required secrets cannot be generated or exposed by updating a template.");
        Check(ApplicationCatalogUpdates.Preview(before with { Limits = new(0.5, 64L * 1024 * 1024, 10) }, oldRevision, target, false).Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateDefinitionIncompatible),
            "Minimum resource changes must be reviewed explicitly.");
        Check(ApplicationCatalogUpdates.Preview(before with { CatalogTemplateVersion = target.Version }, oldRevision, target, false).Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateAlreadyCurrent),
            "A current template must not create a phantom update.");
        var request = new UpdateCatalogApplicationRequest(target.Id, target.Version, before.UpdatedAt, oldRevision.Id, "0.9.0", true);
        var json = JsonSerializer.SerializeToNode(request, RelaxKonOSJsonOptions.Default)!.AsObject();
        json.Remove("expectedRevisionId");
        try { JsonSerializer.Deserialize<UpdateCatalogApplicationRequest>(json.ToJsonString(), RelaxKonOSJsonOptions.Default); throw new Exception("Missing revision baseline accepted."); }
        catch (JsonException) { }
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { ExpectedUpdatedAt = before.UpdatedAt.AddTicks(1) }), ApplicationDeploymentProblemCodes.DefinitionConflict);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { ExpectedRevisionId = Guid.NewGuid() }), ApplicationDeploymentProblemCodes.DefinitionConflict);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { TemplateVersion = "2.0.0" }), ApplicationDeploymentProblemCodes.CatalogTemplateVersionUnavailable);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { Confirmed = false }), ApplicationDeploymentProblemCodes.ConfirmationRequired);

        var logs = new ApplicationDeploymentLiveLogs();
        var service = new ApplicationDeploymentService(catalog, secrets, new(environment, options, NullLogger<ApplicationDeploymentStagingStore>.Instance), runtime, null!, options, environment, logs, null!, NullLogger<ApplicationDeploymentService>.Instance);
        var coordinator = new ApplicationDeploymentCoordinator(operations, catalog, service, options, logs, new TestApplicationLifetime(), new CatalogUpdateEventPublisher(), NullLogger<ApplicationDeploymentCoordinator>.Instance);
        await coordinator.StartAsync(default);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(p => {
            p.AddPolicy(ApplicationDeploymentEndpoints.ReadPolicy, x => x.RequireRole("controller", "reader"));
            p.AddPolicy(ApplicationDeploymentEndpoints.ManagePolicy, x => x.RequireRole("controller"));
        });
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton(options); builder.Services.AddSingleton(manager); builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>(); builder.Services.AddSingleton<ApplicationDeploymentStagingStore>();
        builder.Services.AddSingleton<IFileService>(_ => throw new NotSupportedException());
        await using var host = builder.Build();
        host.UseAuthentication(); host.UseAuthorization(); host.MapApplicationDeploymentEndpoints(); await host.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var route = ApplicationDeploymentApiRoutes.CatalogUpdate(app.Id);
        using (var denied = await http.GetAsync(route + "?templateVersion=1.0.0")) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Preview requires authentication.");
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using (var read = await http.GetAsync(route + "?templateVersion=1.0.0")) {
            var receipt = await read.Content.ReadFromJsonAsync<CatalogApplicationUpdatePreviewDto>(RelaxKonOSJsonOptions.Default);
            Check(read.IsSuccessStatusCode && receipt!.ExpectedUpdatedAt == before.UpdatedAt && receipt.CurrentTemplateVersion == "0.9.0", "Production GET must preserve complete baseline precision.");
        }
        using (var denied = await http.PostAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Reading a preview does not grant update permission.");
        http.DefaultRequestHeaders.Remove("X-Test-Role"); http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
        using (var noKey = await http.PostAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(noKey.StatusCode == HttpStatusCode.BadRequest, "Update requires an idempotency key.");
        http.DefaultRequestHeaders.Add("Idempotency-Key", "catalog-update-test");
        using (var unconfirmed = await http.PostAsJsonAsync(route, request with { Confirmed = false }, RelaxKonOSJsonOptions.Default)) Check(unconfirmed.StatusCode == HttpStatusCode.BadRequest, "Update requires explicit confirmation.");
        using (var missingBaseline = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.Deploy(app.Id), new { source = new { imageReference = target.ImageReference }, confirmed = true }))
            Check(missingBaseline.StatusCode == HttpStatusCode.BadRequest, "Deploy must reject the old versionless wire body.");
        using (var staleDeploy = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.Deploy(app.Id), new DeployApplicationRequest(new(ImageReference: target.ImageReference), before.UpdatedAt.AddTicks(1), true), RelaxKonOSJsonOptions.Default))
            Check(staleDeploy.StatusCode == HttpStatusCode.Conflict && operations.Read().Length == 0, "Ordinary revision submission must atomically reject stale definition ticks before queueing.");
        DeploymentOperationDto queued;
        using (var accepted = await http.PostAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) {
            Check(accepted.StatusCode == HttpStatusCode.Accepted, "Confirmed exact update must queue a durable operation.");
            queued = (await accepted.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default))!;
        }
        await controlled.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(catalog.Find(app.Id) == before && operations.Read().Length == 1 && manager.CatalogUpdatePreview(app.Id, target.Version).Blockers.Contains(ApplicationDeploymentProblemCodes.ResourceConflict),
            "Queueing and failed preflight must not mark the template activated.");
        using (var replay = await http.PostAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check((await replay.Content.ReadFromJsonAsync<DeploymentOperationDto>(RelaxKonOSJsonOptions.Default))!.OperationId == queued.OperationId, "Same key must return the original operation.");
        using (var conflict = await http.PostAsJsonAsync(route, request with { TemplateVersion = "2.0.0" }, RelaxKonOSJsonOptions.Default)) Check(conflict.StatusCode == HttpStatusCode.Conflict, "Same key with different intent must conflict.");
        controlled.Status.SetResult(new(false, "docker.unavailable", null, null, null));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (coordinator.Get(queued.OperationId)!.State is DeploymentOperationState.Queued or DeploymentOperationState.Running && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Check(coordinator.Get(queued.OperationId)!.State == DeploymentOperationState.Failed && catalog.Find(app.Id) == before && catalog.ReadRevisions(app.Id).Length == 1,
            "A failed worker retains old binding, definition, data references and immutable revision.");
        var published = catalog.AddRevision(oldRevision with { Id = Guid.NewGuid(), Number = 0, CatalogTemplateVersion = target.Version, ImageReference = target.ImageReference }, out _);
        catalog.Activate(app.Id, published.Id, DateTimeOffset.UtcNow);
        var activated = catalog.Find(app.Id)!;
        Check(activated.CatalogTemplateVersion == target.Version && activated.Limits == before.Limits && activated.Configuration.SequenceEqual(before.Configuration) && activated.Volumes.SequenceEqual(before.Volumes) && activated.SiteId == before.SiteId,
            "Successful activation changes the binding while preserving every operator field and secret version.");
        catalog.Activate(app.Id, oldRevision.Id, DateTimeOffset.UtcNow);
        Check(catalog.Find(app.Id)!.CatalogTemplateVersion == "0.9.0" && new ApplicationDeploymentCatalogStore(environment, options).Find(app.Id)!.CurrentRevisionId == oldRevision.Id,
            "Rollback restores the revision's real binding and the result survives reopening the store.");
        Check(secrets.Reveal(app.Id, "TOKEN", 1) == "hidden_value" && controlled.Calls.All(x => x is nameof(IDockerEngineService.ListContainersAsync) or nameof(IDockerEngineService.GetStatusAsync)), "Update verification must retain secrets and execute no Docker write.");
        await coordinator.StopAsync(default); await host.StopAsync();
        Console.WriteLine("BP16 catalog preview, exact version, HTTP permissions, confirmation, replay, failure, activation and rollback checks passed.");
    }

    private static void Expect(Action action, string problem) {
        try { action(); throw new Exception("Expected " + problem); }
        catch (ApplicationDeploymentException error) { Check(error.ProblemCode == problem, "Unexpected problem code."); }
    }
    private static void Check(bool condition, string message) => TestAssert.Assert(condition, message);
}

public class CatalogUpdateEngine : DispatchProxy
{
    public TaskCompletionSource<DockerStatusDto> Status { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Collections.Concurrent.ConcurrentBag<string> Calls { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        Calls.Add(method!.Name);
        if (method.Name == nameof(IDockerEngineService.ListContainersAsync)) return Task.FromResult<IReadOnlyList<DockerContainerDto>>([]);
        if (method.Name == nameof(IDockerEngineService.GetStatusAsync)) { Started.TrySetResult(); return Status.Task; }
        throw new NotSupportedException("Docker writes are outside this fixture.");
    }
}

internal sealed class CatalogUpdateEventPublisher : RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher
{
    public Task PublishAsync(RelaxKonOS.Server.EventAlerts.OperationalEventSignal signal, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
