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
using RelaxKonOS.Server.Residency;

internal static class CatalogUpdateChecks
{
    internal static void VerifyGeneratedFixtures(string directory)
    {
        var releases = ApplicationCatalog.Load(directory);
        var webDb = releases.Single(release => release.Id == "fixture-web-db");
        var plan = webDb.CompileTopology(Guid.NewGuid(), 18083);
        Check(plan.WebService == "web" && plan.Services.Count == 2
            && plan.Services.Single(service => service.Id == "db").Volumes is [{ Name: "db-data", Path: "/var/lib/postgresql/data" }],
            "The generated PostgreSQL release must compile as the two-service acceptance plan.");
        var ntfy = releases.Where(release => release.Id == "fixture-ntfy").OrderBy(release => release.Version, StringComparer.Ordinal).ToArray();
        Check(ntfy.Length == 2 && ntfy[0].Version == "2.37.0" && ntfy[1].Version == "2.37.2"
            && ntfy[1].UpgradeFrom is [{ Version: "2.37.0" } edge] && edge.ImageReference == ntfy[0].ImageReference
            && ntfy[0].Volumes.SequenceEqual(ntfy[1].Volumes)
            && ntfy[0].ImageReference != ntfy[1].ImageReference,
            "Generated ntfy releases must bind the exact compatible predecessor with the same volume contract.");
        Console.WriteLine("Generated acceptance manifests loaded through the production catalog reader.");
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-catalog-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyAsync(string root)
    {
        VerifyManifestDirectory(root);
        VerifyTopologyManifest();
        await VerifyEngineBoundariesAsync();
        await VerifyManagedComposeRecoveryAsync(Path.Combine(root, "compose-recovery"));
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
        var target = ApplicationCatalog.Require("upgrade-fixture", "1.0.0");
        Check(!(target with { UpgradeFrom = [new("0.9.0", target.ImageReference.Replace("registry.example", "mirror.example"))] }).IsValid(),
            "A different repository name for the same digest cannot be declared as an upgrade source.");
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
            ApplicationDeploymentValidation.Reference("old-input"), target.UpgradeFrom!.Single().ImageReference, "sha256:" + new string('c', 64), "linux/amd64", null,
            before.WorkloadKind, before.ReadinessLevel, before.HealthCheckPath, "image default", [], before.ContainerPort,
            before.HostPort, before.BindAddress, before.Limits, before.Volumes, before.Configuration, before.SiteId, before.OwnerReference,
            DateTimeOffset.UtcNow, target.Id, "0.9.0"), out _);
        catalog.Activate(app.Id, oldRevision.Id, DateTimeOffset.Parse("2026-10-01T00:00:00.1234567+00:00"));
        before = catalog.Find(app.Id)!;
        var preview = manager.CatalogUpdatePreview(app.Id, target.Version);
        Check(preview.Blockers.Count == 0 && preview.ExpectedUpdatedAt == before.UpdatedAt && preview.ExpectedRevisionId == oldRevision.Id,
            "A preview must bind exact definition ticks and current revision.");
        Check(preview.CurrentTemplateVersion == "0.9.0" && preview.Target.Version == "1.0.0" && preview.CurrentImageReference == oldRevision.ImageReference && preview.TargetImageReference == target.ImageReference,
            "Diff must expose the exact target image and the verified predecessor, even when the predecessor is no longer listed.");
        Check(ApplicationCatalogUpdates.Preview(before, oldRevision, target with { UpgradeFrom = null }, false)
            .Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateSourceUnverified),
            "An undeclared predecessor must block an upgrade.");
        Check(ApplicationCatalogUpdates.Preview(before, oldRevision with { ImageReference = target.ImageReference }, target, false)
            .Blockers.Contains(ApplicationDeploymentProblemCodes.CatalogUpdateSourceUnverified),
            "A different image under the same source version cannot satisfy the declaration.");
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
        var request = new UpdateCatalogApplicationRequest(target.Id, target.Version, before.UpdatedAt, oldRevision.Id, "0.9.0", target.ImageReference, true);
        var json = JsonSerializer.SerializeToNode(request, RelaxKonOSJsonOptions.Default)!.AsObject();
        json.Remove("expectedRevisionId");
        try { JsonSerializer.Deserialize<UpdateCatalogApplicationRequest>(json.ToJsonString(), RelaxKonOSJsonOptions.Default); throw new Exception("Missing revision baseline accepted."); }
        catch (JsonException) { }
        json = JsonSerializer.SerializeToNode(request, RelaxKonOSJsonOptions.Default)!.AsObject();
        json.Remove("expectedTargetImageReference");
        try { JsonSerializer.Deserialize<UpdateCatalogApplicationRequest>(json.ToJsonString(), RelaxKonOSJsonOptions.Default); throw new Exception("Missing target image accepted."); }
        catch (JsonException) { }
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { ExpectedUpdatedAt = before.UpdatedAt.AddTicks(1) }), ApplicationDeploymentProblemCodes.DefinitionConflict);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { ExpectedRevisionId = Guid.NewGuid() }), ApplicationDeploymentProblemCodes.DefinitionConflict);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { TemplateVersion = "2.0.0" }), ApplicationDeploymentProblemCodes.CatalogTemplateVersionUnavailable);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { Confirmed = false }), ApplicationDeploymentProblemCodes.ConfirmationRequired);
        Expect(() => ApplicationCatalogUpdates.Validate(before, oldRevision, request with { ExpectedTargetImageReference = oldRevision.ImageReference }),
            ApplicationDeploymentProblemCodes.DefinitionConflict);

        var logs = new ApplicationDeploymentLiveLogs();
        var compose = new FakeComposeService();
        using var docker = DockerChecks.NewStackCoordinator(Path.Combine(root, "compose-install"), compose, new TestApplicationLifetime(), 4);
        await docker.StartAsync(default);
        var dispatch = new ApplicationManagedComposeDispatch(operations, docker);
        var install = new ApplicationManagedComposeInstall(catalog, secrets, runtime, dispatch, options);
        var service = new ApplicationDeploymentService(catalog, secrets, new(environment, options, NullLogger<ApplicationDeploymentStagingStore>.Instance), runtime, null!, options, environment, logs, null!, NullLogger<ApplicationDeploymentService>.Instance, install);
        var coordinator = new ApplicationDeploymentCoordinator(operations, catalog, service, options, logs, new TestApplicationLifetime(), new CatalogUpdateEventPublisher(), NullLogger<ApplicationDeploymentCoordinator>.Instance, dispatch);
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
        builder.Services.AddSingleton(dispatch);
        builder.Services.AddSingleton<ApplicationDeploymentDefinitionMutationStore>(); builder.Services.AddSingleton<ApplicationDeploymentStagingStore>();
        builder.Services.AddSingleton<IFileService>(_ => throw new NotSupportedException());
        var composeLeases = new TestResidentBackgroundLeases();
        builder.Services.AddSingleton<IDockerComposeService>(compose);
        builder.Services.AddSingleton<IResidentModuleWorkLeaseSource>(composeLeases);
        await using var host = builder.Build();
        host.UseAuthentication(); host.UseAuthorization(); host.MapApplicationDeploymentEndpoints(); await host.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var route = ApplicationDeploymentApiRoutes.CatalogUpdate(app.Id);
        var topologyRoute = ApplicationDeploymentApiRoutes.Catalog + "/fixture-web-db-plan/1.0.0/topology?hostPort=18083";
        using (var denied = await http.GetAsync(topologyRoute))
            Check(denied.StatusCode == HttpStatusCode.Unauthorized && composeLeases.Acquired == 0,
                "An unauthorized topology read must not admit Compose parser work.");
        using (var denied = await http.GetAsync(route + "?templateVersion=1.0.0")) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Preview requires authentication.");
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using (var topology = await http.GetAsync(topologyRoute))
        {
            var reply = await topology.Content.ReadFromJsonAsync<CatalogApplicationTopologyDto>(RelaxKonOSJsonOptions.Default);
            Check(topology.IsSuccessStatusCode && reply is { BindAddress: "127.0.0.1", HostPort: 18083, WebService: "web", Services.Count: 2 }
                && compose.ManagedPreview is { HostPort: 18083 } && composeLeases.Acquired == 1 && operations.Read().Length == 0,
                "The topology route passes a typed plan to Compose and returns public topology without allocating an operation.");
        }
        var detail = $"{ApplicationDeploymentApiRoutes.Catalog}/{target.Id}/{target.Version}";
        using (var release = await http.GetAsync(detail))
            Check((await release.Content.ReadFromJsonAsync<ApplicationCatalogTemplateDto>(RelaxKonOSJsonOptions.Default))?.Version == target.Version,
                "Catalogue detail must select the exact published release.");
        using (var missingVersion = await http.GetAsync($"{ApplicationDeploymentApiRoutes.Catalog}/{target.Id}"))
            Check(missingVersion.StatusCode == HttpStatusCode.NotFound, "The versionless catalogue route must not remain registered.");
        using (var denied = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.Catalog + "/install",
            new InstallCatalogApplicationRequest(target.Id, target.Version, "denied-site", 8088, [], true), RelaxKonOSJsonOptions.Default))
            Check(denied.StatusCode == HttpStatusCode.Forbidden && catalog.FindByName("denied-site") is null && operations.Read().Length == 0,
                "A read-only caller cannot allocate an application or an operation through catalogue install.");
        using (var read = await http.GetAsync(route + "?templateVersion=1.0.0")) {
            var receipt = await read.Content.ReadFromJsonAsync<CatalogApplicationUpdatePreviewDto>(RelaxKonOSJsonOptions.Default);
            Check(read.IsSuccessStatusCode && receipt!.ExpectedUpdatedAt == before.UpdatedAt && receipt.CurrentTemplateVersion == "0.9.0", "Production GET must preserve complete baseline precision.");
        }
        using (var denied = await http.PostAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Reading a preview does not grant update permission.");
        http.DefaultRequestHeaders.Remove("X-Test-Role"); http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
        using (var blockedInstall = new HttpRequestMessage(HttpMethod.Post, ApplicationDeploymentApiRoutes.CatalogInstall))
        {
            blockedInstall.Headers.Add("Idempotency-Key", "multi-execution-unavailable");
            blockedInstall.Content = JsonContent.Create(new InstallCatalogApplicationRequest("fixture-web-db-plan", "1.0.0",
                "unavailable-multi", 18083, [new("password", "fixture-only")], true), options: RelaxKonOSJsonOptions.Default);
            using var rejected = await http.SendAsync(blockedInstall);
            Check(rejected.StatusCode == HttpStatusCode.Conflict && catalog.FindByName("unavailable-multi") is null && operations.Read().Length == 0,
                "A preview-only release must not allocate a definition, secrets or an operation.");
        }
        using (var noKey = await http.PostAsJsonAsync(route, request, RelaxKonOSJsonOptions.Default)) Check(noKey.StatusCode == HttpStatusCode.BadRequest, "Update requires an idempotency key.");
        http.DefaultRequestHeaders.Add("Idempotency-Key", "catalog-update-test");
        using (var unconfirmed = await http.PostAsJsonAsync(route, request with { Confirmed = false }, RelaxKonOSJsonOptions.Default)) Check(unconfirmed.StatusCode == HttpStatusCode.BadRequest, "Update requires explicit confirmation.");
        using (var missingBaseline = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.Deploy(app.Id), new { source = new { imageReference = target.ImageReference }, confirmed = true }))
            Check(missingBaseline.StatusCode == HttpStatusCode.BadRequest, "Deploy must reject the old versionless wire body.");
        using (var staleDeploy = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.Deploy(app.Id), new DeployApplicationRequest(new(ImageReference: target.ImageReference), before.UpdatedAt.AddTicks(1), true), RelaxKonOSJsonOptions.Default))
            Check(staleDeploy.StatusCode == HttpStatusCode.Conflict && operations.Read().Length == 0, "Ordinary revision submission must atomically reject stale definition ticks before queueing.");
        using (var bypass = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.Deploy(app.Id), new DeployApplicationRequest(new(ImageReference: target.ImageReference), before.UpdatedAt, true), RelaxKonOSJsonOptions.Default))
            Check(bypass.StatusCode == HttpStatusCode.Conflict && operations.Read().Length == 0, "Generic deployment cannot bypass the catalog upgrade gate.");
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

        var retention = await manager.CreateAsync(bound.Definition with
        {
            Name = "retained-site", Configuration = [new("TOKEN", "retained_secret", true)],
        }, "catalog-test", default, target);
        var retainedDefinition = catalog.Find(retention.Id)!;
        var retainedRevision = catalog.AddRevision(published with
        {
            Id = Guid.NewGuid(), ApplicationId = retention.Id, Number = 0, SiteId = null,
            Configuration = retainedDefinition.Configuration,
        }, out _);
        catalog.Activate(retention.Id, retainedRevision.Id, DateTimeOffset.UtcNow);
        catalog.BindRuntime(retention.Id, "retained-container", "old-id", null);
        controlled.ReportedStatus = new(true, "", "29.0", "linux", "amd64");
        var volume = retainedRevision.Volumes.Single();
        var volumeName = ApplicationDeploymentValidation.VolumeName(retention.Id, volume.Name);
        controlled.Volume = new(volumeName, "local", "/private",
            ApplicationDeploymentValidation.Labels(retention.Id, retention.Name, retainedRevision.Id, retainedRevision.Number, null,
                ApplicationDeploymentValidation.RoleVolume)
                .Select(label => label.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]), []);
        var uninstall = new DeploymentRequest(retention.Id, DeploymentOperationKind.Delete);
        Expect(() => coordinator.Start(uninstall with { DeleteVolumes = true }, "catalog-test", "no-purge"), ApplicationDeploymentProblemCodes.NotSupported);
        var removed = coordinator.Start(uninstall, "catalog-test", "retention-uninstall");
        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (coordinator.Get(removed.OperationId)!.State is DeploymentOperationState.Queued or DeploymentOperationState.Running && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        var retained = new ApplicationDeploymentCatalogStore(environment, options).Find(retention.Id)!;
        Check(coordinator.Get(removed.OperationId)!.State == DeploymentOperationState.Succeeded
            && retained.DesiredState == ApplicationDesiredState.Retained && retained.CurrentRevisionId == retainedRevision.Id
            && retained.ContainerId is null && retained.ContainerName is null && secrets.Reveal(retention.Id, "TOKEN", 1) == "retained_secret",
            "Uninstall clears the runtime binding but retains the original instance, revision, volume and secret version on disk.");
        Check(coordinator.Start(uninstall, "catalog-test", "retention-uninstall").OperationId == removed.OperationId,
            "Uninstall replay keeps its original operation after the retained state is committed.");
        var reinstallRoute = ApplicationDeploymentApiRoutes.CatalogReinstall(retention.Id);
        using (var unconfirmed = await http.PostAsJsonAsync(reinstallRoute, new ReinstallCatalogApplicationRequest(retainedRevision.Id), RelaxKonOSJsonOptions.Default))
            Check(unconfirmed.StatusCode == HttpStatusCode.BadRequest, "Reinstall requires explicit confirmation.");
        using (var stale = await http.PostAsJsonAsync(reinstallRoute, new ReinstallCatalogApplicationRequest(Guid.NewGuid(), true), RelaxKonOSJsonOptions.Default))
            Check(stale.StatusCode == HttpStatusCode.Conflict, "Reinstall rejects any revision other than the retained instance revision.");
        Expect(() => coordinator.Start(new(retention.Id, DeploymentOperationKind.Start), "catalog-test", "start-retained"),
            ApplicationDeploymentProblemCodes.NotSupported);

        var reinstall = new DeploymentRequest(retention.Id, DeploymentOperationKind.Reinstall, RevisionId: retainedRevision.Id);
        var pending = operations.Create(retention.Id, retention.Name, reinstall.Kind, "catalog-test", "original-reinstall",
            ApplicationDeploymentService.Fingerprint(reinstall), ApplicationDeploymentService.Resources(retention.Id), out _);
        operations.Update(pending.Operation.OperationId, current => current with { State = DeploymentOperationState.Running,
            Stage = DeploymentStage.Creating, StartedAt = DateTimeOffset.UtcNow }, "test-dispatched");
        var callCount = controlled.Calls.Count;
        await coordinator.StartAsync(default);
        Check(coordinator.Start(reinstall, "catalog-test", "original-reinstall").OperationId == pending.Operation.OperationId
            && coordinator.Get(pending.Operation.OperationId)!.RecoveryProblemCode == ApplicationDeploymentService.RetainedOutcomeUnknown
            && controlled.Calls.Count == callCount,
            "Recovery keeps the interrupted reinstall receipt without dispatching a new Engine command.");
        Expect(() => coordinator.Start(reinstall, "catalog-test", "new-reinstall"), ApplicationDeploymentProblemCodes.ResourceConflict);

        await VerifyManagedInstallAsync(http, coordinator, options, compose, controlled, clients, catalog, operations, secrets,
            Path.Combine(root, options.RootDirectory, "definition-mutations.json"));
        await coordinator.StopAsync(default); await host.StopAsync(); await docker.StopAsync(default);
        Console.WriteLine("Catalog checks passed, including exact predecessor and target digest, bypass refusal, retained records and original receipt fencing.");
    }

    private static async Task VerifyManagedInstallAsync(HttpClient http, ApplicationDeploymentCoordinator coordinator,
        ApplicationDeploymentOptions options, FakeComposeService compose, CatalogUpdateEngine engine, FixtureHttpClientFactory clients,
        ApplicationDeploymentCatalogStore catalog, ApplicationDeploymentOperationStore operations, ApplicationDeploymentSecretStore secrets,
        string installReceipts)
    {
        var release = ApplicationCatalog.Require("fixture-web-db-plan", "1.0.0");
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        const string password = "compose-install-only-secret";
        var request = new InstallCatalogApplicationRequest(release.Id, release.Version, "managed-web-db", port, [new("password", password)], true);
        options.EnableManagedComposePreview = true;
        engine.LocalCompose = true;
        engine.Images = release.Services.ToDictionary(service => service.ImageReference,
            service => new DockerImageIdentity("sha256:" + service.ImageReference[^64..], "linux", "amd64", [service.ImageReference]));
        var sameSecret = false;
        var receiptPrecededDispatch = false;
        compose.OnManagedDeploy = (plan, environment) =>
        {
            receiptPrecededDispatch = File.ReadAllText(installReceipts).Contains("managed-web-db", StringComparison.Ordinal);
            Dictionary<string, string> Labels(string kind, string value) => new()
            {
                [ApplicationDeploymentValidation.ManagedLabel] = "true",
                [ApplicationDeploymentValidation.OwnerLabel] = ApplicationDeploymentValidation.OwnerValue,
                [ApplicationDeploymentValidation.ApplicationIdLabel] = plan.InstanceId.ToString("D"),
                [DockerStackValidation.ComposeProjectLabel] = plan.Project,
                ["com.docker.compose." + kind] = value,
            };
            sameSecret = environment["web"]["DB_PASSWORD"] == password && environment["db"]["POSTGRES_PASSWORD"] == password;
            var details = plan.Services.Select(service => new DockerContainerDetailsDto(
                plan.ContainerName(service.Id), plan.ContainerName(service.Id), service.ImageReference, "", "running", "", "", "", "",
                [service.Id == plan.WebService ? $"{service.ContainerPort}/tcp → 127.0.0.1:{port}" : $"{service.ContainerPort}/tcp → "],
                [], [plan.NetworkName], [],
                Labels("service", service.Id), engine.Images[service.ImageReference].Id)).ToArray();
            engine.Containers = details.ToDictionary(container => container.Name);
            engine.Network = new(plan.NetworkName, plan.NetworkName, "bridge", "local", [.. engine.Containers.Keys], Labels("network", "private"));
            engine.Volume = new(plan.VolumeName("db-data"), "local", "/fixture", Labels("volume", "db-data"), [plan.ContainerName("db")]);
            compose.Observed = [.. plan.Services.Select(service => new DockerStackServiceDto(service.Id, plan.ContainerName(service.Id),
                service.ImageReference, "running", "running"))];
        };
        http.DefaultRequestHeaders.Remove("Idempotency-Key");
        http.DefaultRequestHeaders.Add("Idempotency-Key", "managed-compose-install");
        http.DefaultRequestHeaders.Remove("X-Test-Role");
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using (var denied = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.CatalogInstall, request, RelaxKonOSJsonOptions.Default))
            Check(denied.StatusCode == HttpStatusCode.Forbidden && catalog.FindByName(request.Name) is null,
                "Managed Compose preview remains restricted to the controller.");
        http.DefaultRequestHeaders.Remove("X-Test-Role");
        http.DefaultRequestHeaders.Add("X-Test-Role", "controller");
        using var response = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.CatalogInstall, request, RelaxKonOSJsonOptions.Default);
        if (response.StatusCode != HttpStatusCode.Accepted)
            throw new InvalidOperationException($"Managed catalog install returned {(int)response.StatusCode}. {await response.Content.ReadAsStringAsync()}");
        var accepted = (await response.Content.ReadFromJsonAsync<CatalogApplicationInstallDto>(RelaxKonOSJsonOptions.Default))!;
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            while (coordinator.Get(accepted.Operation.OperationId)?.State is DeploymentOperationState.Queued or DeploymentOperationState.Running)
                await Task.Delay(20, budget.Token);
        var original = operations.Get(accepted.Operation.OperationId)!;
        var application = catalog.Find(accepted.Application.Id)!;
        Check(original.Operation.State == DeploymentOperationState.Succeeded && original.Compose?.ReceiptId is not null
            && application.CurrentRevisionId == original.Operation.TargetRevisionId && application.ContainerName?.EndsWith("-web-1") == true
            && catalog.FindRevision(application.CurrentRevisionId!.Value)?.ManagedImages is { Length: 2 }
            && sameSecret && receiptPrecededDispatch && application.Configuration is [{ IsSecret: true, SecretVersion: 1, Value: null }]
            && clients.LastClientName == ApplicationDeploymentRuntime.HealthClientName && accepted.Application.Configuration.All(field => field.Value is null),
            $"Activation must bind both verified images and the healthy Web service while retaining one protected secret. State={original.Operation.State}, recovery={original.Operation.RecoveryProblemCode}.");

        catalog.Update(application.Id, item => item with { CurrentRevisionId = null, ContainerName = null, ContainerId = null, DesiredState = ApplicationDesiredState.Stopped });
        operations.Update(original.Operation.OperationId, prior => prior with { State = DeploymentOperationState.Running, Stage = DeploymentStage.Activating, CompletedAt = null }, "test-before-activation");
        await coordinator.StartAsync(default);
        using var replay = await http.PostAsJsonAsync(ApplicationDeploymentApiRoutes.CatalogInstall, request, RelaxKonOSJsonOptions.Default);
        var repeated = (await replay.Content.ReadFromJsonAsync<CatalogApplicationInstallDto>(RelaxKonOSJsonOptions.Default))!;
        Check(repeated.Operation.OperationId == original.Operation.OperationId && compose.ManagedDeployCalls == 1
            && operations.Get(original.Operation.OperationId)?.Operation.State == DeploymentOperationState.Succeeded
            && catalog.Find(application.Id)?.CurrentRevisionId == application.CurrentRevisionId && !secrets.Has(application.Id, "password", 2),
            "Reconciliation must restore the original activation without another project or secret version.");
        Console.WriteLine($"Managed catalog install activated and recovered original app {original.Operation.OperationId} and Compose {original.Compose!.ReceiptId}.");
    }

    private static async Task VerifyManagedComposeRecoveryAsync(string root)
    {
        Directory.CreateDirectory(root);
        var environment = new TestHostEnvironment(root);
        var options = new ApplicationDeploymentOptions();
        var store = new ApplicationDeploymentOperationStore(environment, options);
        var lifetime = new TestApplicationLifetime();
        var compose = new FakeComposeService();
        var dockerRoot = Path.Combine(root, "docker");
        using var docker = DockerChecks.NewStackCoordinator(dockerRoot, compose, lifetime, 4);
        await docker.StartAsync(default);
        var dispatcher = new ApplicationManagedComposeDispatch(store, docker);
        var release = ApplicationCatalog.Require("fixture-web-db-plan", "1.0.0");
        const string actor = "compose-recovery-actor";
        const string revision = "credential-version-reference";
        const string secret = "synthetic-value-for-compose-recovery";
        var fingerprint = ApplicationDeploymentOperationStore.Reference("fixture-request");
        var inputs = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["web"] = new Dictionary<string, string> { ["DB_PASSWORD"] = secret },
            ["db"] = new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = secret },
        };

        (DeploymentOperationDto Operation, Guid RevisionId, ManagedComposePlan Plan, string Key) Prepare(string key, int port)
        {
            var applicationId = Guid.NewGuid();
            var entry = store.Create(applicationId, "compose-fixture", DeploymentOperationKind.Deploy, actor, key, fingerprint,
                ApplicationDeploymentService.Resources(applicationId), out _);
            var operation = store.Update(entry.Operation.OperationId, prior => prior with
            {
                State = DeploymentOperationState.Running,
                Stage = DeploymentStage.Creating,
                Cancellable = false,
            }, "test-dispatch-stage");
            return (operation, Guid.NewGuid(), release.CompileTopology(applicationId, port), key);
        }

        var normal = Prepare("normal", 18083);
        var receipt = await dispatcher.DispatchAsync(normal.Operation.OperationId, normal.RevisionId, normal.Plan, inputs, revision, default);
        Check(store.Get(normal.Operation.OperationId)?.Compose?.ReceiptId == receipt.OperationId,
            "Normal acceptance must persist the child ID.");

        var accepted = Prepare("accepted-before-association", 18084);
        var missing = Prepare("intent-without-receipt", 18085);
        foreach (var pending in new[] { accepted, missing })
        {
            var definition = pending.Plan.RenderDeployment(out _, inputs);
            Check(store.PrepareComposeDispatch(pending.Operation.OperationId, pending.Plan.InstanceId, pending.RevisionId,
                new(DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml),
                    DockerStackValidation.Reference(revision))), "The app intent must precede any Docker admission.");
        }
        // Persist only the child receipt to reproduce the durable boundary of a process exit.
        var lostResponse = await docker.DeployManagedAsync(accepted.Plan, inputs, accepted.Operation.OperationId, revision, default);
        Check(store.Get(accepted.Operation.OperationId)?.Compose?.ReceiptId is null, "The acceptance-response window must start unassociated.");
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (new[] { receipt.OperationId, lostResponse.OperationId }.Any(id =>
                docker.Get(id)?.State is DockerStackOperationState.Queued or DockerStackOperationState.Running))
                await Task.Delay(20, budget.Token);
        }
        await docker.StopAsync(default);
        var calls = compose.ManagedDeployCalls;
        using var reopenedDocker = DockerChecks.NewStackCoordinator(dockerRoot, compose, lifetime, 4);
        var reopened = new ApplicationDeploymentOperationStore(environment, options);
        var recovery = new ApplicationManagedComposeDispatch(reopened, reopenedDocker);
        var noActivation = new ApplicationDeploymentService(null!, null!, null!, null!, null!, options, environment,
            new(), null!, NullLogger<ApplicationDeploymentService>.Instance);
        var coordinator = new ApplicationDeploymentCoordinator(reopened, null!, noActivation, options, new(), lifetime,
            new CatalogUpdateEventPublisher(), NullLogger<ApplicationDeploymentCoordinator>.Instance, recovery);
        await coordinator.StartAsync(default);

        foreach (var pending in new[] { accepted, missing })
        {
            var operationId = pending.Operation.OperationId;
            var id = pending.Operation.ApplicationId;
            var same = reopened.FindIdempotent(id, DeploymentOperationKind.Deploy, actor, pending.Key, fingerprint);
            Check(same?.Operation.OperationId == operationId
                && same.Operation.RecoveryProblemCode == ApplicationManagedComposeDispatch.OutcomeUnknown
                && reopened.HasUnresolvedOutcome(id), "Unverified Compose intent must keep its original ID and mutation fence.");
            Expect(() => reopened.Create(id, "compose-fixture", DeploymentOperationKind.Deploy, actor, "conflicting-" + pending.Key, fingerprint,
                ApplicationDeploymentService.Resources(id), out _), ApplicationDeploymentProblemCodes.ResourceConflict);
        }
        Check(reopened.Get(accepted.Operation.OperationId)?.Compose?.ReceiptId == lostResponse.OperationId
            && reopened.Get(missing.Operation.OperationId)?.Compose?.ReceiptId is null,
            "Startup must associate an existing child by its original key and retain the missing case.");
        Check((await recovery.DispatchAsync(accepted.Operation.OperationId, accepted.RevisionId, accepted.Plan, inputs, revision, default)).OperationId
            == lostResponse.OperationId, "The handoff retry must return the original child.");
        try
        {
            await recovery.DispatchAsync(missing.Operation.OperationId, missing.RevisionId, missing.Plan, inputs, revision, default);
            throw new Exception("A missing receipt must block replay.");
        }
        catch (ApplicationDeploymentException error) when (error.ProblemCode == ApplicationManagedComposeDispatch.OutcomeUnknown) { }
        Check(compose.ManagedDeployCalls == calls && calls == 2
            && !File.ReadAllText(Path.Combine(root, options.RootDirectory, "operations.json")).Contains(secret, StringComparison.Ordinal),
            "Recovery must not dispatch a third child or persist the plaintext credential.");
        Console.WriteLine($"Managed Compose handoff preserved parent {accepted.Operation.OperationId} and child {lostResponse.OperationId}; missing receipt kept fenced.");
    }

    private static void VerifyTopologyManifest()
    {
        var release = ApplicationCatalog.Require("fixture-web-db-plan", "1.0.0");
        var web = release.Services.Single(service => service.Id == "web");
        var db = release.Services.Single(service => service.Id == "db");
        Check(!(release with { Services = [web, db with { DependsOn = ["web"] }] }).IsValid(),
            "A service dependency cycle cannot be admitted.");
        Check(!(release with { Services = [web with { DependsOn = ["external"] }, db] }).IsValid(),
            "An undeclared dependency cannot be admitted.");
        var password = release.Fields.Single();
        Check(!(release with { Fields = [password with { Bindings = [new("external", "PASSWORD")] }] }).IsValid(),
            "A package field cannot inject values into an undeclared service.");
        var compiled = release.CompileTopology(Guid.NewGuid(), 18083);
        Check(compiled.Services.Count == 2 && compiled.Services.Single(service => service.Id == "db").EnvironmentNames.SequenceEqual(["POSTGRES_PASSWORD"]),
            "Both service identities and the database secret target must be carried in the plan.");
        Expect(() => release.Bind(new(release.Id, release.Version, "not-deployed", 18083, [], true)), ApplicationDeploymentProblemCodes.NotSupported);
        Console.WriteLine("Managed catalog topology rejects cycles and undeclared injection targets before installation.");
    }

    private static void VerifyManifestDirectory(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var original = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "ApplicationCatalog", "personal-site.json"));
        var source = Path.Combine(directory, "original.json");
        var addition = Path.Combine(directory, "addition.json");
        File.WriteAllText(source, original);
        Check(ApplicationCatalog.Load(directory) is [{ Id: "personal-site", Version: "1.0.0" }], "The server must read packaged JSON.");
        File.WriteAllText(addition, original.Replace("\"1.0.0\"", "\"1.1.0\"", StringComparison.Ordinal));
        Check(ApplicationCatalog.Load(directory).Select(release => release.Version).ToHashSet().SetEquals(["1.0.0", "1.1.0"]),
            "The same reader must admit two exact versions from package files.");

        foreach (var invalid in new[]
        {
            original,
            original.Replace("\"3\"", "\"2\"", StringComparison.Ordinal),
            original.Replace("\"schemaVersion\": \"3\"", "\"schemaVersion\": \"3\", \"schemaVersion\": \"3\"", StringComparison.Ordinal),
            original.Replace("\"healthPath\"", "\"privileged\": true, \"healthPath\"", StringComparison.Ordinal),
            original.Replace("docker.io/library/nginx@sha256:", "nginx:latest", StringComparison.Ordinal),
        })
        {
            File.WriteAllText(addition, invalid);
            try { ApplicationCatalog.Load(directory); throw new Exception("Unsafe catalogue was admitted."); }
            catch (InvalidDataException) { }
        }
    }

    private static async Task VerifyEngineBoundariesAsync()
    {
        var engine = DispatchProxy.Create<IDockerEngineService, CatalogUpdateEngine>();
        var fixture = (CatalogUpdateEngine)(object)engine;
        var runtime = new ApplicationDeploymentRuntime(engine, new(), new FixtureHttpClientFactory([]), NullLogger<ApplicationDeploymentRuntime>.Instance);
        var reference = ApplicationCatalog.Require("personal-site", "1.0.0").ImageReference;
        fixture.Image = new("sha256:" + new string('a', 64), "linux", "amd64", [reference]);
        Check((await runtime.ResolveImageIdentityAsync(reference, "linux/amd64", default)).ImageId == fixture.Image.Id,
            "A pulled digest must bind the observed immutable Engine image ID.");
        fixture.Image = fixture.Image with { RepositoryDigests = [] };
        Check((await runtime.ResolveImageIdentityAsync(reference, "linux/amd64", default)).ImageId is null,
            "An image without the declared digest cannot activate.");
        fixture.Image = fixture.Image with { RepositoryDigests = [reference], Architecture = "arm64" };
        Check((await runtime.ResolveImageIdentityAsync(reference, "linux/amd64", default)).ImageId is null,
            "A mismatched image platform cannot activate.");

        fixture.Volume = new("legacy-data", "local", "/private",
            new Dictionary<string, string> { [ApplicationDeploymentValidation.OwnerLabel] = ApplicationDeploymentValidation.OwnerValue }, []);
        var reserved = await DockerApplicationResourcePolicy.DeleteVolumeAsync(engine, "relaxkonos-ca-project_db", true, default);
        var labelled = await DockerApplicationResourcePolicy.DeleteVolumeAsync(engine, fixture.Volume.Name, true, default);
        Check(reserved.ProblemCode == DockerApplicationResourcePolicy.ManagedResource && labelled.ProblemCode == reserved.ProblemCode
            && !fixture.Calls.Contains(nameof(IDockerEngineService.DeleteVolumeAsync)),
            "Docker Manager must block deletion of both reserved and labelled volumes before dispatch.");
        fixture.Volume = fixture.Volume with { Labels = new Dictionary<string, string>() };
        Check((await DockerApplicationResourcePolicy.DeleteVolumeAsync(engine, fixture.Volume.Name, true, default)).Success,
            "A user-managed volume continues through the existing Engine delete path.");

        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var name = ApplicationDeploymentValidation.VolumeName(owner, "data");
        fixture.Calls.Clear();
        fixture.Volume = new(name, "local", "/private",
            ApplicationDeploymentValidation.Labels(other, "other-instance", null, null, null, ApplicationDeploymentValidation.RoleVolume)
                .Select(label => label.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]), []);
        try
        {
            await runtime.RequireRetainedVolumesAsync(owner, [new("data", "/data", false)], default);
            throw new Exception("A matching volume name must not substitute for ownership.");
        }
        catch (ApplicationDeploymentException error)
        {
            Check(error.ProblemCode == ApplicationDeploymentProblemCodes.DriftUnownedResource
                && fixture.Calls.SequenceEqual([nameof(IDockerEngineService.GetVolumeAsync)]),
                "Another instance's volume cannot be selected by name or lead to a Docker mutation.");
        }

        var managedLabel = new Dictionary<string, string> { [DockerStackValidation.ApplicationOwnerLabel] = DockerStackValidation.ApplicationOwnerValue };
        var emptyLabels = new Dictionary<string, string>();
        fixture.Container = new("immutable-container-id", "external-name", "", "", "running", "", "", "", "",
            [], [], [], [], managedLabel);
        var changedId = "";
        Task<DockerOperationResult> Change(string id)
        {
            changedId = id;
            return Task.FromResult(new DockerOperationResult(true, ""));
        }
        Check((await DockerApplicationResourcePolicy.ChangeContainerAsync(engine, "alias", null, Change, default)).ProblemCode == reserved.ProblemCode
            && changedId == "", "A container alias cannot bypass its application owner label.");
        fixture.Container = fixture.Container with { Labels = emptyLabels };
        Check((await DockerApplicationResourcePolicy.ChangeContainerAsync(engine, "alias", "RELAXKONOS-CA-other", Change, default)).ProblemCode == reserved.ProblemCode
            && changedId == "", "A Manager rename cannot create a reserved application name.");
        Check((await DockerApplicationResourcePolicy.ChangeContainerAsync(engine, "alias", "ordinary", Change, default)).Success
            && changedId == fixture.Container.Id, "An ordinary container mutation uses its inspected immutable ID.");

        fixture.Network = new("immutable-network-id", "external-network", "bridge", "local", [], managedLabel);
        Check((await DockerApplicationResourcePolicy.DeleteNetworkAsync(engine, "network-alias", true, default)).ProblemCode == reserved.ProblemCode
            && !fixture.Calls.Contains(nameof(IDockerEngineService.DeleteNetworkAsync)),
            "A network alias cannot bypass its application owner label.");
        var create = new DockerContainerCreateRequest("ordinary", "nginx:1.0", []);
        Check((await DockerApplicationResourcePolicy.CreateContainerAsync(engine, create with { Network = "network-alias" }, default)).ProblemCode == reserved.ProblemCode,
            "A Manager container cannot join an application network by its ID or alias.");
        Check((await DockerApplicationResourcePolicy.CreateContainerAsync(engine, create with { Mounts = ["relaxkonos-ca-instance-data:/data"] }, default)).ProblemCode == reserved.ProblemCode
            && (await DockerApplicationResourcePolicy.CreateContainerAsync(engine, create with { Mounts = ["/var/lib/docker:/data"] }, default)).ProblemCode == "docker.validation_failed",
            "A Manager container cannot mount managed data by name or bind the host filesystem.");
        Check((await DockerApplicationResourcePolicy.CreateVolumeAsync(engine, new("ordinary", Labels: ["relaxkonos.owner=application-deployment"]), default)).ProblemCode == reserved.ProblemCode
            && !fixture.Calls.Contains(nameof(IDockerEngineService.CreateContainerAsync))
            && !fixture.Calls.Contains(nameof(IDockerEngineService.CreateVolumeAsync)), "Ownership labels cannot be supplied by Manager clients.");

        fixture.Network = fixture.Network with { Labels = emptyLabels };
        fixture.Volume = new("ordinary-data", "local", "/private", emptyLabels, []);
        Check((await DockerApplicationResourcePolicy.CreateContainerAsync(engine,
                create with { Network = "network-alias", Mounts = ["ordinary-data:/data"] }, default)).Success
            && fixture.CreatedContainer?.Network == fixture.Network.Id,
            "An ordinary container still accepts a named volume and an inspected user network.");
        Console.WriteLine("Docker Manager ownership checks passed for container mutations, mounts, networks, and volume labels.");
    }

    private static void Expect(Action action, string problem) {
        try { action(); throw new Exception("Expected " + problem); }
        catch (ApplicationDeploymentException error) { Check(error.ProblemCode == problem, "Unexpected problem code."); }
    }
    private static void Check(bool condition, string message) => TestAssert.Assert(condition, message);
}

