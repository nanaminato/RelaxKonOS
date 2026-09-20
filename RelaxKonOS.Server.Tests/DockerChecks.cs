internal static class DockerChecks
{
internal static async Task VerifyDockerProxyAsync(string root)
{
    TestAssert.Assert(DockerProxyApiRoutes.Proxy == "/api/v1.0/docker/proxy", "The Docker proxy route moved away from the versioned public base.");

    // --- Value rules and credential masking -------------------------------------------------
    TestAssert.Assert(DockerProxyValidation.IsValidProxyUrl("http://127.0.0.1:7890")
        && DockerProxyValidation.IsValidProxyUrl("https://user:pass@proxy.example:8443"),
        "A valid proxy URL was rejected.");
    TestAssert.Assert(!DockerProxyValidation.IsValidProxyUrl("127.0.0.1:7890")
        && !DockerProxyValidation.IsValidProxyUrl("socks5://127.0.0.1:1080")
        && !DockerProxyValidation.IsValidProxyUrl("http://127.0.0.1:7890/?q=1")
        && !DockerProxyValidation.IsValidProxyUrl("http://127.0.0.1:7890/#fragment")
        && !DockerProxyValidation.IsValidProxyUrl("http://host\necho injected"),
        "A malformed or directive-injecting proxy URL was accepted.");
    TestAssert.Assert(DockerProxyValidation.IsValidBypassList("localhost,127.0.0.1,::1,.internal")
        && DockerProxyValidation.IsValidBypassList(string.Empty)
        && !DockerProxyValidation.IsValidBypassList("localhost, bad host"),
        "Bypass list validation did not separate a host list from a value with a space.");
    // The userinfo is removed once, and an '@' inside a path is not mistaken for one.
    TestAssert.Assert(DockerProxyValidation.MaskProxy("http://user:secret@proxy.example:8080") == "http://***@proxy.example:8080"
        && DockerProxyValidation.MaskProxy("http://proxy.example:8080") == "http://proxy.example:8080"
        && DockerProxyValidation.MaskProxy("http://proxy.example:8080/pa@th") == "http://proxy.example:8080/pa@th",
        "Proxy credentials were not masked, or an '@' in the path was mangled.");

    // A build runs inside a container, where the local machine's own address is that container, so
    // this case has to be recognised before the build layer can call itself usable.
    TestAssert.Assert(DockerProxyValidation.IsLoopbackUrl("http://127.0.0.1:3128")
        && DockerProxyValidation.IsLoopbackUrl("http://localhost:3128")
        && DockerProxyValidation.IsLoopbackUrl("http://[::1]:3128")
        && !DockerProxyValidation.IsLoopbackUrl("http://proxy.example:8080")
        && !DockerProxyValidation.IsLoopbackUrl("not-a-url"),
        "Loopback detection did not separate a local address from a reachable one.");

    // --- Build layer: a value-less build argument carries the proxy -------------------------
    var buildResolution = new DockerProxyResolution(true, DockerProxySource.Custom,
        "http://operator:s3cr3t@proxy.example:8080", "http://operator:s3cr3t@proxy.example:8080", "localhost",
        true, true, false, false, string.Empty, true, string.Empty);
    var buildArguments = DockerBuildProxy.ArgumentNames(buildResolution);
    TestAssert.Assert(buildArguments.Count == 12 && buildArguments[0] == "--build-arg"
        && buildArguments.Where((_, index) => index % 2 == 1)
            .SequenceEqual(["HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "NO_PROXY", "no_proxy"]),
        "The build layer did not request every spelling of the proxy build argument.");
    // The value-less form is the whole point: nothing that reaches a command line may carry a value,
    // because that is where a credential becomes readable to every local user.
    TestAssert.Assert(buildArguments.All(argument => !argument.Contains('=', StringComparison.Ordinal)),
        "A build argument carried its value, which would expose the proxy credential on a command line.");
    TestAssert.Assert(DockerBuildProxy.ArgumentNames(buildResolution with { NoProxy = string.Empty }).Count == 8,
        "An empty bypass list still requested a NO_PROXY build argument.");
    TestAssert.Assert(DockerBuildProxy.ArgumentNames(buildResolution with { Enabled = false }).Count == 0,
        "A disabled preference still requested build arguments.");

    // The environment half: it is the value source for the arguments above, and nothing on its own.
    var buildStartInfo = new System.Diagnostics.ProcessStartInfo("docker");
    DockerBuildProxy.ApplyToEnvironment(buildStartInfo, buildResolution);
    TestAssert.Assert(buildStartInfo.Environment["HTTP_PROXY"] == buildResolution.HttpProxy
        && buildStartInfo.Environment["http_proxy"] == buildResolution.HttpProxy
        && buildStartInfo.Environment["HTTPS_PROXY"] == buildResolution.HttpsProxy
        && buildStartInfo.Environment["https_proxy"] == buildResolution.HttpsProxy
        && buildStartInfo.Environment["NO_PROXY"] == "localhost"
        && buildStartInfo.Environment["no_proxy"] == "localhost",
        "The build layer did not place the resolved proxy on the docker child process.");
    var disabledStartInfo = new System.Diagnostics.ProcessStartInfo("docker");
    _ = disabledStartInfo.Environment.Remove("HTTP_PROXY");
    DockerBuildProxy.ApplyToEnvironment(disabledStartInfo, buildResolution with { Enabled = false });
    TestAssert.Assert(!disabledStartInfo.Environment.ContainsKey("HTTP_PROXY"),
        "A disabled preference still placed a proxy on the docker child process.");

    // --- Resolver: custom source and the HTTPS-reuses-HTTP fallback -------------------------
    var settings = new InMemoryDockerProxySettingsRepository();
    await settings.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.Custom, HttpProxy = "http://127.0.0.1:3128", HttpsProxy = string.Empty,
        ApplyToBuild = true, ApplyToEngine = true, ApplyToImageTags = true, ApplyToRuntimeDownloads = true,
    });
    var resolver = new DockerProxyResolver(settings, new StaticProxySettingsService());
    var custom = await resolver.ResolveAsync();
    TestAssert.Assert(custom.IsUsable && custom.HttpProxy == "http://127.0.0.1:3128" && custom.HttpsProxy == "http://127.0.0.1:3128"
        && custom.BuildLayerActive && custom.EngineLayerRequested && custom.ImageTagsActive && custom.RuntimeDownloadsActive,
        "A custom proxy did not resolve, or an empty HTTPS value was not replaced by the HTTP one.");
    TestAssert.Assert(custom.ManagedProxyEndpoint == "http://127.0.0.1:7890",
        "The managed proxy endpoint was not advertised for one-click selection.");

    await settings.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.Custom, HttpProxy = "not-a-url", HttpsProxy = string.Empty,
        ApplyToBuild = true, ApplyToEngine = true,
    });
    resolver.Invalidate();
    var invalidStored = await resolver.ResolveAsync();
    TestAssert.Assert(!invalidStored.IsUsable && !invalidStored.BuildLayerActive && invalidStored.ProblemCode == DockerProxyProblem.ConfigurationInvalid,
        "An invalid stored proxy was not reported as a configuration problem.");

    // --- Resolver: managed source probes the runtime listener --------------------------------
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var managedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var managedSettings = new InMemoryDockerProxySettingsRepository();
    await managedSettings.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.ManagedProxy, ApplyToBuild = true, ApplyToEngine = true,
    });
    var managed = await new DockerProxyResolver(managedSettings, new TestProxySettingsService(managedPort)).ResolveAsync();
    TestAssert.Assert(managed.IsUsable && managed.ManagedProxyAvailable && managed.HttpProxy == $"http://127.0.0.1:{managedPort}"
        && managed.HttpsProxy == managed.HttpProxy,
        "The managed proxy source did not resolve to the runtime's listener.");
    listener.Stop();

    var unavailable = await new DockerProxyResolver(managedSettings, new TestProxySettingsService(ReserveUnusedPort())).ResolveAsync();
    TestAssert.Assert(!unavailable.IsUsable && !unavailable.BuildLayerActive && !unavailable.EngineLayerRequested
        && unavailable.ProblemCode == DockerProxyProblem.ManagedProxyUnavailable,
        "A managed proxy with no listener was treated as usable instead of failing closed.");

    // --- Storage: protected at rest, exactly one row -----------------------------------------
    var databasePath = Path.Combine(root, "docker-proxy.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var repository = new SqliteDockerProxySettingsRepository(new TestHostEnvironment(root),
        Options.Create(new StorageOptions { DatabasePath = databasePath }),
        DataProtectionProvider.Create(Path.Combine(root, "docker-proxy-keys")));
    const string secret = "http://operator:s3cr3t@proxy.example:8080";
    await repository.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.Custom, HttpProxy = secret, HttpsProxy = secret, NoProxy = "localhost",
        ApplyToEngine = true, ApplyToBuild = true, ApplyToImageTags = true, ApplyToRuntimeDownloads = true, EngineApplied = true,
        UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = Guid.NewGuid().ToString("D"),
    });
    var reloaded = await repository.GetAsync();
    TestAssert.Assert(reloaded?.HttpProxy == secret && reloaded.EngineApplied && reloaded.NoProxy == "localhost"
        && reloaded.ApplyToImageTags && reloaded.ApplyToRuntimeDownloads,
        "The Docker proxy preference was not persisted, or its protected URL could not be recovered.");
    await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT http_proxy FROM docker_proxy_settings WHERE settings_id=1;";
        var atRest = (string?)await command.ExecuteScalarAsync();
        TestAssert.Assert(atRest is not null && !atRest.Contains("s3cr3t", StringComparison.Ordinal),
            "The Docker proxy credential was stored in plaintext.");
    }
    await repository.SaveAsync(new DockerProxySetting
    {
        Enabled = false, Source = DockerProxySource.Custom, HttpProxy = string.Empty, HttpsProxy = string.Empty,
        NoProxy = string.Empty, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test",
    });
    await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM docker_proxy_settings;";
        TestAssert.Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1,
            "Saving the Docker proxy preference twice created a second row.");
    }

    // --- Service: rejection, confirmation, layer reporting, masked diagnostics ---------------
    var serviceSettings = new InMemoryDockerProxySettingsRepository();
    var serviceResolver = new DockerProxyResolver(serviceSettings, new StaticProxySettingsService());
    var daemon = DispatchProxy.Create<IDockerEngineService, StubDockerEngine>();
    var stubbedDaemon = (StubDockerEngine)(object)daemon;
    stubbedDaemon.State = null;
    var fixture = CreateDockerProxyService(serviceSettings, serviceResolver, daemon);
    var actor = Guid.NewGuid();

    var rejected = await CaptureAsync<DockerProxyValidationException>(() =>
        fixture.Service.SaveAsync(new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, "nope", null, null, true, true, true), actor),
        "An invalid proxy URL was accepted instead of being rejected.");
    TestAssert.Assert(rejected.ProblemCode == DockerProxyProblem.ConfigurationInvalid,
        "A rejected proxy preference did not report the configuration problem code.");
    TestAssert.Assert(await serviceSettings.GetAsync() is null, "A rejected proxy preference was still persisted.");

    // Without confirmation the daemon layer is not written, but the build layer still reports.
    var unconfirmed = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost", true, true, Confirmed: false), actor);
    TestAssert.Assert(unconfirmed.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.Failed, ProblemCode: DockerProxyProblem.ConfirmationRequired }
        && unconfirmed.Layers.Single(layer => layer.Target == DockerProxyTarget.Build) is
            { State: DockerProxyLayerState.Applied, Detail: DockerProxyDetail.BuildOnly }
        && fixture.Configurator.Requests.Count == 0,
        "Saving without confirmation wrote the daemon layer, or the build layer was misreported.");

    // Written but not yet live: the daemon still reports no proxy.
    stubbedDaemon.State = new DockerEngineProxyState(string.Empty, string.Empty, string.Empty);
    var written = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost", true, true, Confirmed: true), actor);
    TestAssert.Assert(fixture.Configurator.Requests.Count == 1 && fixture.Configurator.Requests[0]
        && written.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.RestartRequired, Detail: DockerProxyDetail.RestartPending },
        "A confirmed save did not install the daemon layer, or did not report it as awaiting a restart.");

    // Live: the daemon now reports the proxy. The operator reads every reported value verbatim,
    // because a masked echo would be written back as the mask on the next save; keeping the
    // credential away from the wider audience of a log or an audit record stays MaskProxy's job.
    stubbedDaemon.State = new DockerEngineProxyState(secret, secret, "localhost");
    var applied = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost", true, true, Confirmed: true), actor);
    TestAssert.Assert(applied.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.Applied,
        "A daemon that reports a proxy was not recognised as the layer being live.");
    TestAssert.Assert(applied.Settings.HttpProxy == secret, "The saved preference is no longer returned to its owner for the form to round-trip.");
    TestAssert.Assert(applied.EffectiveHttpProxy == secret && applied.EffectiveHttpsProxy == secret && applied.EffectiveNoProxy == "localhost",
        "The daemon-reported proxy was not returned verbatim to the operator who owns the setting.");
    TestAssert.Assert(applied.DesktopProxy is null, "A host without Docker Desktop reported a Docker Desktop proxy.");
    TestAssert.Assert(!applied.Layers.Any(layer => layer.Detail.Contains("s3cr3t", StringComparison.Ordinal)
        || layer.ProblemCode.Contains("s3cr3t", StringComparison.Ordinal)),
        "A layer diagnostic contained the proxy credential.");

    // Replacing an already-installed proxy without confirmation must leave enough state for Clear
    // to retire the old daemon setting. Otherwise the old proxy remains active indefinitely.
    var replacementUnconfirmed = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, "http://replacement:8080", null, "localhost", true, true, Confirmed: false), actor);
    TestAssert.Assert(replacementUnconfirmed.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).ProblemCode == DockerProxyProblem.ConfirmationRequired
        && fixture.Configurator.Requests.Count == 2,
        "An unconfirmed replacement changed the daemon layer instead of preserving it for explicit removal.");

    // --- Docker Desktop: the stored upstream decides the layer, not the internal relay ---------
    var desktopPath = Path.Combine(root, "settings-store.json");
    await File.WriteAllTextAsync(desktopPath,
        """{ "ProxyHTTPMode": "manual", "OverrideProxyHTTP": "http://desktop:8080", "OverrideProxyHTTPS": "http://desktop:8080", "OverrideProxyExclude": "internal", "Unrelated": 7 }""");
    var desktopRead = DockerDesktopProxySettings.TryRead(desktopPath);
    TestAssert.Assert(desktopRead is { IsManual: true } && desktopRead.HttpProxy == "http://desktop:8080"
        && desktopRead.NoProxy == "internal" && desktopRead.SettingsPath == desktopPath,
        "The Docker Desktop settings file was not read back into its proxy values.");
    TestAssert.Assert(DockerDesktopProxySettings.TryRead(Path.Combine(root, "absent-settings.json")) is null,
        "A missing Docker Desktop settings file was reported as a configured proxy.");

    // Docker Desktop always reports its own internal relay, so a non-empty daemon proxy proves only
    // that the relay exists. The stored upstream is the only value that can confirm this layer.
    var desktopDaemon = DispatchProxy.Create<IDockerEngineService, StubDockerEngine>();
    ((StubDockerEngine)(object)desktopDaemon).State =
        new DockerEngineProxyState("http://http.docker.internal:3128", "http://http.docker.internal:3128", "hubproxy.docker.internal");
    var desktopSettings = new InMemoryDockerProxySettingsRepository();
    var desktopReader = new StaticDockerDesktopProxyReader(new DockerDesktopProxyDto("manual", "http://other:9999", "http://other:9999", string.Empty, desktopPath));
    var desktopFixture = CreateDockerProxyService(desktopSettings, new DockerProxyResolver(desktopSettings, new StaticProxySettingsService()),
        desktopDaemon, desktopProxy: desktopReader);

    await desktopFixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, "http://desktop:8080", null, "internal", true, true, Confirmed: true), actor);
    var drifted = await desktopFixture.Service.GetStatusAsync();
    TestAssert.Assert(drifted.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.RestartRequired, Detail: DockerProxyDetail.DesktopUpstreamDiffers },
        "A Docker Desktop host forwarding to another upstream was reported as applied.");

    desktopReader.Snapshot = new DockerDesktopProxyDto("manual", "http://desktop:8080", "http://desktop:8080", "internal", desktopPath);
    var converged = await desktopFixture.Service.GetStatusAsync();
    TestAssert.Assert(converged.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.Applied
        && converged.DesktopProxy is { IsManual: true, HttpProxy: "http://desktop:8080" },
        "A Docker Desktop host whose stored upstream matches the preference was not reported as applied.");

    desktopReader.Snapshot = new DockerDesktopProxyDto("system", string.Empty, string.Empty, string.Empty, desktopPath);
    var systemMode = await desktopFixture.Service.GetStatusAsync();
    TestAssert.Assert(systemMode.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.RestartRequired, Detail: DockerProxyDetail.DesktopRestartPending },
        "A Docker Desktop host still in system proxy mode was reported as applied.");

    // Platform without a daemon mechanism: reported, never written.
    var unsupported = CreateDockerProxyService(serviceSettings, serviceResolver, daemon, supported: false);
    var unsupportedStatus = await unsupported.Service.GetStatusAsync();
    TestAssert.Assert(unsupportedStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.Unsupported
        && unsupportedStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).ProblemCode == DockerProxyProblem.PlatformUnsupported,
        "A host with no daemon mechanism did not report the engine layer as unsupported.");

    // --- Service: clearing retires the daemon layer, and only if we installed one -------------
    var cleared = await fixture.Service.ClearAsync(actor);
    TestAssert.Assert(await serviceSettings.GetAsync() is null && fixture.Configurator.Requests.Count == 3 && !fixture.Configurator.Requests[^1]
        && cleared.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.RestartRequired,
        "Clearing did not retire the daemon layer that RelaxKonOS had installed.");

    var freshSettings = new InMemoryDockerProxySettingsRepository();
    var untouched = CreateDockerProxyService(freshSettings, new DockerProxyResolver(freshSettings, new StaticProxySettingsService()),
        DispatchProxy.Create<IDockerEngineService, StubDockerEngine>());
    await untouched.Service.ClearAsync(actor);
    TestAssert.Assert(untouched.Configurator.Requests.Count == 0,
        "Clearing a preference that was never installed on the host restarted Docker for nothing.");

    // --- An unusable preference is reported, and the host is left exactly as it was ----------
    var brokenSettings = new InMemoryDockerProxySettingsRepository();
    var brokenPort = ReserveUnusedPort();
    var broken = CreateDockerProxyService(brokenSettings, new DockerProxyResolver(brokenSettings, new TestProxySettingsService(brokenPort)),
        DispatchProxy.Create<IDockerEngineService, StubDockerEngine>());
    var brokenStatus = await broken.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.ManagedProxy, null, null, null, true, true, Confirmed: true), actor);
    TestAssert.Assert(brokenStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Build).ProblemCode == DockerProxyProblem.ManagedProxyUnavailable
        && brokenStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.Failed, ProblemCode: DockerProxyProblem.ManagedProxyUnavailable }
        && broken.Configurator.Requests.Count == 0,
        "A managed proxy with no listener was written to the daemon instead of failing closed.");
    TestAssert.Assert(brokenStatus.ManagedProxyEndpoint == $"http://127.0.0.1:{brokenPort}",
        "The unusable managed proxy endpoint was not advertised so the operator can find it.");

    // --- Build layer: a local address is applied, but reported with the warning it deserves ----
    using var buildListener = new TcpListener(IPAddress.Loopback, 0);
    buildListener.Start();
    var buildSettings = new InMemoryDockerProxySettingsRepository();
    var buildFixture = CreateDockerProxyService(buildSettings,
        new DockerProxyResolver(buildSettings, new TestProxySettingsService(((IPEndPoint)buildListener.LocalEndpoint).Port)),
        DispatchProxy.Create<IDockerEngineService, StubDockerEngine>());
    var loopbackBuild = await buildFixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.ManagedProxy, null, null, "localhost",
            ApplyToEngine: false, ApplyToBuild: true, Confirmed: true), actor);
    TestAssert.Assert(loopbackBuild.Layers.Single(layer => layer.Target == DockerProxyTarget.Build) is
            { State: DockerProxyLayerState.Applied, Detail: DockerProxyDetail.BuildLoopbackUnreachable },
        "A build proxy on the local machine was reported as a plain success instead of warning that a build container cannot reach it.");
    buildListener.Stop();

    Console.WriteLine("PASS DOCKER PROXY: value rules, credential visibility, build-argument delivery, "
        + "Docker Desktop upstream read, per-layer reporting, protected storage, and fail-closed behaviour verified.");
}

