using System.Collections.Concurrent;
using RelaxKonOS.Server.EventAlerts;

internal static class DockerChecks
{
    internal static void VerifyComposeSubsetValidation()
    {
        const string supported = "services:\n  web:\n    image: nginx:alpine\n    volumes:\n      - site-data:/usr/share/nginx/html\nvolumes:\n  site-data:\nnetworks:\n  default:\n";
        TestAssert.Assert(DockerComposeSubsetValidation.IsSupported(supported, out var accepted) && accepted.Length == 0,
            "The Android Compose subset rejected an image service with a named volume.");

        // `$$` is Compose's escape for a literal dollar and a comment is never interpolated (Compose
        // substitutes the parsed document, not the raw text), so neither is a variable reference.
        foreach (var escaped in new[]
        {
            "services:\n  web:\n    image: nginx\n    command: [\"sh\", \"-c\", \"echo $$HOME\"]\n",
            "services:\n  web:\n    image: nginx\n# TOKEN: ${AD04_COMMENT_VAR}\n",
        })
        {
            TestAssert.Assert(DockerComposeSubsetValidation.IsSupported(escaped, out var escapeProblem) && escapeProblem.Length == 0,
                "The Android Compose subset rejected an escaped dollar or a comment as an unresolved variable.");
        }

        foreach (var rejected in new[]
        {
            "services:\n  web:\n    build: .\n",
            "services:\n  web:\n    image: nginx\n    privileged: true\n",
            "services:\n  web:\n    image: nginx\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n",
            "services:\n  web:\n    image: nginx\n    volumes:\n      - ./site:/usr/share/nginx/html\n",
            "services:\n  web:\n    image: nginx\n    volumes:\n      - type: bind\n        source: site\n        target: /usr/share/nginx/html\n",
            "volumes:\n  shared:\n    external: true\n",
        })
        {
            TestAssert.Assert(!DockerComposeSubsetValidation.IsSupported(rejected, out var problem)
                && problem == DockerComposeSubsetValidation.UnsupportedFeature,
                "The Android Compose subset accepted an unsupported or dangerous source feature.");
        }

        // `docker compose config` substitutes an unset variable with an empty string and still exits 0,
        // so admitting any of these would deploy a project built from values the operator never saw.
        // The `$$` escape and a trailing quoted `#` are what separate a real reference from literal text.
        foreach (var rejected in new[]
        {
            "services:\n  web:\n    image: nginx\n    environment:\n      TOKEN: ${AD04_UNSET}\n",
            "services:\n  web:\n    image: nginx\n    environment:\n      TOKEN: $AD04_UNSET\n",
            "services:\n  web:\n    image: nginx\n    environment:\n      TOKEN: ${AD04_UNSET:-fallback}\n",
            "services:\n  web:\n    image: nginx\n    environment:\n      TOKEN: ${AD04_UNSET:?required}\n",
            "services:\n  web:\n    image: nginx\n    container_name: \"web-${AD04_UNSET}\"\n",
            "services:\n  web:\n    image: nginx\n    labels:\n      note: \"before#${AD04_UNSET}\"\n",
        })
        {
            TestAssert.Assert(!DockerComposeSubsetValidation.IsSupported(rejected, out var problem)
                && problem == DockerStackProblem.VariableUnresolved,
                "The Android Compose subset accepted a variable reference the server has no value for.");
        }
    }

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
        && DockerProxyValidation.IsValidBypassList("localhost;127.0.0.1;::1;.internal")
        && DockerProxyValidation.IsValidBypassList(string.Empty)
        && !DockerProxyValidation.IsValidBypassList("localhost, bad host"),
        "Bypass list validation did not separate a host list from a value with a space.");
    TestAssert.Assert(DockerProxyValidation.TryNormalizeBypassList("localhost; 127.0.0.1, ::1", out var normalizedBypass)
        && normalizedBypass == "localhost,127.0.0.1,::1",
        "Bypass-list separators were not normalized to Docker's comma-separated form.");
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

    var normalizedSave = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost;127.0.0.1,::1", false, false), actor);
    TestAssert.Assert(normalizedSave.Settings.NoProxy == "localhost,127.0.0.1,::1"
        && (await serviceSettings.GetAsync())?.NoProxy == "localhost,127.0.0.1,::1",
        "A Windows-style bypass list was not persisted in Docker's comma-separated form.");

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

