internal static class WebServerChecks
{
internal static async Task VerifyDeploymentAndNginxSnapshotsAsync(string root)
{
    var databasePath = Path.Combine(root, "deployment-and-snapshots.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Storage:Provider"] = "sqlite",
        ["Storage:DatabasePath"] = databasePath
    }).Build();
    var certificateId = Guid.NewGuid();
    var certificate = new StoredCertificate(certificateId, "a".PadLeft(32, 'a'), "one.example.test", ["one.example.test"],
        CertificateChallengeType.WebRootHttp01, CertificateKeyAlgorithm.EcdsaP256, null, null, null, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7), CertificateStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ops@example.test", null, null, null, null);
    var deployments = new CertificateDeploymentRepository(environment, configuration);
    await deployments.RecordKestrelAsync(certificate, false, "certificate.kestrel_activation_failed", CancellationToken.None);
    TestAssert.Assert((await deployments.ListKestrelAsync(CancellationToken.None)).Count == 0, "A failed Kestrel deployment became restartable.");
    await deployments.RecordKestrelAsync(certificate, true, null, CancellationToken.None);
    TestAssert.Assert((await deployments.ListKestrelAsync(CancellationToken.None)).Single().CurrentVersion == certificate.Version, "A successful Kestrel deployment was not persisted.");

    var configPath = Path.Combine(root, "nginx.conf");
    Directory.CreateDirectory(Path.Combine(root, "conf.d"));
    await File.WriteAllTextAsync(configPath, "events {}\nhttp {\n  include conf.d/*.conf;\n}\n");
    var instance = new WebServerDto("nginx-test", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated, "/usr/sbin/nginx", configPath,
        "test", DateTimeOffset.UtcNow, new WebServerCapabilities(true, true, true));
    var webServers = new WebServerMetadataRepository(environment, configuration);
    await webServers.UpsertInstanceAsync(instance, CancellationToken.None);
    var snapshot = await webServers.CreateSnapshotAsync(instance, CancellationToken.None) ?? throw new InvalidOperationException("Nginx snapshot was not created.");
    TestAssert.Assert(await webServers.IsSnapshotCurrentAsync(configPath, snapshot, CancellationToken.None), "Fresh Nginx snapshot was incorrectly stale.");
    await File.AppendAllTextAsync(configPath, "# external change\n");
    TestAssert.Assert(!await webServers.IsSnapshotCurrentAsync(configPath, snapshot, CancellationToken.None), "Nginx external modification was not detected.");

    var resolver = typeof(NginxWebServerManager).GetMethod("FindOwnedIncludeDirectory", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx include resolver was not found.");
    var resolved = (string?)resolver.Invoke(null, [configPath]);
    TestAssert.Assert(string.Equals(resolved, Path.Combine(root, "conf.d"), StringComparison.Ordinal), "Nginx http-context include was not found.");
    var outsideHttp = Path.Combine(root, "outside-http.conf");
    await File.WriteAllTextAsync(outsideHttp, "include conf.d/*.conf;\nevents {}\nhttp {}\n");
    TestAssert.Assert(resolver.Invoke(null, [outsideHttp]) is null, "Nginx include outside http context was accepted.");

    var managedRoot = Path.Combine(root, "managed-nginx");
    var resolveManagedExecutable = typeof(NginxWebServerManager).GetMethod("ResolveManagedExecutablePath", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Managed Nginx executable resolver was not found.");
    var managedExecutable = (string)resolveManagedExecutable.Invoke(null, [managedRoot, true])!;
    if (OperatingSystem.IsLinux())
        TestAssert.Assert(managedExecutable == "/usr/sbin/nginx", "Built-in Linux installation must use the package executable instead of creating a second copy.");

    var shouldSkipManaged = typeof(NginxWebServerManager).GetMethod("ShouldSkipManagedExecutable", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx managed executable de-duplication check was not found.");
    TestAssert.Assert(!(bool)shouldSkipManaged.Invoke(null, [false, "/usr/sbin/nginx", "/usr/sbin/nginx"])!,
        "An unmanaged system Nginx was incorrectly hidden from integration candidates.");
    TestAssert.Assert((bool)shouldSkipManaged.Invoke(null, [true, "/usr/sbin/nginx", "/usr/sbin/nginx"])!,
        "A managed Nginx executable was not de-duplicated from discovery.");

    var resolveManagedConfiguration = typeof(NginxWebServerManager).GetMethod("ResolveManagedConfigurationPath", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Managed Nginx configuration resolver was not found.");
    var managedConfigurationPath = (string)resolveManagedConfiguration.Invoke(null, [managedRoot, true])!;
    if (OperatingSystem.IsLinux())
        TestAssert.Assert(managedConfigurationPath == "/etc/nginx/nginx.conf", "Built-in Linux installation must use the package configuration managed by nginx.service.");

    if (OperatingSystem.IsLinux())
    {
        var isPosixProcessAlive = typeof(NginxWebServerManager).GetMethod("IsPosixProcessAlive", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Linux process liveness checker was not found.");
        TestAssert.Assert((bool)isPosixProcessAlive.Invoke(null, [Environment.ProcessId])!, "The current Linux process was not recognized as alive.");
        TestAssert.Assert(!(bool)isPosixProcessAlive.Invoke(null, [int.MaxValue])!, "A non-existent Linux process was reported as alive.");
    }

    var validServerName = typeof(NginxWebServerManager).GetMethod("IsValidServerName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx server-name validator was not found.");
    TestAssert.Assert((bool)validServerName.Invoke(null, ["192.0.2.10"])!, "IPv4 addresses were rejected as Nginx server names.");
    TestAssert.Assert((bool)validServerName.Invoke(null, ["2001:db8::10"])!, "IPv6 addresses were rejected as Nginx server names.");
    TestAssert.Assert((bool)validServerName.Invoke(null, ["localhost"])!, "The explicit localhost development binding was rejected.");
    TestAssert.Assert(!(bool)validServerName.Invoke(null, ["internal-service"])!, "An arbitrary single-label host name was accepted.");
    TestAssert.Assert(!(bool)validServerName.Invoke(null, ["example.com; return 200"])!, "Unsafe Nginx server name was accepted.");

    var isNginxProcessName = typeof(NginxWebServerManager).GetMethod("IsNginxProcessName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx process-name matcher was not found.");
    TestAssert.Assert((bool)isNginxProcessName.Invoke(null, ["nginx"])!, "The normal Nginx process name was rejected.");
    TestAssert.Assert((bool)isNginxProcessName.Invoke(null, ["nginx: master process /usr/sbin/nginx"])!, "The Linux Nginx master-process name was rejected.");
    TestAssert.Assert((bool)isNginxProcessName.Invoke(null, ["nginx: worker process"])!, "The Linux Nginx worker-process name was rejected.");
    TestAssert.Assert(!(bool)isNginxProcessName.Invoke(null, ["nginx-helper"])!, "An unrelated process name was accepted as Nginx.");

    var multiPortSite = new WebServerSiteDto("multi-port", "nginx-test", "multi-port",
        [new WebServerSiteBindingDto("app.example.test", 5000), new WebServerSiteBindingDto("admin.example.test", 6000)],
        "/srv/relaxkonos-sites/multi-port", true, [new WebServerProxyRouteDto("/api/", "http://127.0.0.1:5090")], null, false, false, false, DateTimeOffset.UtcNow);
    TestAssert.Assert(multiPortSite.DomainsDisplay == "app.example.test:5000, admin.example.test:6000", "Multi-port bindings were not formatted for the site table.");
    var renderSite = typeof(NginxWebServerManager).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderSiteConfiguration" && method.GetParameters().Length == 2);
    var rendered = (string)renderSite.Invoke(null, [multiPortSite, null])!;
    TestAssert.Assert(rendered.Split("server {", StringSplitOptions.None).Length == 2 && rendered.Contains("listen 5000;") && rendered.Contains("listen 6000;")
        && rendered.Contains("server_name app.example.test admin.example.test;"), "A multi-port site was not rendered as one Nginx server with all listeners and names.");
    var proxySite = multiPortSite with { Id = "proxy-site", RootPath = null, Routes = [new WebServerProxyRouteDto("/", "http://127.0.0.1:5090")] };
    var renderedProxy = (string)renderSite.Invoke(null, [proxySite, null])!;
    TestAssert.Assert(renderedProxy.Contains("proxy_http_version 1.1;")
        && renderedProxy.Contains("proxy_set_header Upgrade $http_upgrade;")
        && renderedProxy.Contains("proxy_set_header Connection \"upgrade\";"), "A reverse-proxy site did not preserve WebSocket upgrades for SignalR.");
    var integratedSite = multiPortSite with
    {
        Id = "relaxkon",
        Bindings = [new WebServerSiteBindingDto("relaxkon.com", 80), new WebServerSiteBindingDto("www.relaxkon.com", 80), new WebServerSiteBindingDto("downloads.relaxkon.com", 80)],
        RootPath = "/srv/relaxkon/frontend/browser",
        SpaFallback = true,
        Routes = [
            new WebServerProxyRouteDto("/api/", "http://127.0.0.1:5062"),
            new WebServerProxyRouteDto("/relaxkonos/", "http://127.0.0.1:5062", true),
            new WebServerProxyRouteDto("/apt/", "http://127.0.0.1:5062", true),
        ],
        HttpsEnabled = true,
        RedirectHttpToHttps = true,
        Ipv6Enabled = true,
    };
    (string FullChainPath, string PrivateKeyPath)? integrationCertificate = ("/etc/letsencrypt/live/relaxkon.com/fullchain.pem", "/etc/letsencrypt/live/relaxkon.com/privkey.pem");
    var renderedIntegrated = (string)renderSite.Invoke(null, [integratedSite, integrationCertificate])!;
    var expectedSiteRoot = Path.GetFullPath("/srv/relaxkon/frontend/browser").Replace('\\', '/');
    TestAssert.Assert(renderedIntegrated.Contains("return 301 https://$host$request_uri;")
        && renderedIntegrated.Contains("listen [::]:80;")
        && renderedIntegrated.Contains("listen 443 ssl http2;")
        && renderedIntegrated.Contains("listen [::]:443 ssl http2;")
        && renderedIntegrated.Contains("root " + expectedSiteRoot + ";", StringComparison.Ordinal)
        && renderedIntegrated.Contains("try_files $uri $uri/ /index.html;")
        && renderedIntegrated.Contains("location ^~ /api/")
        && renderedIntegrated.Contains("location ^~ /relaxkonos/")
        && renderedIntegrated.Contains("proxy_request_buffering off;")
        && renderedIntegrated.Contains("proxy_buffering off;"), "A combined static-and-proxy site did not render its HTTPS, SPA, routing, and download settings.");
    var configurationTestProblem = typeof(NginxWebServerManager).GetMethod("ConfigurationTestProblem", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx configuration-test problem classifier was not found.");
    TestAssert.Assert((string)configurationTestProblem.Invoke(null, ["[emerg] host not found in upstream \"locahost\""])! == "webserver.site_upstream_unresolvable",
        "An unresolvable reverse-proxy upstream was not given a specific error.");
    TestAssert.Assert((string)configurationTestProblem.Invoke(null, ["[emerg] unexpected \"}\""])! == "webserver.site_config_test_failed",
        "An unrelated Nginx configuration error was misclassified as an upstream-resolution error.");
    var renderWithAcme = typeof(NginxWebServerManager).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderSiteConfiguration" && method.GetParameters().Length == 3);
    var renderedWithAcme = (string)renderWithAcme.Invoke(null, [proxySite, null, "/var/lib/relaxkonos/acme-challenge"])!;
    var expectedAcmeRoot = Path.GetFullPath("/var/lib/relaxkonos/acme-challenge").Replace('\\', '/');
    TestAssert.Assert(renderedWithAcme.Contains("location ^~ /.well-known/acme-challenge/")
        && renderedWithAcme.Contains("alias " + expectedAcmeRoot + "/;", StringComparison.Ordinal)
        && renderedWithAcme.Contains("location ^~ / {"), "ACME HTTP-01 routing was not rendered ahead of the site location.");

    var findRoutingConflict = typeof(NginxWebServerManager).GetMethod("FindRoutingConflict", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx site conflict detector was not found.");
    var existingSite = new WebServerSiteDto("existing", "nginx-test", "existing",
        [new WebServerSiteBindingDto("app.example.test", 5000)], "/srv/relaxkonos-sites/existing", false, [], null, false, false, false, DateTimeOffset.UtcNow);
    var conflictingSite = new WebServerSiteDto("new-site", "nginx-test", "new-site",
        [new WebServerSiteBindingDto("app.example.test", 5000)], "/srv/relaxkonos-sites/new-site", false, [], null, false, false, false, DateTimeOffset.UtcNow);
    TestAssert.Assert(findRoutingConflict.Invoke(null, [new[] { existingSite }, conflictingSite]) is not null, "Duplicate domain and port bindings were not rejected.");
    var tlsSite = existingSite with { Id = "tls-site", HttpsEnabled = true };
    var port443Site = conflictingSite with { Id = "port-443-site", Bindings = [new WebServerSiteBindingDto("app.example.test", 443)] };
    TestAssert.Assert(findRoutingConflict.Invoke(null, [new[] { tlsSite }, port443Site]) is not null, "The implicit HTTPS listener was not checked for conflicts.");
    var crossProductSite = conflictingSite with { Id = "cross-product-site", Bindings = [new WebServerSiteBindingDto("other.example.test", 5000), new WebServerSiteBindingDto("app.example.test", 6000)] };
    TestAssert.Assert(findRoutingConflict.Invoke(null, [new[] { existingSite }, crossProductSite]) is not null, "All site names were not checked against every configured listener.");
}

internal static async Task VerifyOperationIdempotencyAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var options = new CertificateOptions { StorageRoot = Path.Combine(root, "operation-certificates") };
    var certificates = new FileCertificateStore(environment, options, new CertificateMetadataRepository(environment, configuration));
    var retries = new CertificateRenewalAttemptRepository(environment, configuration, options);
    var journal = new HostOperationJournal(environment, configuration);
    var certificateOperations = new CertificateOperationStore(environment, journal, certificates, retries, NullLogger<CertificateOperationStore>.Instance);
    var certificateId = Guid.NewGuid();
    var first = await certificateOperations.StartAsync("same-key", certificateId, "issue", "test", _ => Task.FromResult(""), CancellationToken.None);
    var duplicate = await certificateOperations.StartAsync("same-key", certificateId, "issue", "test", _ => Task.FromResult("certificate.should_not_run"), CancellationToken.None);
    TestAssert.Assert(first.OperationId == duplicate.OperationId, "Certificate idempotency key created duplicate work.");
    TestAssert.Assert((await TestOperations.WaitForCertificateOperationAsync(certificateOperations, first.OperationId)).State == CertificateOperationState.Succeeded, "Certificate operation did not complete.");

    var webOperations = new WebServerOperationStore(environment, journal, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebServerOperationStore>.Instance);
    var webFirst = await webOperations.StartAsync("same-key", "nginx-test", "reload", "test", _ => Task.FromResult(WebServerOperationResult.Success), CancellationToken.None);
    var webDuplicate = await webOperations.StartAsync("same-key", "nginx-test", "reload", "test", _ => Task.FromResult(new WebServerOperationResult("webserver.should_not_run")), CancellationToken.None);
    TestAssert.Assert(webFirst.OperationId == webDuplicate.OperationId, "WebServer idempotency key created duplicate work.");
    TestAssert.Assert((await TestOperations.WaitForWebOperationAsync(webOperations, webFirst.OperationId)).State == WebServerOperationState.Succeeded, "WebServer operation did not complete.");
}

internal static async Task VerifyWebServerProviderRoutingAsync()
{
    var provider = new FakeWebServerProvider();
    IWebServerManager manager = new WebServerManager([provider]);
    var discovered = await manager.DiscoverAsync(CancellationToken.None);
    TestAssert.Assert(discovered.Count == 1 && discovered[0].ProviderId == provider.ProviderId, "Web Server Manager did not aggregate provider discovery.");
    var candidates = await manager.ListIntegrationCandidatesAsync(CancellationToken.None);
    TestAssert.Assert(candidates.Single().Id == provider.Candidate.Id, "Web Server Manager did not aggregate integration candidates.");
    var integrated = await manager.IntegrateCandidateAsync(provider.Candidate.Id, "candidate-routing", new IntegrateWebServerRequest(true), "test", CancellationToken.None);
    TestAssert.Assert(integrated?.State == WebServerOperationState.Succeeded && provider.IntegratedCandidateId == provider.Candidate.Id,
        "Web Server Manager did not route candidate integration to its provider.");
    TestAssert.Assert(await manager.IntegrateCandidateAsync("unknown", "candidate-routing-unknown", new IntegrateWebServerRequest(true), "test", CancellationToken.None) is null,
        "Web Server Manager routed an unknown integration candidate.");
    var status = await manager.GetStatusAsync(provider.Instance.Id, CancellationToken.None);
    TestAssert.Assert(status?.RuntimeState == WebServerRuntimeState.Running, "Web Server Manager did not route the instance to its provider.");
    TestAssert.Assert(await manager.GetStatusAsync("unknown", CancellationToken.None) is null, "Web Server Manager routed an unknown instance.");
}

}
