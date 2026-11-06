using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.WebsitePublishing;
using RelaxKonOS.Server.ApplicationDeployments;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.WebsitePublishing;

internal static class ModuleBackgroundOwnershipChecks
{
    // A deliberately quarantined metadata owner remains owned through this test process, never fake-recovered for disposal.
    private static readonly List<object> RetainedFaults = [];
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var count = 0;
        void Check(bool ok, string text)
        { if (!ok) throw new Exception(text); Console.WriteLine($"PASS MB05 OWNER {++count}: {text}"); }
        {
            var directory = Path.Combine(root, "certificate-recovery"); Directory.CreateDirectory(Path.Combine(directory, "data"));
            var environment = new TestHostEnvironment(directory); var leases = new TestResidentBackgroundLeases();
            var id = Guid.NewGuid(); var certificate = Guid.NewGuid();
            var path = Path.Combine(directory, "data", "certificate-operations.json");
            var original = JsonSerializer.Serialize(new[] { new { operationId = id,
                idempotencyKey = CertificateOperationStore.RequestScope("old", certificate, "issue", "actor"), certificateId = certificate,
                kind = "issue", actor = "actor", state = "running", stage = "running", problemCode = "", startedAt = DateTimeOffset.UtcNow, completedAt = (DateTimeOffset?)null } });
            await File.WriteAllTextAsync(path, original);
            using var ledger = CreateCertificates(environment, leases);
            Check(leases.Acquired == 0 && CertificateOperationStore.ReadPending(environment).SequenceEqual([id]) && await File.ReadAllTextAsync(path) == original,
                "Certificate construction and disabled inspection do not perform startup recovery or rewrite the pending ledger");
            try { await ledger.StartAsync("before-ready", Guid.NewGuid(), "issue", "actor", _ => Task.FromResult(""), default, default); throw new Exception("Uninitialized certificate queued"); }
            catch (ModuleUnavailableException) { }
            await ledger.StartAsync(default);
            Check(leases.Acquired == 1 && leases.Active == 0 && (await ledger.GetAsync(id, default)) is { State: CertificateOperationState.Failed, Stage: "interrupted" },
                "Explicit certificate startup recovery runs under one real admission and preserves the original interrupted outcome");
            await ledger.StopAsync(default);
        }
        {
            var directory = Path.Combine(root, "invalid-certificate-recovery"); Directory.CreateDirectory(Path.Combine(directory, "data"));
            var environment = new TestHostEnvironment(directory); var leases = new TestResidentBackgroundLeases();
            var path = Path.Combine(directory, "data", "certificate-operations.json"); await File.WriteAllTextAsync(path, "{invalid");
            using var ledger = CreateCertificates(environment, leases);
            await ledger.StartAsync(default);
            try { await ledger.StartAsync("invalid-startup", Guid.NewGuid(), "issue", "actor", _ => Task.FromResult(""), default, default); throw new Exception("Corrupt startup admitted work"); }
            catch (ModuleUnavailableException) { }
            Check(leases.Active == 1 && leases.RecoveryRequired == 1 && ledger.HasResponsibility && await File.ReadAllTextAsync(path) == "{invalid",
                "Unknown certificate startup recovery is quarantined without replacing or clearing its unreadable ledger");
            try { await ledger.StopAsync(default); throw new Exception("Corrupt recovery Stop hidden"); } catch (InvalidOperationException) { }
        }
        {
            var directory = Path.Combine(root, "isolated-certificate-recovery"); Directory.CreateDirectory(Path.Combine(directory, "data"));
            await File.WriteAllTextAsync(Path.Combine(directory, "data", "certificate-operations.json"), "{invalid");
            var f = await Fixture.CreateAsync(directory);
            Check(f.Coordinator.Describe(ServerModules.CertificatesId) is { State: ModuleResidencyState.Faulted, ActiveLeases: 1 },
                "Certificate Hosted startup returns with a quarantined module instead of failing the unrelated host lifecycle");
            using var website = new WebsitePublicationCoordinator(new WebsitePublicationStore(f.Environment), null!, null!, null!, null!, f.Certificates, null!,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await website.StartAsync(default);
            try { await website.StartAsync(new PublishWebsiteRequest(Guid.NewGuid(), "nginx", "example.test", Guid.NewGuid(), Confirmed: true), "actor", "blocked"); throw new Exception("Blocked dependency published"); }
            catch (WebsitePublicationException error) when (error.ProblemCode == "website.operation_store_unavailable") { }
            Check(!website.HasResponsibility && f.Active(ServerModules.CertificatesId) == 1,
                "Website startup isolates an unavailable certificate dependency and refuses publication before invoking its application port");
            RetainedFaults.Add(f);
        }
        await using (var f = await Fixture.CreateAsync(Path.Combine(root, "certificates")))
        {
            var ledger = f.Certificates;
            using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
            try { await ledger.StartAsync("pre-cancelled", Guid.NewGuid(), "issue", "actor", _ => Task.FromResult(""), default, preCancelled.Token); throw new Exception("Precancelled certificate queued"); }
            catch (OperationCanceledException) { }
            Check(CertificateOperationStore.ReadPending(f.Environment).Count == 0 && f.Active(ServerModules.CertificatesId) == 0,
                "Pre-cancelled certificate admission creates neither a queued record nor a work owner");
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var id = Guid.NewGuid();
            using var caller = new CancellationTokenSource();
            var operation = await ledger.StartAsync("first", id, "issue", "actor", ct =>
            { Interlocked.Increment(ref calls); entered.TrySetResult(ct); return release.Task; }, default, caller.Token);
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active(ServerModules.CertificatesId) == 1, "Certificate action owns a real resident work lease after returning its queued receipt");
            caller.Cancel();
            Check(!token.IsCancellationRequested && f.Active(ServerModules.CertificatesId) == 1,
                "Disconnecting the admission caller cannot cancel an already accepted certificate action");
            var replay = await ledger.StartAsync("first", Guid.NewGuid(), "issue", "actor", _ => throw new Exception("replay"), default, CancellationToken.None);
            Check(replay.OperationId == operation.OperationId && calls == 1 && f.Active(ServerModules.CertificatesId) == 1,
                "Certificate creation replay returns the original operation without executing or retaining a second action");
            var cancelled = await ledger.CancelAsync(operation.OperationId, default);
            Check(token.IsCancellationRequested && cancelled is { State: CertificateOperationState.Running, Stage: "cancel-requested", CompletedAt: null }
                && f.Active(ServerModules.CertificatesId) == 1,
                "Certificate cancellation requests cancellation without declaring an unfinished action terminal");
            using var budget = new CancellationTokenSource();
            var stopping = ledger.StopAsync(budget.Token); budget.Cancel();
            try { await stopping; throw new Exception("Stop budget ignored"); } catch (OperationCanceledException) { }
            Check(f.Active(ServerModules.CertificatesId) == 1 && !release.Task.IsCompleted, "Certificate Stop timeout retains real action responsibility");
            try { await ledger.StartAsync("after-stop", Guid.NewGuid(), "issue", "actor", _ => Task.FromResult(""), default, CancellationToken.None); throw new Exception("Stopped certificate owner accepted"); }
            catch (ModuleUnavailableException) { }
            release.TrySetResult("");
            await ledger.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check((await ledger.GetAsync(operation.OperationId, default)) is { State: CertificateOperationState.Cancelled, CompletedAt: not null }
                && f.Active(ServerModules.CertificatesId) == 0 && calls == 1, "Only actual action completion persists terminal cancellation and returns its lease");
            Check(CertificateOperationStore.ReadPending(f.Environment).Count == 0, "Certificate disable inspection reads final persisted state without rewriting recovery records");
        }
        await using (var f = await Fixture.CreateAsync(Path.Combine(root, "certificate-stop")))
        {
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = await f.Certificates.StartAsync("coordinator", Guid.NewGuid(), "issue", null,
                ct => { entered.TrySetResult(); return AcmeCallLifetime.AwaitAsync(() => release.Task, ct); }, default, CancellationToken.None);
            await entered.Task;
            var drain = f.Coordinator.DisposeAsync().AsTask();
            Check(!drain.IsCompleted && f.Active(ServerModules.CertificatesId) == 1,
                "Coordinator stop cannot detach an ACME call that does not support cancellation");
            release.TrySetResult(""); await f.Certificates.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await f.Certificates.GetAsync(operation.OperationId, default))?.State == CertificateOperationState.Cancelled,
                "ACME task lifetime propagates cancellation after its actual Task completes");
        }
        var immediateLeases = new TestResidentBackgroundLeases();
        using (var owner = new ResidentBackgroundWorkOwner(immediateLeases, ServerModules.DockerId))
        {
            for (var index = 0; index < 100; index++)
            {
                using var work = await owner.AdmitAsync(default);
                work.Run(_ => Task.CompletedTask);
                await work.Completion;
            }
            Check(!owner.HasResponsibility && immediateLeases.Active == 0 && immediateLeases.Acquired == 100,
                "One hundred synchronously completed tasks leave no registered work or retained lease");
            await owner.StopAsync(default);
            try { await owner.AdmitAsync(default); throw new Exception("Closed generic owner accepted"); } catch (ModuleUnavailableException) { }
        }
        {
            var delayed = new DelayedLeases();
            using var owner = new ResidentBackgroundWorkOwner(delayed, ServerModules.DockerId);
            var admission = owner.AdmitAsync(default).AsTask();
            await delayed.Entered.Task;
            using var budget = new CancellationTokenSource(); var stop = owner.StopAsync(budget.Token); budget.Cancel();
            try { await stop; throw new Exception("Pending admission detached"); } catch (OperationCanceledException) { }
            Check(owner.HasResponsibility && delayed.Inner.Active == 1 && !admission.IsCompleted,
                "Stop budget cannot discard background admission whose actual lease acquisition has not returned");
            try { await owner.AdmitAsync(default); throw new Exception("Closed pending owner accepted"); } catch (ModuleUnavailableException) { }
            delayed.Release.TrySetResult();
            try { await admission; throw new Exception("Late stopped admission published"); } catch (OperationCanceledException) { }
            await owner.StopAsync(default);
            Check(!owner.HasResponsibility && delayed.Inner.Active == 0 && delayed.Inner.Acquired == 1,
                "Late stopped admission returns protection without publishing work or leaving a pending owner");
        }
        await VerifyFaultAsync(Path.Combine(root, "terminal-write"), false);
        await VerifyFaultAsync(Path.Combine(root, "challenge-cleanup"), true);
        {
            var f = await Fixture.CreateAsync(Path.Combine(root, "quarantined"));
            var owner = new ResidentBackgroundWorkOwner(f.Source, ServerModules.DockerId);
            using var work = await owner.AdmitAsync(default);
            work.Fail(new IOException("test unresolved completion"));
            Check(f.Coordinator.Describe(ServerModules.DockerId) is { State: ModuleResidencyState.Faulted, ActiveLeases: 1 },
                "Unresolved background completion quarantines the real coordinator generation and retains protection");
            try { await owner.AdmitAsync(default); throw new Exception("Faulted generation admitted work"); } catch (ModuleUnavailableException) { }
            Check(owner.HasResponsibility, "A quarantined generation refuses another side-effect owner without discarding original responsibility");
            RetainedFaults.Add(f); RetainedFaults.Add(owner);
        }

        await using (var f = await Fixture.CreateAsync(Path.Combine(root, "docker")))
        {
            var compose = new FakeComposeService { BlockOnDeploy = true, PreviewServices = ["web"] };
            var options = Options.Create(new DockerComposeOptions { DataDirectory = Path.Combine(root, "docker-ledger"), MaximumConcurrentOperations = 1 });
            var store = new DockerStackOperationStore(f.Environment, options);
            using var coordinator = new DockerStackOperationCoordinator(store, compose, options, new TestApplicationLifetime(),
                new DockerChecks.CaptureOperationalEvents(), NullLogger<DockerStackOperationCoordinator>.Instance, f.Source);
            await coordinator.StartAsync(default);
            const string yaml = "services:\n  web:\n    image: nginx:alpine\n";
            var definition = new DockerStackDefinitionDto("owned", yaml);
            var operation = await coordinator.DeployAsync(new(definition, DockerStackValidation.DefinitionVersion("owned", yaml)), "actor", "once");
            await UntilAsync(() => compose.DeployCalls == 1);
            Check(compose.DeployCalls == 1 && f.Active(ServerModules.DockerId) == 1, "Docker queued work retains a real lease while its Compose action is in progress");
            using var budget = new CancellationTokenSource(); var stop = coordinator.StopAsync(budget.Token); budget.Cancel();
            try { await stop; throw new Exception("Docker Stop detached"); } catch (OperationCanceledException) { }
            Check(f.Active(ServerModules.DockerId) == 1, "Docker Stop timeout preserves a non-cooperative Compose task");
            compose.ReleaseDeploy(); await coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active(ServerModules.DockerId) == 0 && DockerStackOperationStore.Active(store.Get(operation.OperationId)!.Operation),
                "Docker actual shutdown completion preserves its durable active record for original restart observation");
            using var restarted = new DockerStackOperationCoordinator(store, compose, options, new TestApplicationLifetime(),
                new DockerChecks.CaptureOperationalEvents(), NullLogger<DockerStackOperationCoordinator>.Instance, f.Source);
            await restarted.StartAsync(default);
            Check(store.Get(operation.OperationId)!.Operation.State == DockerStackOperationState.Interrupted && compose.DeployCalls == 1,
                "Docker restart observes the original operation and never replays Compose");
            await restarted.StopAsync(default);
        }
        await using (var f = await Fixture.CreateAsync(Path.Combine(root, "websites")))
        {
            var options = new ApplicationDeploymentOptions();
            var catalog = new ApplicationDeploymentCatalogStore(f.Environment, options);
            var secrets = new ApplicationDeploymentSecretStore(f.Environment, options, new EphemeralDataProtectionProvider(), catalog);
            var engine = DispatchProxy.Create<IDockerEngineService, BackgroundWebsiteEngine>();
            var controlled = (BackgroundWebsiteEngine)(object)engine;
            var clients = new FixtureHttpClientFactory([]);
            var runtime = new ApplicationDeploymentRuntime(engine, options, clients, NullLogger<ApplicationDeploymentRuntime>.Instance);
            var applications = new ApplicationDeploymentManager(catalog, secrets, new ApplicationDeploymentOperationStore(f.Environment, options), runtime, options,
                new ApplicationDeploymentImageTagCatalog(clients, NullLogger<ApplicationDeploymentImageTagCatalog>.Instance));
            var app = await applications.CreateAsync(new CreateApplicationRequest("owned-site", ApplicationSourceKind.Image, HostPort: 9080), "actor", default);
            var store = new WebsitePublicationStore(f.Environment);
            using var coordinator = new WebsitePublicationCoordinator(store, applications, catalog, null!, null!, f.Certificates, clients,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await coordinator.StartAsync(default); controlled.Pause();
            var request = new PublishWebsiteRequest(app.Id, "nginx", "example.test", Guid.NewGuid(), Confirmed: true);
            var operation = await coordinator.StartAsync(request, "actor", "once");
            await controlled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Active(ServerModules.WebServerId) == 1, "Website publication retains a real lease during application observation");
            using var budget = new CancellationTokenSource(); var stop = coordinator.StopAsync(budget.Token); budget.Cancel();
            try { await stop; throw new Exception("Website stop detached"); } catch (OperationCanceledException) { }
            Check(f.Active(ServerModules.WebServerId) == 1, "Website Stop timeout retains its actual observation task");
            controlled.Complete(); await coordinator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Check(WebsitePublicationStore.Active(store.Get(operation.OperationId)!) && f.Active(ServerModules.WebServerId) == 0,
                "Website stopping leaves a durable active record after actual observation ends");
            using var restarted = new WebsitePublicationCoordinator(store, applications, catalog, null!, null!, f.Certificates, clients,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await restarted.StartAsync(default);
            Check(store.Get(operation.OperationId)!.State == WebsitePublicationState.Interrupted && controlled.Calls == 2,
                "Website restart marks the original publication interrupted without replaying its observation or side effects");
            await restarted.StopAsync(default);

            controlled.SetRunning(app.Id);
            var web = DispatchProxy.Create<IWebServerManager, BackgroundWebsiteProvider>();
            var applied = (BackgroundWebsiteProvider)(object)web;
            var cert = DispatchProxy.Create<ICertificateManager, BackgroundWebsiteCertificate>();
            using var associating = new WebsitePublicationCoordinator(store, applications, catalog, web, cert, f.Certificates, clients,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await associating.StartAsync(default);
            var association = await associating.StartAsync(request, "actor", "association");
            await applied.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var draining = associating.StopAsync(default);
            try
            {
                Check(!draining.IsCompleted && f.Active(ServerModules.WebServerId) == 1,
                    "Website stop retains work while the site provider returns its committed result");
            }
            finally { applied.Release.TrySetResult(); }
            await draining.WaitAsync(TimeSpan.FromSeconds(5));
            Check(catalog.ReadApplications().Single().SiteId == $"app{app.Id:N}"
                && WebsitePublicationStore.Active(store.Get(association.OperationId)!) && f.Active(ServerModules.WebServerId) == 0,
                "Website shutdown finishes catalog association for the already applied site before releasing its lease");
            var reopened = new WebsitePublicationStore(f.Environment);
            using var afterAssociation = new WebsitePublicationCoordinator(reopened, applications, catalog, web, cert, f.Certificates, clients,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await afterAssociation.StartAsync(default);
            catalog.Delete(app.Id);
            var original = await afterAssociation.StartAsync(request, "actor", "association");
            Check(original.OperationId == association.OperationId && original.State == WebsitePublicationState.Interrupted && applied.Calls == 1,
                "Website startup and same-key retry preserve the original receipt after the application record disappears");
            try
            {
                await afterAssociation.StartAsync(request, "actor", "new-missing-app");
                throw new InvalidOperationException("A fresh publication admitted a missing application.");
            }
            catch (WebsitePublicationException error) when (error.StatusCode == 404) { }
            Check(reopened.History(app.Id, 20).Count == 2 && applied.Calls == 1,
                "A fresh request for the missing application creates neither a receipt nor a site action");
            await afterAssociation.StopAsync(default);

            var httpsApp = await applications.CreateAsync(new CreateApplicationRequest("https-drain", ApplicationSourceKind.Image, HostPort: 9081), "actor", default);
            controlled.SetRunning(httpsApp.Id);
            ((BackgroundWebsiteCertificate)(object)cert).Domain = "localhost";
            var httpsRequest = request with { ApplicationId = httpsApp.Id, Domain = "localhost" };
            using var key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            certificateRequest.CertificateExtensions.Add(san.Build());
            using var issuedTls = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var tls = X509CertificateLoader.LoadPkcs12(issuedTls.Export(X509ContentType.Pfx), null);
            var tlsBuilder = WebApplication.CreateBuilder();
            tlsBuilder.Logging.ClearProviders();
            tlsBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(tls)));
            await using var endpoint = tlsBuilder.Build();
            var enteredHttps = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abortedHttps = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            endpoint.MapGet("/", async (HttpContext context) =>
            {
                enteredHttps.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                { abortedHttps.TrySetResult(); }
            });
            endpoint.MapGet("/probe", () => "ready");
            await endpoint.StartAsync();
            var port = new Uri(endpoint.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            var fingerprint = tls.GetCertHashString(HashAlgorithmName.SHA256);
            using (var probe = new LoopbackWebsiteVerification(port, fingerprint).CreateClient("WebsitePublicationVerification"))
                TestAssert.Assert(await probe.GetStringAsync("https://localhost/probe").WaitAsync(TimeSpan.FromSeconds(10)) == "ready",
                    "The TLS fixture must authenticate the loopback service before running the cancellation check.");
            var verification = new LoopbackWebsiteVerification(port, fingerprint);
            using var verifying = new WebsitePublicationCoordinator(reopened, applications, catalog, web, cert, f.Certificates, verification,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await verifying.StartAsync(default);
            var verifyingReceipt = await verifying.StartAsync(httpsRequest, "actor", "https-drain");
            try { await enteredHttps.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException("Website did not reach its TLS endpoint. Receipt: "
                    + JsonSerializer.Serialize(reopened.Get(verifyingReceipt.OperationId))
                    + $"; client calls: {verification.Calls}; TLS: {verification.TlsObservation}", error);
            }
            Check(f.Active(ServerModules.WebServerId) == 1
                && reopened.Get(verifyingReceipt.OperationId)?.Stage == WebsitePublicationStage.Verifying,
                "Website HTTPS verification owns its lease while real TLS response headers are pending");
            await verifying.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            await abortedHttps.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.Active(ServerModules.WebServerId) == 0 && WebsitePublicationStore.Active(reopened.Get(verifyingReceipt.OperationId)!),
                "Stopping drains the real HTTPS request and leaves the original active receipt for recovery");
            var httpsRecovery = new WebsitePublicationStore(f.Environment);
            using var recoveredHttps = new WebsitePublicationCoordinator(httpsRecovery, applications, catalog, web, cert, f.Certificates, verification,
                new TestApplicationLifetime(), NullLogger<WebsitePublicationCoordinator>.Instance, f.Source);
            await recoveredHttps.StartAsync(default);
            var httpsReplay = await recoveredHttps.StartAsync(httpsRequest, "actor", "https-drain");
            Check(httpsReplay.OperationId == verifyingReceipt.OperationId && httpsReplay.State == WebsitePublicationState.Interrupted
                && verification.Calls == 1 && applied.Calls == 2,
                "Website recovery does not repeat real HTTPS verification or the site application");
            await recoveredHttps.StopAsync(default);
            await endpoint.StopAsync();
        }
        Console.WriteLine($"Module background ownership checks passed: {count}");

        async Task VerifyFaultAsync(string directory, bool cleanup)
        {
            Directory.CreateDirectory(directory); var environment = new TestHostEnvironment(directory);
            var leases = new TestResidentBackgroundLeases();
            using var ledger = CreateCertificates(environment, leases);
            await ledger.StartAsync(default);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = await ledger.StartAsync("fault", Guid.NewGuid(), "issue", "actor", _ => { entered.TrySetResult(); return release.Task; }, default, CancellationToken.None);
            await entered.Task;
            if (cleanup) release.TrySetException(new CertificateChallengeCleanupException([new IOException("test challenge cleanup")]));
            else
            {
                var path = Path.Combine(directory, "data", "certificate-operations.json");
                File.Delete(path); Directory.CreateDirectory(path); release.TrySetResult("");
            }
            try { await ledger.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Ownership fault hidden"); }
            catch (Exception error) when (cleanup ? error is CertificateChallengeCleanupException : error is IOException or UnauthorizedAccessException) { }
            Check(leases.Active == 1 && ledger.HasResponsibility && (await ledger.GetAsync(operation.OperationId, default))?.State == CertificateOperationState.Running,
                cleanup ? "Unconfirmed challenge cleanup retains its owner and lease rather than declaring successful issuance" : "Terminal persistence failure retains the action owner and prevents an unverified successful receipt");
            try { await ledger.StopAsync(default); throw new Exception("Fault replay hidden"); }
            catch (Exception error) when (cleanup ? error is CertificateChallengeCleanupException : error is IOException or UnauthorizedAccessException) { }
            Check(leases.Active == 1 && leases.RecoveryRequired == 1, "Repeated Stop reports quarantined cleanup responsibility without releasing or replaying it");
        }
    }

    private static CertificateOperationStore CreateCertificates(TestHostEnvironment environment, IResidentModuleWorkLeaseSource source)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
        var options = new CertificateOptions { StorageRoot = Path.Combine(environment.ContentRootPath, "certificates") };
        var certificates = new FileCertificateStore(environment, options, new CertificateMetadataRepository(environment, configuration));
        return new(environment, new HostOperationJournal(environment, configuration), certificates,
            new CertificateRenewalAttemptRepository(environment, configuration, options), NullLogger<CertificateOperationStore>.Instance, source);
    }
    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(10); }
    }
    private sealed class DelayedLeases : IResidentModuleWorkLeaseSource
    {
        internal readonly TestResidentBackgroundLeases Inner = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IModuleWorkLease> AcquireAsync(string moduleId, CancellationToken ct = default)
        { var lease = await Inner.AcquireAsync(moduleId, ct); Entered.TrySetResult(); await Release.Task; return lease; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        internal TestHostEnvironment Environment { get; }
        internal IResidentModuleWorkLeaseSource Source => _app.Services.GetRequiredService<IResidentModuleWorkLeaseSource>();
        internal ModuleResidencyCoordinator Coordinator => _app.Services.GetRequiredService<ModuleResidencyCoordinator>();
        internal CertificateOperationStore Certificates { get; }
        internal int Active(string id) => Coordinator.Describe(id)?.ActiveLeases ?? 0;
        private Fixture(string root)
        {
            Directory.CreateDirectory(root); Environment = new(root);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.Environment.ContentRootPath = root;
            var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.DockerId, ServerModules.CertificatesId, ServerModules.WebServerId });
            modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
            _app = builder.Build(); Certificates = CreateCertificates(Environment, Source);
        }
        internal static async Task<Fixture> CreateAsync(string root)
        { var fixture = new Fixture(root); await fixture.Certificates.StartAsync(default); return fixture; }
        public async ValueTask DisposeAsync()
        { await Certificates.StopAsync(default); Certificates.Dispose(); await _app.DisposeAsync(); }
    }
}