/// <summary>
/// Verifies the durable Compose stack-operation contract: the wire routes, the value rules, the ledger
/// that survives a restart, idempotent replay, per-project exclusion, outcome classification over what
/// the Engine actually runs, and the startup reconciliation that observes instead of replaying.
/// </summary>
internal static async Task VerifyStackOperationsAsync(string root)
{
    // --- Wire contract: routes and the action table ---------------------------------------------
    TestAssert.Assert(DockerApiRoutes.StackPreview == "/api/v1.0/docker/stacks/preview"
        && DockerApiRoutes.StackDeploy == "/api/v1.0/docker/stacks/deploy"
        && DockerApiRoutes.StackOperations == "/api/v1.0/docker/stacks/{name}/operations"
        && DockerApiRoutes.StackActiveOperation == "/api/v1.0/docker/stacks/{name}/operations/active"
        && DockerApiRoutes.StackOperationById == "/api/v1.0/docker/stack-operations/{operationId}"
        && DockerApiRoutes.StackOperationDiagnostics == "/api/v1.0/docker/stack-operations/{operationId}/diagnostics"
        && DockerApiRoutes.StackOperationCancel == "/api/v1.0/docker/stack-operations/{operationId}/cancel",
        "A durable stack operation route moved away from the versioned public base.");

    var identifier = Guid.NewGuid();
    TestAssert.Assert(DockerApiRoutes.StackOperation(identifier) == $"/api/v1.0/docker/stack-operations/{identifier:D}",
        "The canonical read route of a stack operation is not built from its identifier.");

    // A deployment carries a definition, so it is not a lifecycle action: the action table must not
    // admit it, or a caller could reach the deployment path without ever submitting a document.
    TestAssert.Assert(DockerStackActionRoutes.Segment(DockerStackOperationKind.Deploy) == "deploy"
        && !DockerStackActionRoutes.TryParseAction("deploy", out _)
        && !DockerStackActionRoutes.TryParseAction("validate", out _)
        && !DockerStackActionRoutes.TryParseAction(null, out _),
        "The stack action table admitted a segment that is not a whole-project lifecycle verb.");
    foreach (var kind in new[]
    {
        DockerStackOperationKind.Start, DockerStackOperationKind.Stop,
        DockerStackOperationKind.Restart, DockerStackOperationKind.Delete,
    })
    {
        TestAssert.Assert(DockerStackActionRoutes.TryParseAction(DockerStackActionRoutes.Segment(kind), out var roundTripped)
            && roundTripped == kind,
            $"The action segment of {kind} did not round-trip through the shared table.");
    }

    // --- Value rules -----------------------------------------------------------------------------
    // The project name also becomes the prefix of every container, volume and network Docker creates,
    // so it stays narrow instead of being escaped later.
    TestAssert.Assert(DockerStackValidation.IsValidProjectName("a")
        && DockerStackValidation.IsValidProjectName("web_app-1")
        && !DockerStackValidation.IsValidProjectName(null)
        && !DockerStackValidation.IsValidProjectName(string.Empty)
        && !DockerStackValidation.IsValidProjectName(new string('a', 64))
        && !DockerStackValidation.IsValidProjectName("../escape")
        && !DockerStackValidation.IsValidProjectName("web app")
        && !DockerStackValidation.IsValidProjectName("web/app"),
        "A Compose project name was accepted outside the resources Docker would prefix with it.");

    const string source = "services:\n  web:\n    image: nginx:alpine\n";
    var version = DockerStackValidation.DefinitionVersion("web", source);
    TestAssert.Assert(version == DockerStackValidation.DefinitionVersion("web", source)
        && version != DockerStackValidation.DefinitionVersion("web", source + "  # edited\n")
        && version != DockerStackValidation.DefinitionVersion("other", source)
        && DockerStackValidation.IsValidReference(version),
        "The definition version did not identify exactly one approved source document.");

    TestAssert.Assert(DockerStackValidation.IsValidProblemCode(null)
        && DockerStackValidation.IsValidProblemCode("docker.stack_partial_failure")
        && !DockerStackValidation.IsValidProblemCode("docker.stack_partial_failure\nsecond line")
        && !DockerStackValidation.IsValidProblemCode(new string('c', 121))
        && !DockerStackValidation.IsValidReference(new string('a', 64)),
        "A problem code or a ledger reference was accepted outside its documented shape.");

    // --- Ledger: durability, replay, per-project exclusion ----------------------------------------
    var ledgerRoot = Path.Combine(root, "stack-ledger");
    var actor = Guid.NewGuid().ToString("D");
    var other = Guid.NewGuid().ToString("D");
    var store = NewStackStore(ledgerRoot, 16);

    var request = DockerStackValidation.Reference("deploy|shop|v1");
    var created = store.Create("shop", DockerStackOperationKind.Deploy, actor, "key-1", request, out var wasCreated);
    TestAssert.Assert(wasCreated && DockerStackOperationStore.Active(created.Operation) && created.Operation.Cancellable
        && created.Operation.ProblemCode is null && created.Operation.Services.Count == 0,
        "A submitted deployment was not recorded as an active, cancellable operation with no claimed outcome.");

    // The actor is stored hashed: a ledger that names identities is a ledger that leaks them.
    TestAssert.Assert(created.ActorReference.Length == 64 && !created.ActorReference.Contains(actor, StringComparison.OrdinalIgnoreCase)
        && created.IdempotencyReference != DockerStackValidation.Reference("key-1"),
        "The operation ledger stored an actor or an idempotency key in a recoverable form.");

    var replay = store.Create("shop", DockerStackOperationKind.Deploy, actor, "key-1", request, out var replayedCreated);
    TestAssert.Assert(!replayedCreated && replay.Operation.OperationId == created.Operation.OperationId,
        "A retried submission created a second operation instead of returning the first.");

    // The same key with a different request is a conflict, not a replay: otherwise an approval given
    // for one document would be silently applied to another.
    var keyConflict = CaptureStackFailure(() => store.Create("shop", DockerStackOperationKind.Deploy, actor, "key-1",
        DockerStackValidation.Reference("deploy|shop|v2"), out _),
        "Reusing an idempotency key for a different request was accepted.");
    TestAssert.Assert(keyConflict.ProblemCode == DockerStackProblem.IdempotencyConflict,
        "A reused idempotency key did not report a conflict.");

    var projectConflict = CaptureStackFailure(() => store.Create("shop", DockerStackOperationKind.Stop, actor, "key-2",
        DockerStackValidation.Reference("stop|shop|False"), out _),
        "A second change to a project with an active operation was queued.");
    TestAssert.Assert(projectConflict.ProblemCode == DockerStackProblem.OperationConflict,
        "A concurrent change to one project did not report an operation conflict.");

    // Exclusion is per project, not global: an independent project keeps running beside it.
    var parallel = store.Create("warehouse", DockerStackOperationKind.Deploy, other, "key-3",
        DockerStackValidation.Reference("deploy|warehouse|v1"), out var parallelCreated);
    TestAssert.Assert(parallelCreated
        && store.GetActive("shop")?.Operation.OperationId == created.Operation.OperationId
        && store.GetActive("warehouse")?.Operation.OperationId == parallel.Operation.OperationId,
        "Per-project exclusion was applied globally, or an active operation could not be found.");

    // --- The record outlives the process ---------------------------------------------------------
    var reopened = NewStackStore(ledgerRoot, 16);
    TestAssert.Assert(reopened.Get(created.Operation.OperationId)?.Operation.ProjectName == "shop"
        && reopened.GetActive("shop")?.Operation.OperationId == created.Operation.OperationId,
        "The operation ledger did not survive a restart, so a reconnecting client would lose the record.");

    // Diagnostics are sanitized and bounded here, and a dropped head is recorded rather than implied.
    reopened.Update(created.Operation.OperationId, operation => operation with
    {
        State = DockerStackOperationState.Succeeded,
        Stage = DockerStackOperationStage.Completed,
        CompletedAt = DateTimeOffset.UtcNow,
        Cancellable = false,
    }, "completed", [.. Enumerable.Range(0, 200).Select(index => $"line {index}")]);
    TestAssert.Assert(!DockerStackOperationStore.Active(reopened.Get(created.Operation.OperationId)!.Operation),
        "A completed operation was still reported as active.");

    var diagnostics = reopened.Diagnostics(created.Operation.OperationId);
    TestAssert.Assert(diagnostics.Length == 120 && diagnostics[^1] == "line 199"
        && reopened.DiagnosticsTruncated(created.Operation.OperationId),
        "Bounded diagnostics dropped the tail instead of the head, or did not report the truncation.");

    // A terminal operation frees its project again, and the history stays newest-first.
    await Task.Delay(20);
    var second = reopened.Create("shop", DockerStackOperationKind.Start, actor, "key-4",
        DockerStackValidation.Reference("start|shop|False"), out var secondCreated);
    var shopHistory = reopened.History("shop", 10);
    TestAssert.Assert(secondCreated && shopHistory.Length == 2 && shopHistory[0].Operation.OperationId == second.Operation.OperationId,
        "A terminal operation still blocked its project, or the history is not newest-first.");
    TestAssert.Assert(reopened.History("warehouse", 1).Length == 1 && reopened.History("shop", 1).Length == 1,
        "The history ignored its limit or returned another project's operations.");

    // Sanitization and the per-line bound are independent of where a line sits in the log: binding the
    // bound to the line's index would silently truncate the head of every operation's output.
    reopened.Update(second.Operation.OperationId, operation => operation with
    {
        State = DockerStackOperationState.Succeeded,
        Stage = DockerStackOperationStage.Completed,
        CompletedAt = DateTimeOffset.UtcNow,
        Cancellable = false,
    }, "completed", ["auth-token: super-secret-value", new string('x', 900)]);
    var perLine = reopened.Diagnostics(second.Operation.OperationId);
    TestAssert.Assert(perLine.Length == 2
        && perLine[0].Contains("[REDACTED]", StringComparison.Ordinal)
        && !perLine[0].Contains("super-secret-value", StringComparison.Ordinal)
        && perLine[1].Length == 513 && perLine[1].EndsWith('…'),
        $"Diagnostic lines were not sanitized and bounded per line: {string.Join(", ", perLine.Select(line => line.Length.ToString()))}");

    // --- Coordinator: admission, idempotency, and outcome classification --------------------------
    var coordinatorRoot = Path.Combine(root, "stack-coordinator");
    var compose = new FakeComposeService();
    var events = new CaptureOperationalEvents();
    var coordinator = NewStackCoordinator(coordinatorRoot, compose, new TestApplicationLifetime(), 16, events);

    const string shopYaml = "services:\n  web:\n    image: nginx:alpine\n  db:\n    image: postgres:16-alpine\n";
    var deployKey = Guid.NewGuid().ToString("N");
    DockerStackDeployRequest Deploy(string project, string yaml) =>
        new(new DockerStackDefinitionDto(project, yaml), DockerStackValidation.DefinitionVersion(project, yaml));

    // Nothing is admitted before startup reconciliation has finished: an operation that is accepted
    // and then never run is worse than a refusal.
    var notReady = CaptureStackFailure(() => coordinator.Deploy(Deploy("shop", shopYaml), actor, deployKey),
        "An operation was admitted before the coordinator was ready.");
    TestAssert.Assert(notReady.ProblemCode == DockerStackProblem.StoreUnavailable,
        "A coordinator that had not reconciled yet did not refuse the submission.");

    // An approval is bound to one exact document.
    var edited = CaptureStackFailure(() => coordinator.Deploy(
        new DockerStackDeployRequest(new DockerStackDefinitionDto("shop", shopYaml),
            DockerStackValidation.DefinitionVersion("shop", shopYaml + "  # edited\n")), actor, Guid.NewGuid().ToString("N")),
        "A deployment whose source changed after the preview was accepted.");
    TestAssert.Assert(edited.ProblemCode == DockerStackProblem.DefinitionChanged,
        "A submission that no longer matched its approved definition was not refused.");

    // The import subset is enforced before anything is recorded, and nothing is silently rewritten.
    const string buildYaml = "services:\n  web:\n    build: .\n";
    var unsupported = CaptureStackFailure(() => coordinator.Deploy(Deploy("shop", buildYaml), actor, Guid.NewGuid().ToString("N")),
        "An unsupported Compose document was queued.");
    TestAssert.Assert(unsupported.ProblemCode == DockerStackProblem.FeatureUnsupported,
        "An unsupported Compose feature was not refused with the shared feature code.");

    await coordinator.StartAsync(CancellationToken.None);

    // A mutation without a usable idempotency key can never be replayed, so it is refused outright.
    foreach (var badKey in new[] { string.Empty, "   ", new string('k', 129), "key\nwith-newline" })
    {
        var missingKey = CaptureStackFailure(() => coordinator.Deploy(Deploy("shop", shopYaml), actor, badKey),
            "A submission without a usable idempotency key was admitted.");
        TestAssert.Assert(missingKey.ProblemCode == DockerStackProblem.IdempotencyRequired,
            "A missing or malformed idempotency key did not report the idempotency requirement.");
    }

    compose.PreviewServices = ["web", "db"];
    compose.Observed = [RunningService("web"), RunningService("db")];
    compose.NextResult = new DockerStackMutationResult(true, string.Empty, ["Container shop-web-1 Started"]);
    var submitted = coordinator.Deploy(Deploy("shop", shopYaml), actor, deployKey);
    TestAssert.Assert(submitted.State == DockerStackOperationState.Queued && submitted.Stage == DockerStackOperationStage.Queued,
        "A submitted deployment was not answered with its queued record.");
    var deployed = await AwaitTerminalOperationAsync(coordinator, submitted.OperationId);
    TestAssert.Assert(deployed.State == DockerStackOperationState.Succeeded && deployed.ProblemCode is null
        && deployed.RecoveryProblemCode is null && !deployed.Cancellable
        && deployed.Services.Select(service => service.Service).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(["db", "web"]),
        "A successful deployment was not classified over the services actually observed.");

    var replayedDeployment = coordinator.Deploy(Deploy("shop", shopYaml), actor, deployKey);
    TestAssert.Assert(replayedDeployment.OperationId == submitted.OperationId,
        "Replaying an idempotency key started a second deployment of the same project.");

    // The command succeeded but a service is not in its desired state: that is a partial failure, and
    // the operator has to choose between retrying, stopping, or removing the project.
    const string portalYaml = "services:\n  web:\n    image: nginx:alpine\n  cache:\n    image: redis:7-alpine\n";
    compose.PreviewServices = ["web", "cache"];
    compose.Observed = [RunningService("web"), StoppedService("cache")];
    compose.NextResult = new DockerStackMutationResult(true, string.Empty, ["cache failed to start"]);
    var partial = await AwaitTerminalOperationAsync(coordinator,
        coordinator.Deploy(Deploy("portal", portalYaml), actor, Guid.NewGuid().ToString("N")).OperationId);
    TestAssert.Assert(partial.State == DockerStackOperationState.PartialFailed
        && partial.ProblemCode == DockerStackProblem.PartialFailure
        && partial.RecoveryProblemCode is null
        && partial.Services.Any(service => service.Service == "cache" && service.State == "exited"),
        "A deployment that left a service down was reported as a success instead of a partial failure.");

    // A failure with nothing running is a plain failure; the same failure with containers left behind
    // is a partial one, because doing nothing is then not an option.
    const string brokenYaml = "services:\n  web:\n    image: nginx:alpine\n";
    compose.PreviewServices = ["web"];
    compose.Observed = [];
    compose.NextResult = new DockerStackMutationResult(false, DockerStackProblem.ComposeFailed, ["no such image"]);
    var broken = await AwaitTerminalOperationAsync(coordinator,
        coordinator.Deploy(Deploy("broken", brokenYaml), actor, Guid.NewGuid().ToString("N")).OperationId);
    TestAssert.Assert(broken.State == DockerStackOperationState.Failed
        && broken.ProblemCode == DockerStackProblem.ComposeFailed && broken.RecoveryProblemCode is null
        && broken.Services.Count == 0,
        "A failed deployment that left nothing running was not reported as a plain failure.");

    compose.Observed = [RunningService("web")];
    var halfApplied = await AwaitTerminalOperationAsync(coordinator,
        coordinator.Deploy(Deploy("half", brokenYaml), actor, Guid.NewGuid().ToString("N")).OperationId);
    TestAssert.Assert(halfApplied.State == DockerStackOperationState.PartialFailed
        && halfApplied.ProblemCode == DockerStackProblem.ComposeFailed
        && halfApplied.RecoveryProblemCode == DockerStackProblem.PartialFailure,
        "A failed deployment that left containers running did not report a partial failure with a recovery code.");

    // Removing a project needs an explicit confirmation, and the flag is part of the request identity.
    var unconfirmedDelete = CaptureStackFailure(() =>
        coordinator.ApplyAction("shop", DockerStackOperationKind.Delete, confirmed: false, actor, Guid.NewGuid().ToString("N")),
        "A project removal without confirmation was queued.");
    TestAssert.Assert(unconfirmedDelete.ProblemCode == DockerStackProblem.ConfirmationRequired,
        "An unconfirmed project removal did not report the confirmation requirement.");

    compose.Observed = [];
    compose.NextResult = new DockerStackMutationResult(true, string.Empty, ["removed"]);
    var removed = await AwaitTerminalOperationAsync(coordinator,
        coordinator.ApplyAction("shop", DockerStackOperationKind.Delete, confirmed: true, actor, Guid.NewGuid().ToString("N")).OperationId);
    TestAssert.Assert(removed.State == DockerStackOperationState.Succeeded && removed.Kind == DockerStackOperationKind.Delete
        && compose.AppliedActions.Contains(DockerStackOperationKind.Delete),
        "A confirmed project removal was not executed, or was not classified as a success.");

    // Stopping is verified against the running set, so a service that stayed up is a partial failure.
    compose.Observed = [RunningService("web"), StoppedService("db")];
    compose.NextResult = new DockerStackMutationResult(true, string.Empty, ["stopped"]);
    var stoppedLoudly = await AwaitTerminalOperationAsync(coordinator,
        coordinator.ApplyAction("legacy", DockerStackOperationKind.Stop, confirmed: false, actor, Guid.NewGuid().ToString("N")).OperationId);
    TestAssert.Assert(stoppedLoudly.State == DockerStackOperationState.PartialFailed
        && stoppedLoudly.ProblemCode == DockerStackProblem.PartialFailure && stoppedLoudly.RecoveryProblemCode is null,
        "A stop that left a service running was reported as a success.");

    compose.Observed = [StoppedService("web")];
    var stoppedQuietly = await AwaitTerminalOperationAsync(coordinator,
        coordinator.ApplyAction("legacy", DockerStackOperationKind.Stop, confirmed: false, actor, Guid.NewGuid().ToString("N")).OperationId);
    TestAssert.Assert(stoppedQuietly.State == DockerStackOperationState.Succeeded,
        "A stop that left nothing running was not reported as a success.");
    await events.WaitForAsync(stoppedQuietly.OperationId);
    var failedSignal = events.Signals.Single(signal => signal.OperationId == stoppedLoudly.OperationId);
    var recoveredSignal = events.Signals.Single(signal => signal.OperationId == stoppedQuietly.OperationId);
    TestAssert.Assert(failedSignal.Type == "docker.operation_failed" && !failedSignal.IsRecovery
        && failedSignal.ProblemCode == DockerStackProblem.PartialFailure
        && recoveredSignal.Type == failedSignal.Type && recoveredSignal.IsRecovery
        && recoveredSignal.ResourceId == failedSignal.ResourceId
        && recoveredSignal.ProblemCode == "docker.recovered"
        && failedSignal.Evidence is null && recoveredSignal.Evidence is null,
        "Compose failure and verified recovery were not published as a safe, deduplicated project alert.");
    TestAssert.Assert(events.Signals.Count(signal => signal.OperationId == submitted.OperationId) == 1,
        "An idempotent Compose replay published a second terminal event.");

    // --- Reads: history, diagnostics, and the refusals that go with them --------------------------
    var history = coordinator.History("portal", 10);
    TestAssert.Assert(history.Count == 1 && history[0].OperationId == partial.OperationId,
        "The project operation history did not return the durable records of exactly that project.");
    TestAssert.Assert(coordinator.GetActive("portal") is null && coordinator.GetActive("legacy") is null,
        "A terminal operation was still reported as the project's active one.");

    var recorded = coordinator.Diagnostics(partial.OperationId);
    TestAssert.Assert(recorded.Lines.Any(line => line.Contains("cache failed to start", StringComparison.Ordinal))
        && recorded.Lines.Any(line => line.Contains("not in their desired state", StringComparison.Ordinal)),
        $"The diagnostics of a partial failure did not carry the command output and the classification note: {string.Join(" | ", recorded.Lines)}");

    var invalidName = CaptureStackFailure(() => coordinator.History("no/such", 10),
        "An invalid project name was accepted by the history route.");
    TestAssert.Assert(invalidName.ProblemCode == DockerStackProblem.InvalidName,
        "An invalid project name did not report the name problem.");

    var absent = CaptureStackFailure(() => coordinator.Diagnostics(Guid.NewGuid()),
        "Diagnostics for an unknown operation were returned.");
    TestAssert.Assert(absent.ProblemCode == DockerStackProblem.OperationNotFound && absent.StatusCode == 404,
        "Diagnostics for an unknown operation did not report a missing operation.");

    // --- Cancellation: requested immediately, acknowledged by the worker that stops --------------
    const string cancelYaml = "services:\n  web:\n    image: nginx:alpine\n";
    compose.PreviewServices = ["web"];
    compose.Observed = [StoppedService("web")];
    compose.BlockOnDeploy = true;
    var cancellable = coordinator.Deploy(Deploy("cancelme", cancelYaml), actor, Guid.NewGuid().ToString("N"));
    await AwaitRunningOperationAsync(coordinator, cancellable.OperationId);
    var cancellation = coordinator.Cancel(cancellable.OperationId, Guid.NewGuid().ToString("N"));
    TestAssert.Assert(!cancellation.Cancellable && DockerStackOperationStore.Active(cancellation),
        "A cancellation request did not immediately mark the operation as no longer cancellable.");

    compose.BlockOnDeploy = false;
    compose.ReleaseDeploy();
    var cancelled = await AwaitTerminalOperationAsync(coordinator, cancellable.OperationId);
    TestAssert.Assert(cancelled.State == DockerStackOperationState.Cancelled
        && cancelled.ProblemCode == DockerStackProblem.Cancelled,
        "A cancelled operation did not end in the cancelled state.");
    TestAssert.Assert(events.Signals.All(signal => signal.OperationId != cancelled.OperationId),
        "A cancellation was published as a verified recovery or failure.");

    // --- Startup reconciliation: observed, never replayed ----------------------------------------
    var recoveryRoot = Path.Combine(root, "stack-recovery");
    var recoveryStore = NewStackStore(recoveryRoot, 16);
    var interrupted = recoveryStore.Create("leftover", DockerStackOperationKind.Deploy, actor, "key-r1",
        DockerStackValidation.Reference("deploy|leftover|v1"), out _).Operation;
    recoveryStore.Update(interrupted.OperationId, operation => operation with
    {
        State = DockerStackOperationState.Running,
        Stage = DockerStackOperationStage.Applying,
        StartedAt = DateTimeOffset.UtcNow,
    }, "started");
    // A queued change that never started is equally unverifiable, and must not claim observations.
    var neverStarted = recoveryStore.Create("waiting", DockerStackOperationKind.Deploy, actor, "key-r2",
        DockerStackValidation.Reference("deploy|waiting|v1"), out _).Operation;

    var recoveryCompose = new FakeComposeService { Observed = [RunningService("web")] };
    var recoveryCoordinator = NewStackCoordinator(recoveryRoot, recoveryCompose, new TestApplicationLifetime(), 16);
    await recoveryCoordinator.StartAsync(CancellationToken.None);

    var reconciled = recoveryCoordinator.Get(interrupted.OperationId)!;
    TestAssert.Assert(reconciled.State == DockerStackOperationState.Interrupted
        && reconciled.ProblemCode == DockerStackProblem.Interrupted
        && !reconciled.Cancellable
        && reconciled.Services.Any(service => service.Service == "web")
        && reconciled.RecoveryProblemCode is null,
        "A deployment interrupted by a restart was not reported as unverified over the services actually observed.");

    var reconciledNeverStarted = recoveryCoordinator.Get(neverStarted.OperationId)!;
    TestAssert.Assert(reconciledNeverStarted.State == DockerStackOperationState.Interrupted
        && reconciledNeverStarted.Services.Count == 0
        && reconciledNeverStarted.RecoveryProblemCode == DockerStackProblem.PartialFailure,
        "A queued operation that never started was reconciled into a claimed state, or without a recovery action.");

    // The whole point of reconciliation: nothing was replayed, and the only call was an observation.
    TestAssert.Assert(recoveryCompose.DeployCalls == 0 && recoveryCompose.AppliedActions.Count == 0
        && recoveryCompose.ListServicesCalls == 1,
        "A restart replayed an interrupted operation instead of only observing the project.");

    Console.WriteLine("PASS DOCKER STACK: durable ledger, idempotent replay, per-project exclusion, "
        + "partial-failure classification, cancellation, and restart reconciliation verified.");
}

