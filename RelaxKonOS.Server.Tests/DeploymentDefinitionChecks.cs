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

internal static class DeploymentDefinitionChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-bp16-definition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root); }
        finally
        {
            var resolved = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("relaxkonos-bp16-definition-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected definition fixture directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task VerifyAsync(string root)
    {
        var environment = new TestHostEnvironment(root);
        var options = new ApplicationDeploymentOptions();
        var catalog = new ApplicationDeploymentCatalogStore(environment, options);
        var secrets = new ApplicationDeploymentSecretStore(environment, options, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))), catalog);
        var operations = new ApplicationDeploymentOperationStore(environment, options);
        var engine = DispatchProxy.Create<IDockerEngineService, DefinitionReadEngine>();
        var clients = new FixtureHttpClientFactory([]);
        var runtime = new ApplicationDeploymentRuntime(engine, options, clients, NullLogger<ApplicationDeploymentRuntime>.Instance);
        var manager = new ApplicationDeploymentManager(catalog, secrets, operations, runtime, options,
            new ApplicationDeploymentImageTagCatalog(clients, NullLogger<ApplicationDeploymentImageTagCatalog>.Instance));
        var created = await manager.CreateAsync(new CreateApplicationRequest("website", ApplicationSourceKind.Image,
            HostPort: 9080, HealthCheckPath: "/ready", Limits: new(1.5, 16L * 1024 * 1024 + 1, 512),
            Volumes: [new("data", "/app/data:live", true)], Configuration: [new("TEXT", " value=kept "), new("TOKEN", "original_secret", true)], SiteId: "Site01"),
            "definition-test", default, ApplicationCatalog.Require("personal-site", "1.0.0"));
        var record = catalog.Find(created.Id)!;
        var revision = catalog.AddRevision(new RevisionRecord(Guid.NewGuid(), created.Id, 0, record.SourceKind, "1.0.0",
            ApplicationDeploymentValidation.Reference("fixture-image"), "nginx:1.27.3-alpine", "sha256:abc", "linux/amd64", null,
            record.WorkloadKind, record.ReadinessLevel, record.HealthCheckPath, "image-entry", [], record.ContainerPort, record.HostPort,
            record.BindAddress, record.Limits, record.Volumes, record.Configuration, record.SiteId, record.OwnerReference, DateTimeOffset.UtcNow,
            record.CatalogTemplateId, record.CatalogTemplateVersion), out _);
        var preciseVersion = DateTimeOffset.Parse("2026-10-01T00:00:00.1234567+00:00");
        catalog.Activate(created.Id, revision.Id, preciseVersion);
        var baseline = (await manager.SnapshotAsync(created.Id, default)).Application;
        var request = new UpdateApplicationRequest("renamed", baseline.WorkloadKind, baseline.ReadinessLevel, baseline.UpdatedAt,
            baseline.HealthCheckPath, baseline.ContainerPort, baseline.HostPort, baseline.BindAddress, baseline.Limits, baseline.Volumes,
            [new("TEXT", " value=kept "), new("TOKEN", "rotated_secret", true, 1)], baseline.SiteId);

        var json = JsonSerializer.SerializeToNode(request, RelaxKonOSJsonOptions.Default)!.AsObject();
        var missingVersion = JsonNode.Parse(json.ToJsonString())!.AsObject(); missingVersion.Remove("expectedUpdatedAt");
        try { JsonSerializer.Deserialize<UpdateApplicationRequest>(missingVersion.ToJsonString(), RelaxKonOSJsonOptions.Default); throw new Exception("Missing definition version was accepted."); }
        catch (JsonException) { }
        Check(JsonSerializer.Deserialize<UpdateApplicationRequest>(json.ToJsonString(), RelaxKonOSJsonOptions.Default)!.ExpectedUpdatedAt == preciseVersion,
            "The complete seven-tick definition version must survive serialization.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ProgressTestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization(policies =>
        {
            policies.AddPolicy(ApplicationDeploymentEndpoints.ReadPolicy, p => p.RequireRole("controller", "reader"));
            policies.AddPolicy(ApplicationDeploymentEndpoints.ManagePolicy, p => p.RequireRole("controller"));
        });
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton(options); builder.Services.AddSingleton(manager);
        builder.Services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>();
        builder.Services.AddSingleton<ApplicationDeploymentCoordinator>();
        builder.Services.AddSingleton<ApplicationDeploymentStagingStore>();
        builder.Services.AddSingleton<IFileService>(_ => throw new NotSupportedException("File operations are outside this test."));
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapApplicationDeploymentEndpoints(); await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
        var route = ApplicationDeploymentApiRoutes.Application(created.Id);
        using (var denied = await http.PutAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Anonymous definition updates must be denied.");
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using (var denied = await http.PutAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Read permission must not grant definition edits.");
        http.DefaultRequestHeaders.Remove("X-Test-Role"); http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
        using (var noKey = await http.PutAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(noKey.StatusCode == HttpStatusCode.BadRequest, "Definition edits require an idempotency key.");
        http.DefaultRequestHeaders.Add("Idempotency-Key", "definition-save");
        using (var missing = await http.PutAsJsonAsync(route, missingVersion)) Check(missing.StatusCode == HttpStatusCode.BadRequest, "The HTTP route must reject the old versionless update body.");
        ApplicationDto saved;
        using (var response = await http.PutAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default))
        {
            Check(response.IsSuccessStatusCode, "Current definition update failed: " + response.StatusCode);
            saved = (await response.Content.ReadFromJsonAsync<ApplicationDto>(RelaxKonOSJsonOptions.Default))!;
        }
        Check(saved.Name == "renamed" && saved.UpdatedAt > baseline.UpdatedAt, "Definition edits must advance their optimistic version.");
        Check(saved.Id == baseline.Id && saved.SourceKind == baseline.SourceKind && saved.CatalogTemplateId == "personal-site" && saved.CatalogTemplateVersion == "1.0.0",
            "A definition edit must preserve source and exact catalog identity.");
        Check(saved.Limits == baseline.Limits && saved.Volumes.SequenceEqual(baseline.Volumes) && saved.SiteId == baseline.SiteId
            && saved.HealthCheckPath == baseline.HealthCheckPath && saved.HostPort == baseline.HostPort && saved.ContainerPort == baseline.ContainerPort && saved.BindAddress == baseline.BindAddress,
            "Unedited resource ceilings, paths, ports, readiness and site association must remain exact.");
        Check(saved.Configuration.Single(x => !x.IsSecret).Value == " value=kept " && saved.Configuration.Single(x => x.IsSecret) is { Value: null, SecretVersion: 2 },
            "A rotated secret must return only its new version; ordinary values must remain exact.");
        Check(catalog.FindRevision(revision.Id) == revision && saved.CurrentRevisionId == revision.Id && operations.Read().Length == 0,
            "Saving intent must not publish a revision, queue Docker work, or rewrite the current immutable revision.");
        Check(secrets.Reveal(created.Id, "TOKEN", 1) == "original_secret" && secrets.Reveal(created.Id, "TOKEN", 2) == "rotated_secret",
            "Both the current definition secret and the retained revision secret must remain resolvable.");
        using (var replay = await http.PutAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default))
        {
            var replayed = await replay.Content.ReadFromJsonAsync<ApplicationDto>(RelaxKonOSJsonOptions.Default);
            Check(replay.IsSuccessStatusCode && replayed!.UpdatedAt == saved.UpdatedAt && replayed.Configuration.Single(x => x.IsSecret).SecretVersion == 2,
                "A repeated key must return the original response without rotating or updating again.");
        }
        http.DefaultRequestHeaders.Remove("Idempotency-Key"); http.DefaultRequestHeaders.Add("Idempotency-Key", "stale-save");
        using (var conflict = await http.PutAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default))
            Check(conflict.StatusCode == HttpStatusCode.Conflict && (await conflict.Content.ReadAsStringAsync()).Contains(ApplicationDeploymentProblemCodes.DefinitionConflict),
                "A new key with a stale definition version must return a stable conflict.");
        Check(!secrets.Has(created.Id, "TOKEN", 3), "A known stale draft must be rejected before secret rotation.");
        Check(catalog.Find(created.Id)!.UpdatedAt == saved.UpdatedAt && catalog.Find(created.Id)!.Name == "renamed", "A stale save must not overwrite intent.");
        var retained = new ApplicationDeploymentCatalogStore(environment, options);
        Check(retained.Find(created.Id)!.UpdatedAt == saved.UpdatedAt && retained.FindRevision(revision.Id)!.CatalogTemplateVersion == "1.0.0",
            "Definition version and immutable catalog identity must survive reopening the real store.");
        var ledger = File.ReadAllText(Path.Combine(root, options.RootDirectory, "definition-mutations.json"));
        Check(!ledger.Contains("rotated_secret") && !ledger.Contains("original_secret"), "The real idempotency ledger must never contain secret bodies.");
        operations.Create(created.Id, saved.Name, DeploymentOperationKind.Deploy, "fixture", "active-fixture", ApplicationDeploymentValidation.Reference("active"), [], out _);
        http.DefaultRequestHeaders.Remove("Idempotency-Key"); http.DefaultRequestHeaders.Add("Idempotency-Key", "blocked-save");
        using (var blocked = await http.PutAsJsonAsync(route, request with { ExpectedUpdatedAt = saved.UpdatedAt }, RelaxKonOSJsonOptions.Default))
            Check(blocked.StatusCode == HttpStatusCode.Conflict && (await blocked.Content.ReadAsStringAsync()).Contains(ApplicationDeploymentProblemCodes.ResourceConflict),
                "An active application operation must block definition replacement.");
        Check(((DefinitionReadEngine)(object)engine).Calls.All(call => call == nameof(IDockerEngineService.ListContainersAsync)), "Definition verification must not execute Docker actions.");
        var readEngine = (DefinitionReadEngine)(object)engine;
        foreach (var problem in new[] { "docker.not_installed", "docker.permission_denied", "docker.unavailable" })
        {
            readEngine.ReadProblem = problem;
            using var response = await http.GetAsync(ApplicationDeploymentApiRoutes.Applications);
            var applications = await response.Content.ReadFromJsonAsync<ApplicationDto[]>(RelaxKonOSJsonOptions.Default);
            Check(response.IsSuccessStatusCode && applications!.Single().ActualState == ApplicationActualState.Unknown,
                "Docker read failure must preserve definitions with unknown observed state instead of returning HTTP 500: " + problem);
        }
        readEngine.ReadProblem = null;
        var other = await manager.CreateAsync(new CreateApplicationRequest("other-app", ApplicationSourceKind.Image, ApplicationWorkloadKind.Worker,
            ApplicationReadinessLevel.Process, HealthCheckPath: null, Configuration: [new("OTHER", "other_1", true)]), "definition-test", default);
        for (var index = 2; index <= 4; index++) secrets.Set(other.Id, "OTHER", "other_" + index);
        catalog.Update(other.Id, before => before with { Configuration = [new("OTHER", true, 4, null)] });
        var otherRecord = catalog.Find(other.Id)!;
        var otherRevision = catalog.AddRevision(revision with
        {
            Id = Guid.NewGuid(), ApplicationId = other.Id, Number = 0, WorkloadKind = ApplicationWorkloadKind.Worker,
            ReadinessLevel = ApplicationReadinessLevel.Process, HealthCheckPath = null, HostPort = null, SiteId = null,
            Volumes = [], Limits = otherRecord.Limits, Configuration = otherRecord.Configuration, CatalogTemplateId = null, CatalogTemplateVersion = null,
        }, out _);
        for (var index = 5; index <= 9; index++) secrets.Set(other.Id, "OTHER", "other_" + index);
        for (var index = 3; index <= 6; index++) secrets.Set(created.Id, "TOKEN", "rotated_" + index);
        Check(secrets.Has(created.Id, "TOKEN", 1) && secrets.Has(created.Id, "TOKEN", 2) && secrets.Has(created.Id, "TOKEN", 6)
            && !secrets.Has(created.Id, "TOKEN", 3), "Multiple rotations must retain the immutable revision and definition versions while pruning unreferenced old values.");
        Check(secrets.Reveal(other.Id, "OTHER", 4) == "other_4" && secrets.Version(other.Id, "OTHER") == 9 && catalog.FindRevision(otherRevision.Id) is not null,
            "Rotating one application's secret must not prune another application's pinned secret version.");
        var reopenedSecrets = new ApplicationDeploymentSecretStore(environment, options, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))), catalog);
        Check(reopenedSecrets.Reveal(created.Id, "TOKEN", 1) == "original_secret" && reopenedSecrets.Reveal(other.Id, "OTHER", 4) == "other_4",
            "Pinned encrypted secret versions must survive reopening the actual protected store.");
        await app.StopAsync();
        Console.WriteLine("BP16 definition contract, HTTP authorization, replay, conflict, exact fields, secret retention, and immutable revision checks passed.");
    }

    private static void Check(bool condition, string message) => TestAssert.Assert(condition, message);
}

public class DefinitionReadEngine : DispatchProxy
{
    public List<string> Calls { get; } = [];
    public string? ReadProblem { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls.Add(targetMethod!.Name);
        if (targetMethod.Name == nameof(IDockerEngineService.ListContainersAsync) && ReadProblem is { } problem)
            throw new DockerReadException(problem);
        return targetMethod.Name == nameof(IDockerEngineService.ListContainersAsync)
            ? Task.FromResult<IReadOnlyList<DockerContainerDto>>([])
            : throw new NotSupportedException("Docker writes are outside the definition fixture.");
    }
}