/// <summary>A port that was allocated and released, so nothing is listening on it right now.</summary>
internal static int ReserveUnusedPort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

internal static async Task<T> CaptureAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException(message);
}

/// <summary>
/// Builds a proxy service around a recording configurator, so a test can prove that the host was
/// not touched without needing a real daemon-side mechanism.
/// </summary>
internal static DockerProxyServiceFixture CreateDockerProxyService(IDockerProxySettingsRepository settings, IDockerProxyResolver resolver,
    IDockerEngineService engine, bool supported = true, IDockerDesktopProxyReader? desktopProxy = null) =>
    new(settings, resolver, engine, supported, desktopProxy);

internal static async Task VerifyDockerEngineControlAsync(string root)
{
    TestAssert.Assert(DockerApiRoutes.EngineAction == "/api/v1.0/docker/engine/{action}",
        "The Docker engine action route moved away from the versioned public base.");
    TestAssert.Assert(DockerEngineActionRoutes.Segment(DockerEngineAction.Restart) == "restart"
        && DockerEngineActionRoutes.TryParse("stop", out var parsedStop) && parsedStop == DockerEngineAction.Stop
        && !DockerEngineActionRoutes.TryParse("delete", out _),
        "The engine action segment table no longer maps exactly the supported lifecycle actions.");

    var daemon = DispatchProxy.Create<IDockerEngineService, StubDockerEngine>();
    var controller = new RecordingDockerEngineHostController();
    var service = new DockerEngineControlService(controller, daemon, NullLogger<DockerEngineControlService>.Instance);

    // An unknown action must not reach the host at all.
    var invalid = await service.ApplyAsync("delete", confirmed: true);
    TestAssert.Assert(!invalid.Success && invalid.ProblemCode == DockerEngineProblem.InvalidAction
        && invalid.Status is null && controller.Requests.Count == 0,
        "An unsupported engine action reached the host instead of being rejected.");

    // Stop and restart terminate every running container, so neither happens without confirmation.
    foreach (var action in new[] { "stop", "restart" })
    {
        var unconfirmed = await service.ApplyAsync(action, confirmed: false);
        TestAssert.Assert(!unconfirmed.Success && unconfirmed.ProblemCode == DockerEngineProblem.ConfirmationRequired,
            $"An unconfirmed '{action}' was accepted.");
    }
    TestAssert.Assert(controller.Requests.Count == 0, "An unconfirmed engine action was executed on the host.");

    // Start interrupts nothing, so it needs no confirmation and is passed through unchanged.
    var started = await service.ApplyAsync("start", confirmed: false);
    TestAssert.Assert(started.Success && started.Status is { IsAvailable: true } && controller.Requests.SequenceEqual([DockerEngineAction.Start]),
        "Starting the engine was blocked, mis-mapped, or reported without the engine state.");

    // A host failure is reported with its own code, and the engine state is still reported.
    controller.Succeed = false;
    var failed = await service.ApplyAsync("restart", confirmed: true);
    TestAssert.Assert(!failed.Success && failed.ProblemCode == DockerEngineProblem.ActionFailed && failed.Status is not null
        && controller.Requests[^1] == DockerEngineAction.Restart,
        "A failed engine command did not report its problem code, the engine state, or the wrong action.");

    // A platform without a mechanism never touches a host command.
    controller.IsSupported = false;
    var unsupported = await service.ApplyAsync("start", confirmed: false);
    TestAssert.Assert(!unsupported.Success && unsupported.ProblemCode == DockerEngineProblem.PlatformUnsupported
        && controller.Requests.Count == 2,
        "A platform without an engine mechanism still ran a host command.");

    Console.WriteLine("PASS DOCKER ENGINE: lifecycle route, action mapping, confirmation, host dispatch, and outcome reporting verified.");
}

}