/// <summary>
/// AD04-T1/T3/T5 on a real Compose host. <see cref="VerifyStackOperationsAsync" /> proves what the
/// coordinator decides once the Engine's answers are chosen for it; this proves that the commands those
/// decisions rest on behave that way — that <c>config --format json</c> really has the shape the parser
/// reads, that a service which exits is observed as not running while <c>up</c> still exits 0, and that
/// taking a project down leaves its named volume in place.
///
/// It is opt-in (<c>--stack-live</c>) and skips unless an Engine answers and its image is already on the
/// host, because a live check that quietly passes without Docker would be worse than no check at all.
/// </summary>
internal static async Task VerifyStackOperationsLiveAsync(string root)
{
    // One small image that can both idle and exit, so a single document can produce a service that
    // reaches its target state and one that does not.
    const string image = "alpine:3.20";
    const string variableDocument = "services:\n  web:\n    image: alpine:3.20\n    environment:\n      TOKEN: ${AD04_UNSET}\n";
    var dataDirectory = Path.Combine(root, "docker-stack-live", "compose");
    var ledgerDirectory = Path.Combine(root, "docker-stack-live", "ledger");
    Directory.CreateDirectory(dataDirectory);

    var engine = new DockerCliEngineService(new DockerCliEngineOptions(), new DisabledDockerProxyResolver(),
        NullLogger<DockerCliEngineService>.Instance);
    var status = await engine.GetStatusAsync();
    if (!status.IsAvailable)
    {
        Console.WriteLine($"SKIP DOCKER STACK LIVE: no reachable Docker Engine ({status.ProblemCode}).");
        return;
    }
    var images = await engine.ListImagesAsync();
    if (!images.Any(item => item.Repository == "alpine" && item.Tag.StartsWith("3.20", StringComparison.Ordinal)))
    {
        Console.WriteLine($"SKIP DOCKER STACK LIVE: {image} is not on this host; pull it first.");
        return;
    }

    var project = "ad04live" + Guid.NewGuid().ToString("N")[..8];
    var volume = $"{project}_data";
    var compose = new DockerComposeService(new TestHostEnvironment(dataDirectory),
        Options.Create(new DockerComposeOptions { DataDirectory = dataDirectory }));
    var coordinator = NewStackCoordinator(ledgerDirectory, compose, new TestApplicationLifetime(), 4);
    await coordinator.StartAsync(CancellationToken.None);

    static string Document(string workerCommand) => $$"""
services:
  web:
    image: alpine:3.20
    command: ["sh", "-c", "sleep 600"]
    volumes:
      - data:/data
  worker:
    image: alpine:3.20
    command: ["sh", "-c", "{{workerCommand}}"]
    volumes:
      - data:/work
volumes:
  data: {}
""";

    // A real deployment takes longer than the fake engine's instant answer, so the live waits are their
    // own budget rather than the 30 seconds the offline checks use.
    static async Task<DockerStackOperationDto> WaitAsync(DockerStackOperationCoordinator coordinator, Guid operationId, int seconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (coordinator.Get(operationId) is { } operation && !DockerStackOperationStore.Active(operation)) return operation;
            await Task.Delay(200);
        }
        throw new InvalidOperationException($"Live operation {operationId} did not reach a terminal state within {seconds} seconds.");
    }

    try
    {
        // AD04-T2 — this server exposes no way to supply a variable, and `docker compose config`
        // substitutes an empty string and still exits 0. Refusing has to happen before the Engine runs.
        var variableVersion = DockerStackValidation.DefinitionVersion(project, variableDocument);
        var refusal = CaptureStackFailure(() => coordinator.Deploy(
            new DockerStackDeployRequest(new DockerStackDefinitionDto(project, variableDocument), variableVersion),
            "live-actor", "live-key-variable"),
            "A definition with an unset variable was queued instead of refused.");
        TestAssert.Assert(refusal.ProblemCode == DockerStackProblem.VariableUnresolved && refusal.StatusCode == 400,
            $"An unresolved variable was not refused before execution: {refusal.ProblemCode}/{refusal.StatusCode}.");

        // AD04-T1/T3 — two services, one of which exits immediately. `up` still exits 0, so the outcome
        // can only come from observing the Engine.
        var failing = Document("exit 7");
        var preview = await compose.PreviewAsync(new DockerStackDefinitionDto(project, failing));
        TestAssert.Assert(preview.Services.Select(service => service.Service).SequenceEqual(["web", "worker"])
            && preview.Volumes.Contains(volume) && preview.Networks.Contains($"{project}_default"),
            $"The live parser did not report the services, volume and network the coordinator reasons about: "
            + $"{string.Join(", ", preview.Services.Select(service => $"{service.Service}/{service.Image}/[{string.Join(",", service.Ports)}]"))}; "
            + $"volumes {string.Join(",", preview.Volumes)}; networks {string.Join(",", preview.Networks)}");

        var submitted = coordinator.Deploy(
            new DockerStackDeployRequest(new DockerStackDefinitionDto(project, failing), preview.DefinitionVersion),
            "live-actor", "live-key-failing");
        var classified = await WaitAsync(coordinator, submitted.OperationId, 180);
        TestAssert.Assert(classified.State == DockerStackOperationState.PartialFailed
            && classified.ProblemCode == DockerStackProblem.PartialFailure,
            $"A live deployment that left one service down was classified as {classified.State}/{classified.ProblemCode}.");
        // What is recorded is the Engine's answer, not the document's intent.
        TestAssert.Assert(classified.Services.Any(service => service.Service == "web" && service.State.Equals("running", StringComparison.OrdinalIgnoreCase))
            && classified.Services.Any(service => service.Service == "worker" && !service.State.Equals("running", StringComparison.OrdinalIgnoreCase)),
            "The live observation did not record one running and one stopped service: "
            + string.Join(", ", classified.Services.Select(service => $"{service.Service}={service.State}")));

        // The repair is the same project and the same volume, which is what makes it an update rather
        // than a second project.
        var repaired = Document("sleep 600");
        var repairedPreview = await compose.PreviewAsync(new DockerStackDefinitionDto(project, repaired));
        TestAssert.Assert(repairedPreview.DefinitionVersion != preview.DefinitionVersion,
            "A changed document produced the same definition version, so an old approval would still be valid.");
        var repair = coordinator.Deploy(
            new DockerStackDeployRequest(new DockerStackDefinitionDto(project, repaired), repairedPreview.DefinitionVersion),
            "live-actor", "live-key-repair");
        var recovered = await WaitAsync(coordinator, repair.OperationId, 180);
        TestAssert.Assert(recovered.State == DockerStackOperationState.Succeeded,
            $"A repaired project was not reported as succeeded: {recovered.State}/{recovered.ProblemCode}.");

        // Same key and same document: the ledger answers with the operation it already ran.
        var replay = coordinator.Deploy(
            new DockerStackDeployRequest(new DockerStackDefinitionDto(project, repaired), repairedPreview.DefinitionVersion),
            "live-actor", "live-key-repair");
        TestAssert.Assert(replay.OperationId == repair.OperationId,
            "A repeated submission created a second live deployment instead of replaying the recorded operation.");

        // AD04-T5 — the volume holds the project's data, not leftover state.
        var retained = await engine.GetVolumeAsync(volume);
        TestAssert.Assert(retained is not null && retained.UsedBy.Count == 2,
            $"A running two-service project did not report both containers as volume references: {retained?.UsedBy.Count}.");
        var inUse = await engine.DeleteVolumeAsync(volume, confirmed: true);
        TestAssert.Assert(!inUse.Success && inUse.ProblemCode == "docker.volume_in_use",
            $"A volume with live references was not refused: {inUse.Success}/{inUse.ProblemCode}.");

        // A stopped project still reserves its volume: the containers exist, so their mounts are held.
        var stopped = await WaitAsync(coordinator,
            coordinator.ApplyAction(project, DockerStackOperationKind.Stop, confirmed: true, "live-actor", "live-key-stop").OperationId, 180);
        TestAssert.Assert(stopped.State == DockerStackOperationState.Succeeded,
            $"Stopping a live project was not reported as succeeded: {stopped.State}/{stopped.ProblemCode}.");
        var afterStop = await engine.GetVolumeAsync(volume);
        TestAssert.Assert(afterStop is not null && afterStop.UsedBy.Count == 2,
            "A stopped project stopped counting as a volume reference, so its data would be releasable by accident.");
        var stoppedDelete = await engine.DeleteVolumeAsync(volume, confirmed: true);
        TestAssert.Assert(!stoppedDelete.Success && stoppedDelete.ProblemCode == "docker.volume_in_use",
            $"A volume held by stopped containers was not refused: {stoppedDelete.Success}/{stoppedDelete.ProblemCode}.");

        // Taking the project down removes the containers and the project network, never the named volume.
        var removed = await WaitAsync(coordinator,
            coordinator.ApplyAction(project, DockerStackOperationKind.Delete, confirmed: true, "live-actor", "live-key-delete").OperationId, 180);
        TestAssert.Assert(removed.State == DockerStackOperationState.Succeeded,
            $"Removing a live project was not reported as succeeded: {removed.State}/{removed.ProblemCode}.");
        var survivor = await engine.GetVolumeAsync(volume);
        TestAssert.Assert(survivor is not null && survivor.UsedBy.Count == 0,
            "Taking the project down removed the project's named volume instead of retaining it.");
        var released = await engine.DeleteVolumeAsync(volume, confirmed: true);
        TestAssert.Assert(released.Success, $"A volume with no references could not be released: {released.ProblemCode}.");

        Console.WriteLine("PASS DOCKER STACK LIVE: a real Compose host parsed, deployed, partly failed, "
            + "recovered, replayed idempotently, and released the project's volume only after removal.");
    }
    finally
    {
        // The check must not leave containers, networks, volumes or files behind.
        try { await compose.ApplyActionAsync(project, DockerStackOperationKind.Delete, confirmed: true); } catch { /* best-effort cleanup */ }
        try { await engine.DeleteVolumeAsync(volume, confirmed: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(Path.Combine(root, "docker-stack-live"), recursive: true); } catch { /* best-effort cleanup */ }
    }
}

