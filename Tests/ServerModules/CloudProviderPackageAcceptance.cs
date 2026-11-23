using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Amazon.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RelaxKonOS.CloudStorage.Transport;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.ServerModule.CloudS3;
using RelaxKonOS.ServerModule.CloudWebDav;

internal static class CloudProviderPackageAcceptance
{
    private const string Cloud = CloudProviderProtocol.CallerModuleId;
    private const string Actor = "b55180ec-91c4-467e-b838-773d51dbb68d";
    private const string ForeignActor = "044aee35-b657-4812-acd4-064243c8544f";
    private const string Source = "cloud-provider-acceptance";
    private const string Origin = "https://fixtures.invalid/";

    internal static async Task RunAsync(CloudFileProvider kind, string cloudPublish, string providerPublish, bool liveService = true)
    {
        var providerId = CloudProviderProtocol.ModuleId(kind);
        var providerRoute = CloudProviderProtocol.RequestPath(kind);
        var webDav = kind == CloudFileProvider.WebDav;
        var baidu = kind == CloudFileProvider.Baidu;
        var protocolName = baidu ? "Baidu Netdisk" : webDav ? "WebDAV" : "R2";
        var config = baidu
            ? new CloudConnectionRequest("Baidu package acceptance", CloudProviderProtocol.BaiduProtocol, "https://pan.baidu.com",
                null, null, null, liveService ? Required("BAIDU", "CLIENT_ID") : "fixture-key",
                liveService ? Required("BAIDU", "CLIENT_SECRET") : "fixture-secret", "pending-authorization", EnableWrites: true)
            : !liveService
            ? new CloudConnectionRequest("Local S3 package checks", "s3", "https://s3.invalid/", "auto",
                "fixture-bucket", "fixture-prefix", "fixture-key", "fixture-secret", EnableWrites: true)
            : webDav ? new CloudConnectionRequest("WebDAV package acceptance", "webdav",
            Required("WEBDAV", "ENDPOINT"), null, null, null, Required("WEBDAV", "USER"), Required("WEBDAV", "SECRET"),
            AllowPrivateWebDav: Environment.GetEnvironmentVariable("RELAXKONOS_WEBDAV_ALLOW_PRIVATE") == "1", EnableWrites: true)
            : R2Connection();
        var work = Directory.CreateTempSubdirectory("relaxkonos-cloud-provider-packages-");
        var root = work.FullName;
        var keyPath = Path.Combine(root, "acceptance-key.pem");
        var parent = baidu ? (Environment.GetEnvironmentVariable("RELAXKONOS_BAIDU_TEST_PARENT") ?? "").Trim('/') : "";
        if (!CloudTransport.ValidPath(parent)) throw new ArgumentException("Invalid dedicated acceptance parent.");
        if (parent.Length > 0) parent += "/";
        var remoteName = $"relaxkonos-provider-package-{Guid.NewGuid():N}";
        var remotePath = parent + remoteName + "/";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = deadline.Token;
        var remoteCreated = false;
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), ct);
            var faultFixture = Directory.CreateDirectory(Path.Combine(root, "unhealthy-fixture"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HostFixture.dll"), Path.Combine(faultFixture.FullName, "HostFixture.dll"));
            const string cloudAssembly = "RelaxKonOS.ServerModule.CloudStorage";
            var module = baidu ? "CloudBaidu" : webDav ? "CloudWebDav" : "CloudS3";
            var assembly = "RelaxKonOS.ServerModule." + module;
            var fixture = baidu ? "FixtureUnhealthyBaiduEntry" : webDav ? "FixtureUnhealthyWebDavEntry" : "FixtureUnhealthyS3Entry";
            var compiled = new[]
            {
                await BuildAsync(cloudPublish, Cloud, "1.0.0", cloudAssembly, cloudAssembly + ".CloudStorageModule"),
                await BuildAsync(providerPublish, providerId, "1.0.0", assembly, assembly + "." + module + "Module"),
                await BuildAsync(providerPublish, providerId, "1.1.0", assembly, assembly + "." + module + "Module"),
                await BuildAsync(faultFixture.FullName, providerId, "1.2.0", "HostFixture", "HostFixture." + fixture),
            };
            Check(compiled.Take(2).All(artifact => !ModulePackageVerifier.SupportsHostApi(
                artifact.Manifest with { HostApiMinimum = "2.1.0" }, "1.0.0")), "obsolete cloud module ABI cannot be installed");
            var now = DateTimeOffset.UtcNow;
            var catalog = new ModulePackageCatalog(1, Source, 1, now, now.AddHours(1), compiled, [], []);
            var signed = await ModulePackageBuilder.SignCatalogAsync(catalog, "acceptance", keyPath, ct);
            File.Delete(keyPath);
            const string prefix = "ModulePackages:Sources:" + Source + ":";
            var settings = new Dictionary<string, string?>
            {
                ["ModuleManagement:Authority"] = "managed",
                [prefix + "SchemaVersion"] = "1",
                [prefix + "SourceId"] = Source,
                [prefix + "Revision"] = "1",
                [prefix + "Enabled"] = "true",
                [prefix + "PublisherId"] = Source,
                [prefix + "Purpose"] = "official",
                [prefix + "ArtifactOrigin"] = Origin,
                [prefix + "MaximumValiditySeconds"] = "3600",
                [prefix + "AuthorizedModuleIds:0"] = Cloud,
                [prefix + "AuthorizedModuleIds:1"] = providerId,
                [prefix + "PublicKeysSpki:acceptance"] = signed.PublicKeySpki,
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            SeedDeployment(configuration, []);
            string serviceId;
            using (var identity = new ModuleManagementStore(configuration, root)) serviceId = identity.ServiceId;
            using (var store = new ModulePackageCatalogStore(root, serviceId))
            {
                var environment = new WebHostEnvironmentFixture(root);
                var policy = ModulePackageHostConfiguration.Source(configuration, Source);
                store.Accept(signed.Envelope, policy, ModulePackageHostConfiguration.Context(serviceId, configuration, environment),
                    ModulePackageCatalogStore.Read(root, serviceId).Revision, Actor, Source, DateTimeOffset.UtcNow);
            }

            Guid cloudInstall;
            Guid providerInstall;
            await using (var staging = await StartAsync(configuration))
            {
                cloudInstall = (await InstallAsync(staging.Client, compiled[0])).OperationId;
                providerInstall = (await InstallAsync(staging.Client, compiled[1])).OperationId;
            }
            Guid connection;
            var entries = CloudStorageApi.Route + "/connections";
            SeedDeployment(configuration, [Cloud]);
            await using (var cloudOnly = await StartAsync(configuration))
            {
                await RunningAsync(cloudOnly, Cloud, "1.0.0");
                connection = (await PostAsync<CloudConnectionDto>(cloudOnly.Client, entries, config)).Id;
                if (baidu)
                {
                    using var configured = await cloudOnly.Client.PutAsJsonAsync(CloudStorageApi.Route + "/authorization/configuration/"
                        + config.Protocol, new CloudAuthorizationConfigurationRequest(config.UserName, config.Secret), ct);
                    configured.EnsureSuccessStatusCode();
                }
                await VerifyOfflineAsync(cloudOnly.Client, connection);
            }
            SeedDeployment(configuration, [Cloud, providerId]);
            await using (var runtime = await StartAsync(configuration))
            {
                await RunningAsync(runtime, providerId, "1.0.0");
                await RunningAsync(runtime, Cloud, "1.0.0");
                await RequireReceiptAsync(runtime.Client, cloudInstall, "Succeeded", null);
                await RequireReceiptAsync(runtime.Client, providerInstall, "Succeeded", null);
                var siblingGeneration = Generation(runtime, Cloud);
                var coreGeneration = runtime.Store.ProcessGeneration;
                var route = $"{entries}/{connection:D}";
                try
                {
                    var payload = new byte[liveService ? 64 * 1024 + 3 : 0];
                    string? completedUpload = null;
                    if (liveService)
                    {
                        if (baidu) await AuthorizeBaiduAsync(runtime.Client, connection);
                        else if (!webDav)
                        {
                            var rootListing = await runtime.Client.GetFromJsonAsync<CloudDirectoryDto>(route + "/entries", ct);
                            Check(rootListing is { Entries.Count: 0 }, "R2 lists the dedicated connection prefix before writing");
                        }
                        remoteCreated = true;
                        await PostAsync<CloudCreatedFolder>(runtime.Client,
                            route + "/folders?reference=" + CloudTransport.Reference(parent) + "&name=" + remoteName, null);
                        RandomNumberGenerator.Fill(payload);
                        completedUpload = await StageUploadAsync(runtime.Client, route, payload, "provider-acceptance");
                        await CommitAsync(runtime.Client, completedUpload, payload, HttpStatusCode.OK);
                        if (!webDav && !baidu)
                        {
                            var conflict = new byte[] { 1, 2, 3 };
                            var duplicate = await StageUploadAsync(runtime.Client, route, conflict, "provider-duplicate");
                            await CommitAsync(runtime.Client, duplicate, conflict, HttpStatusCode.Conflict);
                            var multipart = new byte[16 * 1024 * 1024 + 3];
                            RandomNumberGenerator.Fill(multipart);
                            var multipartUpload = await StageUploadAsync(runtime.Client, route, multipart, "provider-multipart", "multipart.bin");
                            await CommitAsync(runtime.Client, multipartUpload, multipart, HttpStatusCode.OK);
                            Report("real R2 completed the 16777219-byte multipart publication");
                            await VerifyProviderAsync(runtime, route, multipart, "multipart.bin");
                        }
                    }
                    await VerifyProviderAsync(runtime, route, payload);
                    Report(liveService
                        ? $"signed initial install and real {protocolName} list/upload/download through the existing Explorer routes"
                        : "signed initial install and local Explorer upload-session creation through the existing routes");

                    using (var foreign = new HttpRequestMessage(HttpMethod.Get, route + "/entries"))
                    {
                        foreign.Headers.Add("test-subject", ForeignActor);
                        using var denied = await runtime.Client.SendAsync(foreign, ct);
                        Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign connection is hidden");
                    }
                    using (var readOnly = new HttpRequestMessage(HttpMethod.Post, route + "/folders?name=must-not-be-created"))
                    {
                        readOnly.Headers.Add("test-file-access", "read-only");
                        using var denied = await runtime.Client.SendAsync(readOnly, ct);
                        Check(denied.StatusCode == HttpStatusCode.Forbidden, "the Core file write policy applies before proxying");
                    }
                    using (var publicProvider = await runtime.Client.PostAsync(ServerModuleManagementRoutes.ModuleHosts + "/" + providerId
                        + providerRoute, null, ct))
                        Check(publicProvider.StatusCode == HttpStatusCode.NotFound, "provider does not expose a public credential-taking route");
                    var brokerRoute = new Uri(new Uri(runtime.Broker.Url!), providerRoute);
                    using (var directBroker = await runtime.Client.GetAsync(brokerRoute, ct))
                        Check(directBroker.StatusCode == HttpStatusCode.Forbidden, "private relay requires an authenticated ModuleHost");
                    using (var provider = runtime.Hosts.AcquireConnection(providerId)!)
                    using (var privateProbe = new HttpRequestMessage(HttpMethod.Get, brokerRoute))
                    {
                        privateProbe.Headers.Add(ModuleHostProtocol.ServiceTokenHeader, provider.ServiceToken);
                        using var denied = await runtime.Client.SendAsync(privateProbe, ct);
                        Check(denied.StatusCode == HttpStatusCode.Forbidden, "the provider's own ModuleHost token cannot invoke the relay");
                    }
                    configuration[prefix + "Purpose"] = "third-party";
                    try
                    {
                        using var denied = await runtime.Client.GetAsync(route + "/entries", ct);
                        Check(denied.StatusCode == HttpStatusCode.Forbidden, "a source policy mismatch cannot forward stored credentials");
                    }
                    finally { configuration[prefix + "Purpose"] = "official"; }

                    var update = await InstallAsync(runtime.Client, compiled[2]);
                    await RequireReceiptAsync(runtime.Client, update.OperationId, "Succeeded", null);
                    await RunningAsync(runtime, providerId, "1.1.0");
                    await RequireActiveInventoryAsync(runtime.Client, compiled[2]);
                    await VerifyProviderAsync(runtime, route, payload);
                    var broken = await InstallAsync(runtime.Client, compiled[3]);
                    await RequireReceiptAsync(runtime.Client, broken.OperationId, "Failed", "module-host-update-rolled-back");
                    await RunningAsync(runtime, providerId, "1.1.0");
                    await RequireActiveInventoryAsync(runtime.Client, compiled[2]);
                    await VerifyProviderAsync(runtime, route, payload);
                    Check(Generation(runtime, Cloud) == siblingGeneration && runtime.Store.ProcessGeneration == coreGeneration
                        && await runtime.Client.GetStringAsync("/acceptance-core", ct) == "ready", "Core and CloudStorage generations remain running");
                    Report(liveService
                        ? "online update and startup health-check rollback; the previous provider serves the same connection and bytes"
                        : "online update and startup health-check rollback; the previous provider serves the same connection");

                    var pending = await StageUploadAsync(runtime.Client, route, [4, 5, 6], "provider-pending");
                    if (liveService)
                    {
                        await DeleteRemoteAsync(runtime.Client, route, remotePath, parent, remoteName, ct);
                        remoteCreated = false;
                    }
                    var stop = await ManageAsync(runtime, ServerModuleAction.Stop, runtime.Store.DesiredIds);
                    while (stop.State is ServerModuleOperationState.Prepared or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying)
                    {
                        await Task.Delay(30, ct);
                        stop = (await runtime.Client.GetFromJsonAsync<ServerModuleOperationDto>(
                            ServerModuleManagementRoutes.Operation.Replace("{operationId:guid}", stop.OperationId.ToString("D")), ct))!;
                    }
                    Check(stop.State == ServerModuleOperationState.Succeeded, "provider stops under the standard module plan");
                    await VerifyOfflineAsync(runtime.Client, connection);
                    if (completedUpload is not null)
                    {
                        using var receipt = await runtime.Client.PostAsJsonAsync(completedUpload + "/commit",
                            new CommitUploadRequest(null), ct);
                        Check(receipt.StatusCode == HttpStatusCode.OK, "a completed upload remains verifiable when the provider is stopped");
                    }
                    using (var unavailable = await runtime.Client.PostAsJsonAsync(pending + "/commit",
                        new CommitUploadRequest(null), ct))
                        Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "provider stop leaves unpublished upload data staged");
                    var pendingSession = await runtime.Client.GetFromJsonAsync<UploadSessionDto>(pending, ct);
                    Check(pendingSession is { Offset: 3 }, "staged upload can still be inspected after provider stop");
                    Check(Generation(runtime, Cloud) == siblingGeneration, "stopping the provider does not restart CloudStorage");
                    var disable = await ManageAsync(runtime, ServerModuleAction.Disable, [Cloud]);
                    Check(disable.State == ServerModuleOperationState.AwaitingRestart, "provider disable follows the existing enable-set lifecycle");
                }
                finally
                {
                    if (baidu && remoteCreated)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        try
                        {
                            await DeleteRemoteAsync(runtime.Client, route, remotePath, parent, remoteName, cleanup.Token);
                            remoteCreated = false;
                        }
                        catch (Exception error) when (error is HttpRequestException or IOException or
                            InvalidOperationException or OperationCanceledException) { }
                    }
                }
            }
            await using (var runtime = await StartAsync(configuration))
            {
                await RunningAsync(runtime, Cloud, "1.0.0");
                var remove = await ManageAsync(runtime, ServerModuleAction.Remove, [Cloud]);
                Check(remove.State == ServerModuleOperationState.Succeeded
                    && !InstalledModuleStore.Read(root).Any(record => record.ModuleId == providerId), "provider uninstalls through the standard removal plan");
                await VerifyOfflineAsync(runtime.Client, connection);
                Report("provider uninstall retains the encrypted connection and leaves CloudStorage usable");
            }
        }
        finally
        {
            if (remoteCreated && baidu)
                Console.Error.WriteLine($"Baidu acceptance cleanup could not be confirmed for the test folder {remotePath}.");
            else if (remoteCreated)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var http = CloudTransport.Create(new Uri(config.Endpoint), config.AllowPrivateWebDav);
                var operation = new CloudProviderRequest(CloudProviderAction.Delete, config.Endpoint, config.UserName, config.Secret,
                    config.AllowPrivateWebDav, remotePath, Region: config.Region, Bucket: config.Bucket, Prefix: config.Prefix);
                try
                {
                    if (webDav) await new WebDavConnector(operation, http).DeleteAsync(remotePath, cleanup.Token);
                    else
                    {
                        using var s3 = new S3Connector(operation, http);
                        await s3.DeleteAsync(remotePath, cleanup.Token);
                    }
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
                catch (AmazonServiceException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
                catch (KeyNotFoundException) { }
                catch { Console.Error.WriteLine("Cloud provider fixture cleanup could not be confirmed."); }
            }
            work.Delete(recursive: true);
        }

        Task<ModulePackageRelease> BuildAsync(string publish, string module, string version, string assembly, string entry)
        {
            var release = module + "-" + version + "-acceptance";
            return ModulePackageBuilder.BuildArchiveAsync(new(Path.GetFullPath(publish), Path.Combine(root, release),
                module, version, release, Source, "official", ModulePackagePlatform.CurrentRid,
                assembly + ".dll", entry, Source, new string('a', 40), Source, 1,
                Origin, PrivateKeyPemPath: keyPath, KeyId: "acceptance", ValiditySeconds: 3600,
                HostApiMinimum: module == Cloud || baidu ? CloudProviderProtocol.BaiduHostApiMinimum : CloudProviderProtocol.HostApiMinimum,
                HostApiMaximumExclusive: "3.0.0", Runtime: "module-host",
                Ui: new(assembly, "", "", null, "MIT")), ct);
        }

        void SeedDeployment(IConfiguration configuration, IReadOnlyCollection<string> enabled)
        {
            var installed = InstalledModuleCatalog.Load(root);
            var selection = ServerModules.Create(new ConfigurationBuilder().Build(), installed).KnownModuleIds
                .ToDictionary(id => "Modules:" + ServerModules.ConfigurationName(id) + ":Enabled",
                    id => (string?)enabled.Contains(id).ToString(), StringComparer.Ordinal);
            selection["ModuleManagement:Authority"] = "deployment";
            using var store = new ModuleManagementStore(new ConfigurationBuilder().AddConfiguration(configuration)
                .AddInMemoryCollection(selection).Build(), root, installed);
        }

        async Task<Runtime> StartAsync(IConfiguration configuration)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = root, EnvironmentName = Environments.Development, Args = [],
            });
            builder.Logging.ClearProviders();
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddConfiguration(configuration);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var installed = InstalledModuleCatalog.Load(root);
            var store = new ModuleManagementStore(builder.Configuration, root, installed);
            var modules = ServerModules.CreateManaged(store.DesiredIds, installed);
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
            modules.ConfigureServices(builder, context);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton(modules);
            builder.Services.AddSingleton(context);
            builder.Services.AddSingleton<CoreModulePrivilegeBroker>();
            builder.Services.AddHostedService(services => services.GetRequiredService<CoreModulePrivilegeBroker>());
            builder.Services.AddSingleton(services => new ModuleHostSupervisor(builder.Environment, builder.Configuration,
                NullLogger<ModuleHostSupervisor>.Instance, services.GetRequiredService<IResidentModuleWorkLeaseSource>(), modules,
                (package, id, data, record) => new ModuleHostProcessClient(package, id, data,
                    brokerUrl: services.GetRequiredService<CoreModulePrivilegeBroker>().Url, installed: record)));
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleHostSupervisor>());
            builder.Services.AddSingleton<ModuleManagementExecutor>();
            builder.Services.AddHostedService(services => services.GetRequiredService<ModuleManagementExecutor>());
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(ServerModuleManagementEndpoints.ManagePolicy, policy =>
                    policy.RequireAssertion(user => ServerModuleManagementEndpoints.CanManage(user.User)));
                options.AddPolicy(FileAuthorizationPolicies.Read, policy => policy.RequireAuthenticatedUser());
                options.AddPolicy(FileAuthorizationPolicies.Write, policy => policy.RequireClaim("acceptance-file-write", "allowed"));
            });
            var app = builder.Build();
            try
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/acceptance-core", () => "ready");
                modules.MapEndpoints(app, context);
                app.MapServerModuleManagementEndpoints();
                await app.StartAsync(ct);
                store.VerifyStartup(modules.EnabledIds);
                ModulePackageOperationStore.VerifyStartup(root, InstalledModuleStore.Read(root), store.ProcessGeneration, DateTimeOffset.UtcNow);
                return new(app, store, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
            }
            catch { await app.DisposeAsync(); store.Dispose(); throw; }
        }

        async Task<ServerModulePackageInstallResultDto> InstallAsync(HttpClient client, ModulePackageRelease artifact)
        {
            var path = Path.Combine(root, artifact.Manifest.ReleaseId, new Uri(artifact.DownloadUri).Segments[^1]);
            await using var file = File.OpenRead(path);
            using var upload = await client.PostAsync(ServerModuleManagementRoutes.PackageUpload, new StreamContent(file), ct);
            var body = await ReadAsync<ServerModulePackageUploadDto>(upload, ct);
            var plan = await PostAsync<ServerModulePackagePlanDto>(client, ServerModuleManagementRoutes.PackagePlans,
                new ServerModulePackageInstallRequest(Source, artifact.Manifest.ReleaseId, body.UploadId));
            return await PostAsync<ServerModulePackageInstallResultDto>(client,
                ServerModuleManagementRoutes.PackagePlanApply.Replace("{planId:guid}", plan.PlanId.ToString("D")), null);
        }

        async Task<ServerModuleOperationDto> ManageAsync(Runtime runtime, ServerModuleAction action, IEnumerable<string> enabled)
        {
            var plan = await PostAsync<ServerModulePlanDto>(runtime.Client, ServerModuleManagementRoutes.Plans,
                new ServerModulePlanRequest(runtime.Store.ServiceId, action, [providerId], enabled.ToArray(), runtime.Store.Revision,
                    Guid.NewGuid().ToString("N")));
            Check(plan.BlockedBy.Count == 0, $"module plan {action}: {string.Join(",", plan.BlockedBy.Select(item => item.ProblemCode))}");
            return await PostAsync<ServerModuleOperationDto>(runtime.Client,
                ServerModuleManagementRoutes.Apply.Replace("{planId:guid}", plan.PlanId.ToString("D")), null);
        }

        async Task<T> PostAsync<T>(HttpClient client, string route, object? body)
        {
            using var response = body is null ? await client.PostAsync(route, null, ct) : await client.PostAsJsonAsync(route, body, ct);
            return await ReadAsync<T>(response, ct);
        }

        async Task AuthorizeBaiduAsync(HttpClient client, Guid id)
        {
            var sessions = CloudStorageApi.Route + "/authorization/sessions";
            var session = await PostAsync<CloudAuthorizationSessionDto>(client, sessions,
                new CloudAuthorizationStartRequest(config.Protocol, config.Name, EnableWrites: true, ConnectionId: id));
            Check(session is { Status: "pending", UserCode.Length: > 0, AuthorizationUrl.Length: > 0 },
                $"Baidu device session did not become pending ({session.Status}).");
            Console.WriteLine($"BAIDU_APPROVAL {session.AuthorizationUrl} {session.UserCode}");
            var route = $"{sessions}/{session.Id:D}";
            while (session.Status == "pending")
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, session.PollIntervalSeconds)), ct);
                session = (await client.GetFromJsonAsync<CloudAuthorizationSessionDto>(route, ct))!;
            }
            Check(session.Status == "completed" && session.ConnectionId == id
                && session.UserCode is null && session.AuthorizationUrl is null, $"Baidu consent did not complete ({session.Status}).");
            Report("real OAuth device approval refreshed the existing connection without exposing token or device code");
        }

        async Task RunningAsync(Runtime runtime, string module, string version)
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromSeconds(25));
            while (true)
            {
                var status = runtime.Hosts.Snapshot().SingleOrDefault(item => item.ModuleId == module);
                if (status is { Status: "running" } && status.Version == version) return;
                if (status is { Status: "faulted" }) throw new InvalidOperationException($"{module} failed to start: {status.ProblemCode}");
                await Task.Delay(50, ready.Token);
            }
        }

        async Task RequireActiveInventoryAsync(HttpClient client, ModulePackageRelease release)
        {
            var detail = await client.GetFromJsonAsync<ServerModuleDto>(ServerModuleManagementRoutes.Detail
                .Replace("{moduleId}", release.Manifest.ModuleId), RelaxKonOSJsonOptions.Default, ct);
            var health = await client.GetFromJsonAsync<ServerModuleHealthPageDto>(ServerModuleManagementRoutes.ModuleHealth,
                RelaxKonOSJsonOptions.Default, ct);
            Check(detail is { Packaging: "independent", EffectiveEnabled: true, RestartRequired: false }
                && detail.InstalledVersion == release.Manifest.Version && detail.EffectiveVersion == release.Manifest.Version
                && detail.ArtifactDigest == release.ArchiveSha256
                && health?.Modules.Single(module => module.ModuleId == release.Manifest.ModuleId) is { Status: "healthy" } running
                && running.Version == release.Manifest.Version,
                "module detail and health agree on the active signed release after live update or rollback");
        }

        async Task RequireReceiptAsync(HttpClient client, Guid id, string state, string? problem)
        {
            var history = await client.GetFromJsonAsync<ServerPackageOperationPageDto>(ServerModuleManagementRoutes.PackageOperations, ct);
            var receipt = history!.Items.Single(item => item.OperationId == id);
            Check(receipt.State == state && receipt.ProblemCode == problem, $"package receipt: {receipt.State}, {receipt.ProblemCode}");
        }

        async Task<string> StageUploadAsync(HttpClient client, string route, byte[] payload, string key, string fileName = "round trip.txt")
        {
            var upload = await PostAsync<UploadSessionDto>(client, $"{route}/uploads?key={key}",
                new CreateUploadRequest(CloudTransport.Reference(remotePath), fileName, payload.Length));
            var target = $"{route}/uploads/{upload.UploadId}";
            for (var offset = 0; offset < payload.Length; offset += FileUploadProtocol.DefaultChunkSize)
            {
                var length = Math.Min(FileUploadProtocol.DefaultChunkSize, payload.Length - offset);
                using var patch = await client.PatchAsync($"{target}?offset={offset}&length={length}",
                    new ByteArrayContent(payload, offset, length), ct);
                patch.EnsureSuccessStatusCode();
            }
            return target;
        }

        async Task CommitAsync(HttpClient client, string target, byte[] payload, HttpStatusCode expected)
        {
            var hash = new CommitUploadRequest(Convert.ToHexString(SHA256.HashData(payload)));
            while (true)
            {
                using var response = await client.PostAsJsonAsync(target + "/commit", hash, ct);
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    Check(response.StatusCode == expected,
                        $"publication returned {(int)response.StatusCode}, expected {(int)expected}");
                    return;
                }
                await Task.Delay(100, ct);
            }
        }

        async Task VerifyProviderAsync(Runtime runtime, string route, byte[] bytes, string fileName = "round trip.txt")
        {
            var client = runtime.Client;
            if (!liveService)
            {
                await PostAsync<UploadSessionDto>(client, $"{route}/uploads?key=health-{Guid.NewGuid():N}",
                    new CreateUploadRequest(CloudTransport.Reference(remotePath), "probe.txt", 0));
                return;
            }
            var listing = await client.GetFromJsonAsync<CloudDirectoryDto>(route + "/entries?reference=" + CloudTransport.Reference(remotePath), ct);
            var file = listing!.Entries.Single(item => item.Name == fileName);
            using var response = await client.GetAsync(route + "/download?reference=" + file.Reference, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (baidu)
                Check(response.Content.Headers.ContentDisposition?.FileNameStar == fileName, "Baidu keeps the real download filename");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            var offset = 0;
            const int milestone = 4 * 1024 * 1024;
            var nextMilestone = milestone;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            {
                Check(offset + read <= bytes.Length && bytes.AsSpan(offset, read).SequenceEqual(buffer.AsSpan(0, read)),
                    $"{protocolName} download preserves the data");
                offset += read;
                if (offset >= nextMilestone)
                {
                    Console.WriteLine($"Verified {offset} of {bytes.Length} download bytes.");
                    nextMilestone += milestone;
                }
            }
            Check(offset == bytes.Length, $"{protocolName} download returned the full file");
        }

        async Task VerifyOfflineAsync(HttpClient client, Guid id)
        {
            var all = await client.GetFromJsonAsync<CloudConnectionDto[]>(CloudStorageApi.Route + "/connections", ct);
            Check(all!.Any(item => item.Id == id && item.Protocol == config.Protocol), "existing connection keeps its ID when the provider is unavailable");
            using var unavailable = await client.GetAsync($"{CloudStorageApi.Route}/connections/{id:D}/entries", ct);
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "missing provider gives an explicit unavailable response");
            if (baidu)
            {
                var authorization = CloudStorageApi.Route + "/authorization";
                var configured = await client.GetFromJsonAsync<CloudAuthorizationProviderDto[]>(authorization + "/providers", ct);
                Check(configured!.Any(item => item.Protocol == config.Protocol && item.Configured), "the OAuth application configuration is retained");
                using var noConsent = await client.PostAsJsonAsync(authorization + "/sessions",
                    new CloudAuthorizationStartRequest(config.Protocol, config.Name), ct);
                Check(noConsent.StatusCode == HttpStatusCode.ServiceUnavailable, "device authorization requires the provider");
            }
        }
    }

    private static async Task DeleteRemoteAsync(HttpClient client, string route, string directory, string parent, string name, CancellationToken ct)
    {
        using var response = await client.DeleteAsync(route + "/entries?reference=" + CloudTransport.Reference(directory), ct);
        response.EnsureSuccessStatusCode();
        string? cursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<CloudDirectoryDto>(route + "/entries?reference=" + CloudTransport.Reference(parent)
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), ct);
            Check(page!.Entries.All(entry => entry.Name != name), "the isolated remote acceptance directory was removed");
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                ? await response.Content.ReadFromJsonAsync<RelaxKonOS.Protocol.Common.ProblemDetails>(cancellationToken: ct) : null;
            throw new InvalidOperationException($"Acceptance HTTP {(int)response.StatusCode} for {response.RequestMessage?.RequestUri?.AbsolutePath}: {problem?.Title}");
        }
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static Guid Generation(Runtime runtime, string module)
    {
        using var connection = runtime.Hosts.AcquireConnection(module);
        return connection?.GenerationId ?? throw new InvalidOperationException($"{module} is offline.");
    }

    private static void Report(string message) => Console.WriteLine("PASS: " + message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Required(string provider, string name) =>
        Environment.GetEnvironmentVariable($"RELAXKONOS_{provider}_{name}") is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing RELAXKONOS_{provider}_{name}.");

    private static CloudConnectionRequest R2Connection()
    {
        var endpoint = Required("R2", "ENDPOINT");
        var prefix = Required("R2", "TEST_PREFIX").Trim('/');
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Port != 443 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.IdnHost.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase)
            || prefix.Length == 0 || !CloudTransport.ValidPath(prefix))
            throw new InvalidOperationException("Expected a dedicated HTTPS R2 endpoint and test prefix.");
        return new("R2 package acceptance", "s3", uri.AbsoluteUri, "auto", Required("R2", "BUCKET"), prefix,
            Required("R2", "KEY_ID"), Required("R2", "SECRET"), EnableWrites: true);
    }

    private sealed class Runtime(WebApplication app, ModuleManagementStore store, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public ModuleManagementStore Store => store;
        public CoreModulePrivilegeBroker Broker => app.Services.GetRequiredService<CoreModulePrivilegeBroker>();
        public ModuleHostSupervisor Hosts => app.Services.GetRequiredService<ModuleHostSupervisor>();
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); store.Dispose(); }
        }
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["test-subject"] == ForeignActor ? ForeignActor : Actor;
            var identity = new ClaimsIdentity([new Claim("sub", subject), new Claim("sid", subject), new Claim("jti", subject),
                new Claim(ClaimTypes.Role, "controller")], Scheme.Name);
            if (Request.Headers["test-file-access"] != "read-only")
                identity.AddClaim(new Claim("acceptance-file-write", "allowed"));
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class WebHostEnvironmentFixture(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CloudProviderPackageAcceptance";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