public class BackgroundWebsiteEngine : DispatchProxy
{
    private TaskCompletionSource<IReadOnlyList<DockerContainerDto>> _response = Ready();
    internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls;
    private DockerContainerDetailsDto? _details;
    internal void SetRunning(Guid applicationId)
    {
        var name = ApplicationDeploymentValidation.ContainerName(applicationId);
        _details = new("fixture", name, "fixture", "", "running", "", "", "", "", [], [], [], [],
            new Dictionary<string, string>
            {
                [ApplicationDeploymentValidation.ManagedLabel] = "true",
                [ApplicationDeploymentValidation.OwnerLabel] = ApplicationDeploymentValidation.OwnerValue,
                [ApplicationDeploymentValidation.ApplicationIdLabel] = applicationId.ToString("D")
            });
        _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _response.SetResult([new("fixture", name, "fixture", "running", "")]);
    }
    internal void Pause() { _response = new(TaskCreationOptions.RunContinuationsAsynchronously); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal void Complete() => _response.TrySetResult([]);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IDockerEngineService.GetContainerAsync)) return Task.FromResult(_details);
        if (method.Name != nameof(IDockerEngineService.ListContainersAsync)) throw new InvalidOperationException("Unexpected Docker mutation");
        Calls++; Entered.TrySetResult(); return _response.Task;
    }
    private static TaskCompletionSource<IReadOnlyList<DockerContainerDto>> Ready()
    { var response = new TaskCompletionSource<IReadOnlyList<DockerContainerDto>>(TaskCreationOptions.RunContinuationsAsynchronously); response.SetResult([]); return response; }
}

