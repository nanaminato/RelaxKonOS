using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Server.Endpoints;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.WebsitePublishing;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

internal static class NginxProcessRecoveryChecks
{
    private const string PackageExecutable = "/usr/sbin/nginx";
    private const string Key = "reload-crash";
    private const string Actor = "nginx-live-fixture";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsLinux() || executable != PackageExecutable || !File.Exists(executable))
            throw new ArgumentException("Expected an installed Linux Nginx executable.");

        var directory = Directory.CreateTempSubdirectory("relaxkonos-nginx-crash-");
        try
        {
            var root = directory.FullName;
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            await File.WriteAllTextAsync(NginxManagedPaths.MarkerPath(new() { InstallationRoot = root }), NginxManagedPaths.MarkerContent);
            using var certificate = CreateCertificate(root);
            WriteConfig(root, port, "before");
            using var nginx = StartNginx(executable, root);
            try
            {
                using var client = CreatePinnedClient(certificate);
                var address = new Uri($"https://127.0.0.1:{port}/");
                await ObserveAsync(client, address, "before", nginx);
                WriteConfig(root, port, "after");

                Guid operationId;
                using (var worker = DockerProcessRecoveryChecks.StartWorker("--nginx-recovery-worker", executable, root))
                {
                    try
                    {
                        var response = await worker.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                        TestAssert.Assert(Guid.TryParse(response, out operationId), $"Missing Nginx reload receipt: {response}");
                        await ObserveAsync(client, address, "after", nginx);
                        using var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "webserver-operations.json")));
                        var running = ledger.RootElement.EnumerateArray().Single();
                        var reloads = CountReloads(root);
                        TestAssert.Assert(!worker.HasExited && reloads == 1
                            && running.GetProperty("operationId").GetGuid() == operationId
                            && running.GetProperty("state").GetString() == "running",
                            $"The real reload and original Running receipt must precede the crash. Reloads={reloads}; receipt={running}.");
                        worker.Kill();
                        await worker.WaitForExitAsync().WaitAsync(Budget);
                        TestAssert.Assert(worker.ExitCode != 0, "The original Nginx operation process must exit abnormally.");
                    }
                    finally
                    {
                        await DockerProcessRecoveryChecks.StopWorkerAsync(worker);
                    }
                }

                using var store = await OpenStoreAsync(root);
                var restored = await store.GetAsync(operationId, default);
                var transport = new IsolatedReloadTransport(executable, root, allowReload: false);
                var recoveryScopes = 0;
                var provider = CreateProvider(root, store, transport, executionCreated: () => Interlocked.Increment(ref recoveryScopes));
                File.Delete(NginxManagedPaths.MarkerPath(new() { InstallationRoot = root }));
                TestAssert.Assert(restored is not null, "Original operation was absent after startup recovery.");
                TestAssert.Assert(!(await provider.DiscoverAsync(default)).Any(instance => instance.Id == restored!.InstanceId),
                    "The original instance must be absent from discovery when retrying.");
                var manager = new WebServerManager([provider]);
                var replay = await manager.ApplyLifecycleAsync(restored!.InstanceId, WebServerLifecycleAction.Reload, Key, Actor, default);
                var reloadReplay = await ReplayOverHttpAsync<WebServerOperationDto>(services =>
                {
                    services.AddSingleton<IWebServerManager>(manager);
                    services.AddSingleton(provider);
                    services.AddSingleton(store);
                    services.AddSingleton(new WebServerMetadataRepository(new TestHostEnvironment(root), TestConfiguration()));
                }, app => app.MapWebServerEndpoints(), WebServerApiRoutes.Reload.Replace("{id}", restored.InstanceId),
                    Key, HostElevationCapability.NginxLifecycle, restored.InstanceId, null, CancellationToken.None);
                using var reopenedStore = await OpenStoreAsync(root);
                var reopened = await reopenedStore.GetAsync(operationId, default);
                TestAssert.Assert(restored is { State: WebServerOperationState.Failed, Stage: "interrupted",
                        ProblemCode: "webserver.operation_interrupted" }
                    && replay == restored && reloadReplay == restored && reopened == restored && CountReloads(root) == 1
                    && transport.Calls == 0 && recoveryScopes == 0,
                    "The original interrupted receipt must persist and the retry must preserve the single reload.");
                await ObserveAsync(client, address, "after", nginx);
                Console.WriteLine($"PASS NGINX LIVE CRASH RECOVERY: operation={operationId}; interrupted receipt persisted, reloads=1, recovery scopes=0, original Nginx still serving; HTTP response drained after disconnect");
            }
            finally
            {
                if (!nginx.HasExited)
                {
                    try
                    {
                        using var quit = StartNginx(executable, root, "-s", "quit");
                        await quit.WaitForExitAsync().WaitAsync(Budget);
                        await nginx.WaitForExitAsync().WaitAsync(Budget);
                    }
                    finally
                    {
                        if (!nginx.HasExited) nginx.Kill(entireProcessTree: true);
                        await nginx.WaitForExitAsync().WaitAsync(Budget);
                    }
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static async Task RunSiteAsync(string root, bool configuring)
    {
        const string configRoot = "/etc/nginx";
        if (!OperatingSystem.IsLinux() || !File.ReadLines("/proc/self/mountinfo").Any(line =>
                line.Split(' ')[4] == configRoot && line.Contains(" - tmpfs ", StringComparison.Ordinal))
            || Directory.EnumerateFileSystemEntries(configRoot).Any())
            throw new InvalidOperationException("An empty private Nginx configuration mount is required.");

        Directory.CreateDirectory(Path.Combine(configRoot, "conf.d", "relaxkonos.d"));
        var documentRoot = Directory.CreateDirectory(Path.Combine(root, "site")).FullName;
        await File.WriteAllTextAsync(Path.Combine(documentRoot, "index.html"), "production TLS site");
        await File.WriteAllTextAsync(NginxManagedPaths.MarkerPath(new() { InstallationRoot = root }), NginxManagedPaths.MarkerContent);
        using var certificate = CreateCertificate(root);
        File.WriteAllText(Path.Combine(configRoot, "nginx.conf"), """
            user root;
            daemon off;
            worker_processes 1;
            pid nginx.pid;
            error_log nginx.log notice;
            events { worker_connections 16; }
            http {
                client_body_temp_path client-body;
                proxy_temp_path proxy;
                fastcgi_temp_path fastcgi;
                uwsgi_temp_path uwsgi;
                scgi_temp_path scgi;
                access_log off;
                include /etc/nginx/conf.d/*.conf;
            }
            """);

        using var nginx = StartNginx(PackageExecutable, root, [], Path.Combine(configRoot, "nginx.conf"));
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            while (!File.Exists(Path.Combine(root, "nginx.pid")))
            {
                if (nginx.HasExited) throw new InvalidOperationException("Isolated site Nginx exited at startup.");
                await Task.Delay(50, budget.Token);
            }
            using var store = await OpenStoreAsync(root);
            var environment = new TestHostEnvironment(root);
            var certificates = new FileCertificateStore(environment,
                new CertificateOptions { StorageRoot = Path.Combine(root, "certificates") },
                new CertificateMetadataRepository(environment, TestConfiguration()));
            var provider = CreateProvider(root, store, new IsolatedSiteTransport(root, nginx), certificates);
            var instance = await FindManagedInstanceAsync(provider);
            var manager = new WebServerManager([provider]);
            var site = await manager.UpsertSiteAsync(instance.Id, new("tls-site", "TLS site",
                [new("127.0.0.1", 443)], RootPath: documentRoot, HttpsEnabled: true,
                CertificatePath: Path.Combine(root, "fixture.pem"), PrivateKeyPath: Path.Combine(root, "fixture.key")), budget.Token);
            TestAssert.Assert(site is { HttpsEnabled: true } &&
                (await manager.ListSitesAsync(instance.Id, budget.Token))?.Single() is { HttpsEnabled: true } saved
                && saved.Id == site.Id && saved.CertificatePath == site.CertificatePath,
                "Production Nginx site metadata was not persisted.");
            using var client = CreatePinnedClient(certificate);
            await ObserveAsync(client, new Uri("https://127.0.0.1/"), "production TLS site", nginx);
            TestAssert.Assert(CountReloads(root) == 1, "The production site must reload exactly once.");
            using var managedCertificate = CreateCertificate(root);
            var certificateId = Guid.NewGuid();
            await certificates.SaveAsync(new(certificateId, ["127.0.0.1"], CertificateChallengeType.WebRootHttp01,
                CertificateKeyAlgorithm.Rsa2048, null, managedCertificate.ExportCertificatePem(),
                await File.ReadAllTextAsync(Path.Combine(root, "fixture.key"), budget.Token), DateTimeOffset.UtcNow,
                Kind: CertificateKind.SelfSigned), budget.Token);
            var bound = await manager.UpsertSiteAsync(instance.Id, new("tls-site", "TLS site",
                [new("127.0.0.1", 443)], RootPath: documentRoot, HttpsEnabled: true, CertificateId: certificateId,
                ExpectedUpdatedAt: site!.UpdatedAt), budget.Token);
            TestAssert.Assert(bound?.CertificateId == certificateId &&
                (await manager.ListSitesAsync(instance.Id, budget.Token))?.Single().CertificateId == certificateId,
                "The managed certificate binding must persist by ID.");
            var paths = await certificates.GetNginxPathsAsync(certificateId, budget.Token)
                ?? throw new InvalidOperationException("The managed certificate has no Nginx material.");
            var rendered = await File.ReadAllTextAsync("/etc/nginx/conf.d/relaxkonos.d/tls-site.conf", budget.Token);
            TestAssert.Assert(rendered.Contains(paths.FullChainPath, StringComparison.Ordinal)
                && rendered.Contains(paths.PrivateKeyPath, StringComparison.Ordinal),
                "The managed site must use the stable certificate store paths.");
            using var managedClient = CreatePinnedClient(managedCertificate);
            await ObserveAsync(managedClient, new Uri("https://127.0.0.1/"), "production TLS site", nginx);
            TestAssert.Assert(CountReloads(root) == 2, "The managed certificate binding must add exactly one reload.");
            await VerifyPublicationCrashAsync(root, nginx, configuring, budget.Token);
            Console.WriteLine($"PASS NGINX LIVE SITE TLS: local and managed certificate bindings applied, certificate={certificateId}; binding reloads=2");
        }
        finally
        {
            if (!nginx.HasExited) nginx.Kill(entireProcessTree: true);
            await nginx.WaitForExitAsync().WaitAsync(Budget);
        }
    }

    private const string PublicationKey = "publish-crash";
    private const string PublicationDomain = "localhost";
    private const int UpstreamPort = 18080;

    private static async Task VerifyPublicationCrashAsync(string root, Process nginx, bool configuring, CancellationToken cancellationToken)
    {
        // Give the new publication a separate SNI name and a real loopback upstream.
        using var upstream = new HttpListener();
        upstream.Prefixes.Add($"http://localhost:{UpstreamPort}/");
        upstream.Start();
        var responseTask = ReplyOnceAsync();
        using var worker = DockerProcessRecoveryChecks.StartWorker("--nginx-publication-worker", root, nginx.Id.ToString(), configuring.ToString());
        try
        {
            var line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
            TestAssert.Assert(Guid.TryParse(line, out var operationId), $"Missing publication receipt: {line}");
            var stage = configuring ? WebsitePublicationStage.Configuring : WebsitePublicationStage.Verifying;
            TestAssert.Assert(await worker.StandardOutput.ReadLineAsync(cancellationToken) == stage.ToString(),
                "The publication must finish site application and association before interruption.");
            var environment = new TestHostEnvironment(root);
            var options = new ApplicationDeploymentOptions();
            var catalog = new ApplicationDeploymentCatalogStore(environment, options);
            var app = catalog.ReadApplications().Single();
            var store = new WebsitePublicationStore(environment);
            var running = store.Get(operationId);
            TestAssert.Assert(running is not null && running.Stage == stage && WebsitePublicationStore.Active(running)
                && app.SiteId == (configuring ? null : running.SiteId) && running.CertificateId is not null,
                "The original Verifying receipt and committed association must be durable.");

            const string metadataPath = "/etc/nginx/conf.d/relaxkonos.d/sites.json";
            var siteMetadata = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            TestAssert.Assert(siteMetadata.Contains(running!.SiteId!, StringComparison.Ordinal) != configuring,
                "Site metadata must match the selected crash boundary.");
            using var certificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(Path.Combine(root, "publication.pem"), cancellationToken));
            using var client = CreatePinnedClient(certificate);
            var address = new Uri($"https://{PublicationDomain}/");
            await ObserveAsync(client, address, "published upstream", nginx);
            var before = CountReloads(root);
            TestAssert.Assert(before == 3,
                "The published site must proxy actual TLS traffic after a single publication reload.");
            await responseTask;

            worker.Kill();
            await worker.WaitForExitAsync(cancellationToken);
            TestAssert.Assert(worker.ExitCode != 0, "The publication worker must exit abnormally.");

            var recoveredStore = new WebsitePublicationStore(environment);
            var recoveryExecutions = 0;
            using var restarted = WebsitePublicationTestScopes.Create(recoveredStore, null!, catalog, null!, null!, null!, null!,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, new TestResidentBackgroundLeases(),
                executionCreated: () => Interlocked.Increment(ref recoveryExecutions));
            await restarted.StartAsync(cancellationToken);
            var restored = restarted.Get(operationId);
            var publication = new PublishWebsiteRequest(app.Id, running!.WebServerId, PublicationDomain, running.CertificateId, Confirmed: true);
            var replay = await ReplayOverHttpAsync<WebsitePublicationOperationDto>(services => services.AddSingleton(restarted),
                app => app.MapWebsitePublicationEndpoints(), WebsitePublicationApiRoutes.Root, PublicationKey,
                HostElevationCapability.NginxConfigurationWrite, publication.WebServerId,
                () => JsonContent.Create(publication, options: RelaxKonOSJsonOptions.Default), cancellationToken);
            TestAssert.Assert(restored is { State: WebsitePublicationState.Interrupted }
                && restored.RecoveryProblemCode == (configuring ? WebsitePublicationStore.SiteApplicationUnverified : "website.recheck_required")
                && replay.RecoveryProblemCode == restored.RecoveryProblemCode && replay.OperationId == operationId && replay.State == restored.State && replay.ProblemCode == restored.ProblemCode
                && new WebsitePublicationStore(environment).Get(operationId)?.State == restored.State,
                "Startup and same-key retry must preserve the original interrupted publication.");

            responseTask = ReplyOnceAsync();
            using var afterClient = CreatePinnedClient(certificate);
            TestAssert.Assert(await afterClient.GetStringAsync(address, cancellationToken) == "published upstream"
                && CountReloads(root) == before && !nginx.HasExited
                && catalog.ReadApplications().Single().SiteId == app.SiteId
                && await File.ReadAllTextAsync(metadataPath, cancellationToken) == siteMetadata,
                "Publication recovery must preserve live TLS, the association and the original reload count.");
            await responseTask;
            await restarted.StopAsync(cancellationToken);
            TestAssert.Assert(recoveryExecutions == 0, "Recovery and HTTP replay must leave the native execution graph absent.");
            Console.WriteLine($"PASS NGINX LIVE PUBLICATION CRASH: operation={operationId}; stage={stage}; recovery={restored!.RecoveryProblemCode}; publication reloads=1; recovery scopes=0; HTTP response drained after disconnect");
        }
        finally
        {
            await DockerProcessRecoveryChecks.StopWorkerAsync(worker);
            upstream.Stop();
            try { await responseTask; }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        async Task ReplyOnceAsync()
        {
            var context = await upstream.GetContextAsync();
            using var response = context.Response;
            var bytes = "published upstream"u8.ToArray();
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
    }

    private static async Task<TReceipt> ReplayOverHttpAsync<TReceipt>(Action<IServiceCollection> configure,
        Action<WebApplication> map, string path, string key, HostElevationCapability capability, string target,
        Func<HttpContent>? content, CancellationToken cancellationToken) where TReceipt : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Budget);
        var ct = deadline.Token;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Actor),
            new Claim("name", Actor), new Claim("jti", "publication-fixture"), new Claim("amr", "alias")], "Fixture"));
        var elevations = new HostElevationSessionStore(new TestHostAccountPrivilegeService(), new ProgressTestMode(), new HostElevationSessionState());
        var leases = new TestResidentBackgroundLeases();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(ApplicationDeploymentEndpoints.ManagePolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(ApplicationDeploymentEndpoints.ReadPolicy, policy => policy.RequireAuthenticatedUser());
        });
        configure(builder.Services);
        builder.Services.AddSingleton<IHostElevationSessionStore>(elevations);
        builder.Services.AddSingleton<RelaxKonOS.Server.HostMode.IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton<RelaxKonOS.Server.Residency.IResidentModuleWorkLeaseSource>(leases);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = principal;
            if (!context.Request.Headers.ContainsKey("X-Hold-Receipt"))
            {
                await next(context);
                return;
            }
            using var registration = context.RequestAborted.Register(() => aborted.TrySetResult());
            context.Response.OnStarting(async () => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
            try { await next(context); }
            finally { exited.TrySetResult(); }
        });
        app.UseAuthorization();
        map(app);
        await app.StartAsync(ct);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var first = NewRequest(hold: true);
        var send = client.SendAsync(first, abort.Token);
        try
        {
            await entered.Task.WaitAsync(ct);
            TestAssert.Assert(leases.Active == 1, "The website receipt must own its response lease.");
            abort.Cancel();
            await aborted.Task.WaitAsync(ct);
            try { using var unexpected = await send; throw new InvalidOperationException("Expected website client disconnect."); }
            catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
            TestAssert.Assert(leases.Active == 1 && !exited.Task.IsCompleted,
                "The disconnected website receipt must keep its lease until response execution exits.");
        }
        finally { release.TrySetResult(); }
        await exited.Task.WaitAsync(ct);
        TestAssert.Assert(leases.Active == 0, "The website receipt must return its lease after response exit.");
        using var retry = NewRequest(hold: false);
        using var response = await client.SendAsync(retry, ct);
        TestAssert.Assert(response.StatusCode == HttpStatusCode.Accepted,
            $"Website retry returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        return await response.Content.ReadFromJsonAsync<TReceipt>(RelaxKonOSJsonOptions.Default, ct)
            ?? throw new InvalidOperationException("Missing website replay response.");

        HttpRequestMessage NewRequest(bool hold)
        {
            elevations.Grant(principal, capability, target, false, "fixture");
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content?.Invoke() };
            request.Headers.Add("Idempotency-Key", key);
            if (hold) request.Headers.Add("X-Hold-Receipt", "1");
            return request;
        }
    }

    internal static async Task RunPublicationWorkerAsync(string root, int nginxPid, bool configuring)
    {
        using var nginx = Process.GetProcessById(nginxPid);
        using var store = await OpenStoreAsync(root);
        var environment = new TestHostEnvironment(root);
        var options = new ApplicationDeploymentOptions();
        var catalog = new ApplicationDeploymentCatalogStore(environment, options);
        var secrets = new ApplicationDeploymentSecretStore(environment, options, new EphemeralDataProtectionProvider(), catalog);
        var engine = DispatchProxy.Create<IDockerEngineService, BackgroundWebsiteEngine>();
        var verification = new PublicationCrashVerification();
        var clients = new FixtureHttpClientFactory([]);
        var runtime = new ApplicationDeploymentRuntime(engine, options, clients, NullLogger<ApplicationDeploymentRuntime>.Instance);
        var applications = new ApplicationDeploymentManager(catalog, secrets, new ApplicationDeploymentOperationStore(environment, options),
            runtime, options, new ApplicationDeploymentImageTagCatalog(clients, NullLogger<ApplicationDeploymentImageTagCatalog>.Instance));
        var app = await applications.CreateAsync(new CreateApplicationRequest("live-publication", ApplicationSourceKind.Image,
            HostPort: UpstreamPort), Actor, default);
        ((BackgroundWebsiteEngine)(object)engine).SetRunning(app.Id);

        var certificateId = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest($"CN={PublicationDomain}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(PublicationDomain);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var material = new CertificateMaterial(certificateId, [PublicationDomain], CertificateChallengeType.WebRootHttp01,
            CertificateKeyAlgorithm.EcdsaP256, null, certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(),
            DateTimeOffset.UtcNow, Kind: CertificateKind.SelfSigned);
        var certificates = new FileCertificateStore(environment, new CertificateOptions { StorageRoot = Path.Combine(root, "certificates") },
            new CertificateMetadataRepository(environment, TestConfiguration()));
        await certificates.SaveAsync(material, default);
        await File.WriteAllTextAsync(Path.Combine(root, "publication.pem"), material.CertificatePem);
        var transport = new IsolatedSiteTransport(root, nginx, configuring);
        var provider = CreateProvider(root, store, transport, certificates);
        var instance = await FindManagedInstanceAsync(provider);
        var certificateManager = DispatchProxy.Create<ICertificateManager, BackgroundWebsiteCertificate>();
        ((BackgroundWebsiteCertificate)(object)certificateManager).Domain = PublicationDomain;
        var executions = 0;
        using var coordinator = WebsitePublicationTestScopes.Create(new WebsitePublicationStore(environment), applications, catalog,
            new WebServerManager([provider]), certificateManager, null!, verification, new TestApplicationLifetime(),
            NullLogger<WebsitePublicationCoordinator>.Instance, new TestResidentBackgroundLeases(),
            executionCreated: () => Interlocked.Increment(ref executions));
        await coordinator.StartAsync(default);
        var receipt = await coordinator.StartAsync(new(app.Id, instance.Id, PublicationDomain, certificateId, Confirmed: true), Actor, PublicationKey);
        Console.WriteLine(receipt.OperationId);
        await (configuring ? transport.Reloaded.Task : verification.Entered.Task).WaitAsync(Budget);
        TestAssert.Assert(executions == 1 && coordinator.HasResponsibility, "The live publication must own one execution scope and lease.");
        Console.WriteLine(configuring ? WebsitePublicationStage.Configuring : WebsitePublicationStage.Verifying);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private sealed class PublicationCrashVerification : HttpMessageHandler, IHttpClientFactory
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Publication crash barrier ended.");
        }
    }

    private sealed class IsolatedSiteTransport(string root, Process nginx, bool pauseAfterReload = false) : IPrivilegedOperationTransport
    {
        internal readonly TaskCompletionSource Reloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken)
        {
            switch (request.Operation)
            {
                case PrivilegedOperationKind.NginxRuntimeStatus:
                    return new(true, NginxRunning: !nginx.HasExited);
                case PrivilegedOperationKind.NginxWriteManagedFile:
                    await File.WriteAllBytesAsync(OwnedPath(request.Path), Convert.FromBase64String(request.ContentBase64!), cancellationToken);
                    return new(true);
                case PrivilegedOperationKind.NginxMoveManagedFile:
                    File.Move(OwnedPath(request.Path), OwnedPath(request.DestinationPath), request.Overwrite);
                    return new(true);
                case PrivilegedOperationKind.NginxDeleteManagedFile:
                    File.Delete(OwnedPath(request.Path));
                    return new(true);
                case PrivilegedOperationKind.NginxConfigurationTest:
                    return await RunAsync(["-t"], cancellationToken);
                case PrivilegedOperationKind.NginxSystemServiceAction when request.NginxServiceAction == NginxSystemServiceAction.Reload:
                    var result = await RunAsync(["-s", "reload"], cancellationToken);
                    if (result.Success && pauseAfterReload)
                    {
                        Reloaded.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    return result;
                default:
                    throw new InvalidOperationException($"Unexpected isolated Nginx operation: {request.Operation}.");
            }
        }

        private async Task<PrivilegedOperationResult> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            using var command = StartNginx(PackageExecutable, root, arguments, "/etc/nginx/nginx.conf");
            await command.WaitForExitAsync(cancellationToken);
            return new(command.ExitCode == 0, command.ExitCode);
        }

        private static string OwnedPath(string? path)
        {
            var fullPath = Path.GetFullPath(path ?? throw new InvalidOperationException("Missing Nginx fixture path."));
            if (!fullPath.StartsWith("/etc/nginx/conf.d/", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected Nginx fixture path: {fullPath}.");
            return fullPath;
        }
    }

    internal static async Task RunWorkerAsync(string executable, string root)
    {
        using var store = await OpenStoreAsync(root);
        var transport = new IsolatedReloadTransport(executable, root, allowReload: true);
        var executions = 0;
        var provider = CreateProvider(root, store, transport, executionCreated: () => Interlocked.Increment(ref executions));
        var instance = await FindManagedInstanceAsync(provider);
        var receipt = await new WebServerManager([provider]).ApplyLifecycleAsync(instance.Id, WebServerLifecycleAction.Reload, Key, Actor, default)
            ?? throw new InvalidOperationException("The production provider refused the isolated reload.");
        await transport.Accepted.Task.WaitAsync(Budget);
        TestAssert.Assert(executions == 1, "The original reload must own one direct execution scope.");
        Console.WriteLine(receipt.OperationId);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task<WebServerDto> FindManagedInstanceAsync(NginxWebServerManager provider) =>
        (await provider.DiscoverAsync(default)).Single(instance => instance.ManagementMode == WebServerManagementMode.Managed
            && instance.ExecutablePath == PackageExecutable);

    private static NginxWebServerManager CreateProvider(string root, WebServerOperationStore store, IPrivilegedOperationTransport transport,
        ICertificateStore? certificates = null, Action? executionCreated = null)
    {
        var environment = new TestHostEnvironment(root);
        var configuration = TestConfiguration();
        var metadata = new WebServerMetadataRepository(environment, configuration);
        NginxWebServerManager? provider = null;
        var scopes = new OperationTestScopeFactory<NginxWebServerManager.DirectExecution>(() =>
        {
            executionCreated?.Invoke();
            return new(provider!, metadata, NullLogger<NginxWebServerManager.DirectExecution>.Instance);
        });
        provider = new(new PrivilegedNginxOperations(transport), store, scopes, metadata,
            new TestApplicationLifetime(), new NginxManagedOptions { InstallationRoot = root },
            null!, certificates!, null!, null!, NullLogger<NginxWebServerManager>.Instance);
        return provider;
    }

    // Intercept only the closed reload request. No Helper connection or systemd call is available to this fixture.
    private sealed class IsolatedReloadTransport(string executable, string root, bool allowReload) : IPrivilegedOperationTransport
    {
        internal int Calls;
        internal readonly TaskCompletionSource Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (!allowReload || request.Operation != PrivilegedOperationKind.NginxSystemServiceAction
                || request.NginxServiceAction != NginxSystemServiceAction.Reload)
                throw new InvalidOperationException("Unexpected privileged request in isolated Nginx fixture.");
            using var command = StartNginx(executable, root, "-s", "reload");
            await command.WaitForExitAsync(cancellationToken);
            if (command.ExitCode != 0) throw new InvalidOperationException("Isolated Nginx reload failed.");
            Accepted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Crash barrier exited without cancellation.");
        }
    }

    private static IConfiguration TestConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();

    private static async Task<WebServerOperationStore> OpenStoreAsync(string root)
    {
        var environment = new TestHostEnvironment(root);
        var configuration = TestConfiguration();
        var store = new WebServerOperationStore(environment, new HostOperationJournal(environment, configuration),
            NullLogger<WebServerOperationStore>.Instance, new TestResidentBackgroundLeases());
        await store.StartAsync(default);
        return store;
    }

    private static Process StartNginx(string executable, string root, params string[] extra) =>
        StartNginx(executable, root, extra, Path.Combine(root, "nginx.conf"));

    private static Process StartNginx(string executable, string root, string[] extra, string config)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "-p", root + "/", "-c", config }.Concat(extra))
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start isolated Nginx.");
    }

    private static HttpClient CreatePinnedClient(X509Certificate2 certificate) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        SslOptions = new()
        {
            RemoteCertificateValidationCallback = (_, peer, _, errors) =>
                peer is not null && peer.GetCertHashString() == certificate.Thumbprint
                && errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
        }
    }) { Timeout = TimeSpan.FromSeconds(2) };

    private static X509Certificate2 CreateCertificate(string root)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        File.WriteAllText(Path.Combine(root, "fixture.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(root, "fixture.key"), key.ExportPkcs8PrivateKeyPem());
        return certificate;
    }

    private static void WriteConfig(string root, int port, string body) =>
        File.WriteAllText(Path.Combine(root, "nginx.conf"), $$"""
            daemon off;
            worker_processes 1;
            pid nginx.pid;
            error_log nginx.log notice;
            events { worker_connections 16; }
            http {
                client_body_temp_path client-body;
                proxy_temp_path proxy;
                fastcgi_temp_path fastcgi;
                uwsgi_temp_path uwsgi;
                scgi_temp_path scgi;
                access_log off;
                server {
                    listen 127.0.0.1:{{port}} ssl;
                    ssl_certificate fixture.pem;
                    ssl_certificate_key fixture.key;
                    location / { return 200 "{{body}}"; }
                }
            }
            """);

    private static int CountReloads(string root) =>
        File.ReadLines(Path.Combine(root, "nginx.log")).Count(line => line.EndsWith(": reconfiguring", StringComparison.Ordinal));

    private static async Task ObserveAsync(HttpClient client, Uri address, string expected, Process nginx)
    {
        using var budget = new CancellationTokenSource(Budget);
        while (!nginx.HasExited)
        {
            try
            {
                if (await client.GetStringAsync(address, budget.Token) == expected) return;
            }
            catch (HttpRequestException) when (!budget.IsCancellationRequested) { }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested) { }
            await Task.Delay(50, budget.Token);
        }
        throw new InvalidOperationException("The isolated Nginx process exited before serving the expected configuration.");
    }
}