/// <summary>A store over a fixed directory, with a ceiling high enough that exclusion rules under test
/// are the per-project ones rather than the concurrency ceiling.</summary>
internal static DockerStackOperationStore NewStackStore(string dataDirectory, int maximumConcurrent)
{
    Directory.CreateDirectory(dataDirectory);
    return new(new TestHostEnvironment(dataDirectory),
        Options.Create(new DockerComposeOptions { DataDirectory = dataDirectory, MaximumConcurrentOperations = maximumConcurrent }));
}

internal static DockerStackOperationCoordinator NewStackCoordinator(string dataDirectory, IDockerComposeService compose,
    IHostApplicationLifetime lifetime, int maximumConcurrent, IOperationalEventPublisher? eventPublisher = null) =>
    new(NewStackStore(dataDirectory, maximumConcurrent), compose,
        Options.Create(new DockerComposeOptions { DataDirectory = dataDirectory, MaximumConcurrentOperations = maximumConcurrent }),
        lifetime, eventPublisher ?? new CaptureOperationalEvents(), NullLogger<DockerStackOperationCoordinator>.Instance);

internal sealed class CaptureOperationalEvents : IOperationalEventPublisher
{
    private readonly ConcurrentQueue<OperationalEventSignal> signals = new();
    internal OperationalEventSignal[] Signals => [.. signals];
    public Task PublishAsync(OperationalEventSignal signal, CancellationToken cancellationToken = default)
    {
        signals.Enqueue(signal);
        return Task.CompletedTask;
    }