public class BackgroundWebsiteProvider : DispatchProxy
{
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Calls;

    protected override object Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        nameof(IWebServerManager.ListAsync) => Task.FromResult<IReadOnlyList<WebServerDto>>(
            [new("nginx", "nginx", WebServerType.Nginx, WebServerManagementMode.Managed, "nginx", null, "fixture", DateTimeOffset.UtcNow, new(true, true, true))]),
        nameof(IWebServerManager.ListSitesAsync) => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([]),
        nameof(IWebServerManager.UpsertSiteAsync) => ApplyAsync((UpsertWebServerSiteRequest)args![1]!),
        _ => throw new InvalidOperationException("Unexpected website provider call.")
    };

    private async Task<WebServerSiteDto?> ApplyAsync(UpsertWebServerSiteRequest request)
    {
        Interlocked.Increment(ref Calls);
        Entered.TrySetResult();
        await Release.Task;
        return new(request.Id!, "nginx", request.Name, request.Bindings, null, false, request.Routes!,
            request.CertificateId, true, true, false, DateTimeOffset.UtcNow);
    }
}

public class BackgroundWebsiteCertificate : DispatchProxy
{
    internal string Domain = "example.test";
    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != nameof(ICertificateManager.GetAsync))
            throw new InvalidOperationException("Unexpected website certificate call.");
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult<CertificateDto?>(new((Guid)args![0]!, Domain, [Domain], null, null, null,
            now.AddDays(-1), now.AddDays(1), CertificateStatus.Active, CertificateChallengeType.DirectHttp01,
            CertificateKeyAlgorithm.EcdsaP256, null, null, null, null, now, now));
    }
}

internal sealed class LoopbackWebsiteVerification(int port, string fingerprint) : IHttpClientFactory
{
    internal int Calls;
    internal string? TlsObservation;

    public HttpClient CreateClient(string name)
    {
        if (name != "WebsitePublicationVerification") throw new ArgumentException("Unexpected verification client.");
        Interlocked.Increment(ref Calls);
        return new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new()
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    var matched = certificate?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint;
                    TlsObservation = $"errors={errors}; matched={matched}";
                    return matched && (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == 0;
                }
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (context.DnsEndPoint.Host != "localhost" || context.DnsEndPoint.Port != 443)
                    throw new InvalidOperationException("Unexpected website verification destination.");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        });
    }
}