public class CatalogUpdateEngine : DispatchProxy
{
    public DockerContainerDetailsDto? Container { get; set; }
    public DockerNetworkDetailsDto? Network { get; set; }
    public DockerContainerCreateRequest? CreatedContainer { get; set; }
    public DockerStatusDto? ReportedStatus { get; set; }
    public DockerImageIdentity? Image { get; set; }
    public Dictionary<string, DockerImageIdentity> Images { get; set; } = [];
    public Dictionary<string, DockerContainerDetailsDto> Containers { get; set; } = [];
    public bool LocalCompose { get; set; }
    public DockerVolumeDetailsDto? Volume { get; set; }
    public TaskCompletionSource<DockerStatusDto> Status { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Collections.Concurrent.ConcurrentBag<string> Calls { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        Calls.Add(method!.Name);
        if (method.Name == nameof(IDockerEngineService.ListContainersAsync)) return Task.FromResult<IReadOnlyList<DockerContainerDto>>([]);
        if (method.Name == nameof(IDockerEngineService.GetStatusAsync)) { Started.TrySetResult(); return ReportedStatus is { } status ? Task.FromResult(status) : Status.Task; }
        if (method.Name == nameof(IDockerEngineService.UsesLocalComposeEndpointAsync)) return Task.FromResult(LocalCompose);
        if (method.Name == nameof(IDockerEngineService.InspectImageAsync)) return Task.FromResult(Images.GetValueOrDefault((string)args![0]!) ?? Image);
        if (method.Name == nameof(IDockerEngineService.GetVolumeAsync)) return Task.FromResult(Volume);
        if (method.Name == nameof(IDockerEngineService.GetContainerAsync)) return Task.FromResult(Containers.GetValueOrDefault((string)args![0]!) ?? Container);
        if (method.Name == nameof(IDockerEngineService.GetNetworkAsync)) return Task.FromResult(Network);
        if (method.Name == nameof(IDockerEngineService.ListVolumesAsync)) return Task.FromResult<IReadOnlyList<DockerVolumeDto>>(
            Volume is { } volume ? [new(volume.Name, volume.Driver, volume.Mountpoint)] : []);
        if (method.Name == nameof(IDockerEngineService.CreateContainerAsync))
        {
            CreatedContainer = (DockerContainerCreateRequest)args![0]!;
            return Task.FromResult(new DockerOperationResult(true, ""));
        }
        if (method.Name == nameof(IDockerEngineService.DeleteVolumeAsync)) return Task.FromResult(new DockerOperationResult(true, ""));
        throw new NotSupportedException("Docker writes are outside this fixture.");
    }
}

internal sealed class CatalogUpdateEventPublisher : RelaxKonOS.Server.EventAlerts.IOperationalEventPublisher
{
    public Task PublishAsync(RelaxKonOS.Server.EventAlerts.OperationalEventSignal signal, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