    internal async Task WaitForAsync(Guid operationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (signals.Any(signal => signal.OperationId == operationId)) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException($"No operational event was published for {operationId}.");
    }
}

internal static DockerStackServiceDto RunningService(string service) =>
    new(service, $"shop-{service}-1", "nginx:alpine", "running", "Up 2 seconds");

internal static DockerStackServiceDto StoppedService(string service) =>
    new(service, $"shop-{service}-1", "nginx:alpine", "exited", "Exited (0) 2 seconds ago");

/// <summary>Waits for the outcome the worker writes, so the classification is asserted on the record
/// the server persisted rather than on a value the test guessed.</summary>
internal static async Task<DockerStackOperationDto> AwaitTerminalOperationAsync(DockerStackOperationCoordinator coordinator, Guid operationId)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (coordinator.Get(operationId) is { } operation && !DockerStackOperationStore.Active(operation)) return operation;
        await Task.Delay(10);
    }
    throw new InvalidOperationException($"Operation {operationId} did not reach a terminal state.");
}

internal static async Task AwaitRunningOperationAsync(DockerStackOperationCoordinator coordinator, Guid operationId)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (coordinator.Get(operationId) is { State: DockerStackOperationState.Running }) return;
        await Task.Delay(10);
    }
    throw new InvalidOperationException($"Operation {operationId} never entered the running state.");
}

