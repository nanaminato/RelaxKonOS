using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
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
using RelaxKonOS.ServerModule.CloudWebDav;

internal static class CloudWebDavPackageAcceptance
{
    private const string Provider = CloudWebDavProtocol.ProviderModuleId;
    private const string Cloud = CloudWebDavProtocol.CallerModuleId;
    private const string Actor = "b55180ec-91c4-467e-b838-773d51dbb68d";
    private const string ForeignActor = "044aee35-b657-4812-acd4-064243c8544f";
    private const string Source = "webdav-acceptance";
    private const string Origin = "https://fixtures.invalid/";

    internal static async Task RunAsync(string cloudPublish, string davPublish)
    {
        var work = Directory.CreateTempSubdirectory("relaxkonos-webdav-packages-");
        var root = work.FullName;
        var keyPath = Path.Combine(root, "acceptance-key.pem");
        var remotePath = $"relaxkonos-webdav-package-{Guid.NewGuid():N}/";
        var config = new CloudConnectionRequest("WebDAV package acceptance", "webdav",
            Required("ENDPOINT"), null, null, null, Required("USER"), Required("SECRET"),
            AllowPrivateWebDav: Environment.GetEnvironmentVariable("RELAXKONOS_WEBDAV_ALLOW_PRIVATE") == "1", EnableWrites: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = deadline.Token;
        var remoteCreated = false;
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), ct);
            var faultFixture = Directory.CreateDirectory(Path.Combine(root, "unhealthy-fixture"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HostFixture.dll"), Path.Combine(faultFixture.FullName, "HostFixture.dll"));
            const string cloudAssembly = "RelaxKonOS.ServerModule.CloudStorage";
            const string davAssembly = "RelaxKonOS.ServerModule.CloudWebDav";
            var compiled = new[]
            {
                await BuildAsync(cloudPublish, Cloud, "1.0.0", cloudAssembly, cloudAssembly + ".CloudStorageModule"),
                await BuildAsync(davPublish, Provider, "1.0.0", davAssembly, davAssembly + ".CloudWebDavModule"),
                await BuildAsync(davPublish, Provider, "1.1.0", davAssembly, davAssembly + ".CloudWebDavModule"),
                await BuildAsync(faultFixture.FullName, Provider, "1.2.0", "HostFixture", "HostFixture.FixtureUnhealthyWebDavEntry"),
            };
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
                [prefix + "AuthorizedModuleIds:1"] = Provider,
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
            SeedDeployment(configuration, [Cloud, Provider]);
            Guid connection;
            await using (var runtime = await StartAsync(configuration))
            {
                await RunningAsync(runtime, Provider, "1.0.0");
                await RunningAsync(runtime, Cloud, "1.0.0");
                await RequireReceiptAsync(runtime.Client, cloudInstall, "Succeeded", null);
                await RequireReceiptAsync(runtime.Client, providerInstall, "Succeeded", null);
                var siblingGeneration = Generation(runtime, Cloud);
                var coreGeneration = runtime.Store.ProcessGeneration;
                var entries = CloudStorageApi.Route + "/connections";
                connection = (await PostAsync<CloudConnectionDto>(runtime.Client, entries, config)).Id;
                var route = $"{entries}/{connection:D}";
                await PostAsync<CloudCreatedFolder>(runtime.Client, route + "/folders?name=" + remotePath.TrimEnd('/'), null);
                remoteCreated = true;
                var payload = new byte[64 * 1024 + 3];
                RandomNumberGenerator.Fill(payload);
                var completedUpload = await UploadAsync(runtime.Client, route, remotePath, payload);
                await VerifyDataAsync(runtime.Client, route, remotePath, payload);
                Report("signed initial install and WebDAV list/upload/download through the Core proxy and two ModuleHosts");

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
                using (var publicProvider = await runtime.Client.PostAsync(ServerModuleManagementRoutes.ModuleHosts + "/" + Provider
                    + CloudWebDavProtocol.RequestPath, null, ct))
                    Check(publicProvider.StatusCode == HttpStatusCode.NotFound, "provider does not expose a public credential-taking route");
                var brokerRoute = new Uri(new Uri(runtime.Broker.Url!), CloudWebDavProtocol.RequestPath);
                using (var directBroker = await runtime.Client.GetAsync(brokerRoute, ct))
                    Check(directBroker.StatusCode == HttpStatusCode.Forbidden, "private relay requires an authenticated ModuleHost");
                using (var provider = runtime.Hosts.AcquireConnection(Provider)!)
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
                await RunningAsync(runtime, Provider, "1.1.0");
                await VerifyDataAsync(runtime.Client, route, remotePath, payload);
                var broken = await InstallAsync(runtime.Client, compiled[3]);
                await RequireReceiptAsync(runtime.Client, broken.OperationId, "Failed", "module-host-update-rolled-back");
                await RunningAsync(runtime, Provider, "1.1.0");
                await VerifyDataAsync(runtime.Client, route, remotePath, payload);
                Check(Generation(runtime, Cloud) == siblingGeneration && runtime.Store.ProcessGeneration == coreGeneration
                    && await runtime.Client.GetStringAsync("/acceptance-core", ct) == "ready", "Core and CloudStorage generations remain running");
                Report("online update and startup health-check rollback; the previous provider serves the same connection and bytes");

                await DeleteRemoteAsync(runtime.Client, route, remotePath, ct);
                remoteCreated = false;
                var stop = await ManageAsync(runtime, ServerModuleAction.Stop, runtime.Store.DesiredIds);
                while (stop.State is ServerModuleOperationState.Prepared or ServerModuleOperationState.Applying or ServerModuleOperationState.Verifying)
                {
                    await Task.Delay(30, ct);
                    stop = (await runtime.Client.GetFromJsonAsync<ServerModuleOperationDto>(
                        ServerModuleManagementRoutes.Operation.Replace("{operationId:guid}", stop.OperationId.ToString("D")), ct))!;
                }
                Check(stop.State == ServerModuleOperationState.Succeeded, "provider stops under the standard module plan");
                await VerifyOfflineAsync(runtime.Client, connection);
                using (var receipt = await runtime.Client.PostAsJsonAsync(completedUpload + "/commit",
                    new CommitUploadRequest(null), ct))
                    Check(receipt.StatusCode == HttpStatusCode.OK, "a completed upload remains verifiable when the provider is stopped");
                Check(Generation(runtime, Cloud) == siblingGeneration, "stopping the provider does not restart CloudStorage");
                var disable = await ManageAsync(runtime, ServerModuleAction.Disable, [Cloud]);
                Check(disable.State == ServerModuleOperationState.AwaitingRestart, "provider disable follows the existing enable-set lifecycle");
            }
            await using (var runtime = await StartAsync(configuration))
            {
                await RunningAsync(runtime, Cloud, "1.0.0");
                var remove = await ManageAsync(runtime, ServerModuleAction.Remove, [Cloud]);
                Check(remove.State == ServerModuleOperationState.Succeeded
                    && !InstalledModuleStore.Read(root).Any(record => record.ModuleId == Provider), "provider uninstalls through the standard removal plan");
                await VerifyOfflineAsync(runtime.Client, connection);
                Report("provider uninstall retains the encrypted connection and leaves CloudStorage usable");
            }
        }
        finally
        {
            if (remoteCreated)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var http = CloudTransport.Create(new Uri(config.Endpoint), config.AllowPrivateWebDav);
                var dav = new WebDavConnector(new(CloudWebDavAction.Delete, config.Endpoint, config.UserName, config.Secret,
                    config.AllowPrivateWebDav, remotePath), http);
                try { await dav.DeleteAsync(remotePath, cleanup.Token); }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
                catch { Console.Error.WriteLine("WebDAV fixture cleanup could not be confirmed."); }
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
                HostApiMinimum: ModuleHostProtocol.HostApiVersion, HostApiMaximumExclusive: "3.0.0", Runtime: "module-host",
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
                new ServerModulePlanRequest(runtime.Store.ServiceId, action, [Provider], enabled.ToArray(), runtime.Store.Revision,
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

        async Task RequireReceiptAsync(HttpClient client, Guid id, string state, string? problem)
        {
            var history = await client.GetFromJsonAsync<ServerPackageOperationPageDto>(ServerModuleManagementRoutes.PackageOperations, ct);
            var receipt = history!.Items.Single(item => item.OperationId == id);
            Check(receipt.State == state && receipt.ProblemCode == problem, $"package receipt: {receipt.State}, {receipt.ProblemCode}");
        }

        async Task<string> UploadAsync(HttpClient client, string route, string directory, byte[] payload)
        {
            var upload = await PostAsync<UploadSessionDto>(client, $"{route}/uploads?key=provider-acceptance",
                new CreateUploadRequest(CloudTransport.Reference(directory), "round trip.txt", payload.Length));
            var target = $"{route}/uploads/{upload.UploadId}";
            using (var patch = await client.PatchAsync($"{target}?offset=0&length={payload.Length}", new ByteArrayContent(payload), ct))
                patch.EnsureSuccessStatusCode();
            while (true)
            {
                using var response = await client.PostAsJsonAsync(target + "/commit",
                    new CommitUploadRequest(Convert.ToHexString(SHA256.HashData(payload))), ct);
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    response.EnsureSuccessStatusCode();
                    return target;
                }
                await Task.Delay(35, ct);
            }
        }

        async Task VerifyDataAsync(HttpClient client, string route, string directory, byte[] bytes)
        {
            var listing = await client.GetFromJsonAsync<CloudDirectoryDto>(route + "/entries?reference=" + CloudTransport.Reference(directory), ct);
            var file = listing!.Entries.Single(item => item.Name == "round trip.txt");
            var output = await client.GetByteArrayAsync(route + "/download?reference=" + file.Reference, ct);
            Check(bytes.AsSpan().SequenceEqual(output), "WebDAV list and byte-for-byte download");
        }

        async Task VerifyOfflineAsync(HttpClient client, Guid id)
        {
            var all = await client.GetFromJsonAsync<CloudConnectionDto[]>(CloudStorageApi.Route + "/connections", ct);
            Check(all!.Any(item => item.Id == id && item.Protocol == "webdav"), "existing connection keeps its ID when the provider is unavailable");
            using var unavailable = await client.GetAsync($"{CloudStorageApi.Route}/connections/{id:D}/entries", ct);
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "missing provider gives an explicit unavailable response");
        }
    }

    private static async Task DeleteRemoteAsync(HttpClient client, string route, string directory, CancellationToken ct)
    {
        using var response = await client.DeleteAsync(route + "/entries?reference=" + CloudTransport.Reference(directory), ct);
        response.EnsureSuccessStatusCode();
        using var check = await client.GetAsync(route + "/info?reference=" + CloudTransport.Reference(directory), ct);
        Check(check.StatusCode == HttpStatusCode.NotFound, "the isolated remote acceptance directory was removed");
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

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable("RELAXKONOS_WEBDAV_" + name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing RELAXKONOS_WEBDAV_{name}.");

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
        public string ApplicationName { get; set; } = "CloudWebDavAcceptance";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