/// <summary>Runs a synchronous admission check and returns the domain refusal it raised.</summary>
internal static DockerStackException CaptureStackFailure(Action action, string message)
{
    try { action(); }
    catch (DockerStackException exception) { return exception; }
    throw new InvalidOperationException(message);
}
}

/// <summary>
/// Compose executor whose every answer is chosen by the test, so the coordinator's outcome
/// classification can be verified without a Docker host.
/// </summary>
internal sealed class FakeComposeService : IDockerComposeService
{
    public IReadOnlyList<string> PreviewServices { get; set; } = [];
    public DockerStackMutationResult NextResult { get; set; } = new(true, string.Empty, []);
    public IReadOnlyList<DockerStackServiceDto> Observed { get; set; } = [];
    public List<DockerStackOperationKind> AppliedActions { get; } = [];
    public int DeployCalls { get; private set; }
    public int ListServicesCalls { get; private set; }
    /// <summary>Holds a deployment inside the Engine call so a test can cancel it while it is active.</summary>
    public bool BlockOnDeploy { get; set; }
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ReleaseDeploy() => gate.TrySetResult();

    public Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DockerStackDto>>([]);

    public Task<DockerStackPreviewDto> PreviewAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DockerStackPreviewDto(definition.Name,
            DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml),
            [.. PreviewServices.Select(service => new DockerStackPreviewServiceDto(service, "nginx:alpine", []))], [], []));

    public Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<DockerStackDefinitionDto?>(null);

    public Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken cancellationToken = default)
    {
        ListServicesCalls++;
        return Task.FromResult(Observed);
    }

    public async Task<DockerStackMutationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default)
    {
        DeployCalls++;
        if (BlockOnDeploy) await gate.Task;
        return NextResult;
    }

    public Task<DockerStackMutationResult> ApplyActionAsync(string name, DockerStackOperationKind action, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        AppliedActions.Add(action);
        return Task.FromResult(NextResult);
    }
}

/// <summary>Lifetime the coordinator links its own cancellation to. It never stops during a test.</summary>
internal sealed class TestApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

/// <summary>No outbound proxy, which is what a default install resolves to. The live stack check talks to
/// the local Engine exactly as that install does.</summary>
internal sealed class DisabledDockerProxyResolver : IDockerProxyResolver
{
    public Task<DockerProxyResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DockerProxyResolution(false, DockerProxySource.Custom, string.Empty, string.Empty,
            string.Empty, false, false, false, false, string.Empty, false, string.Empty));

    public void Invalidate() { }
}
