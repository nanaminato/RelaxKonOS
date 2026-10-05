using RelaxKonOS.Protocol.Proxy;
internal static class NetworkProxyTunnelChecks
{
internal static void VerifyTunnelProtocolContract()
{
    TestAssert.Assert(TunnelApiRoutes.Tunnels == "/api/v1.0/tunnels", "Tunnel API base route changed unexpectedly.");
    var profile = new TunnelServerProfileDto(Guid.NewGuid(), "edge", "frps.example.test", 7000,
        TunnelAuthKind.Token, true, TunnelTlsMode.Default, TunnelRuntimeMode.External, "/opt/frp/frpc", 3,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "fixture-profile-token");
    var json = JsonSerializer.Serialize(profile, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(json.Contains("fixture-profile-token", StringComparison.Ordinal),
        "Tunnel profile DTO must return the saved Token.");
    TestAssert.Assert(json.Contains("tokenConfigured", StringComparison.Ordinal), "Safe tunnel profile DTO lost configured-state indicator.");
    var definition = new TunnelDefinitionDto(Guid.NewGuid(), profile.Id, "ssh", "frp", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null, true, false, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var roundTrip = JsonSerializer.Deserialize<TunnelDefinitionDto>(JsonSerializer.Serialize(definition, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(roundTrip?.Protocol == TunnelProtocol.Tcp && roundTrip.RemotePort == 6000, "Tunnel desired-state DTO JSON contract changed.");
}

internal static void VerifyProxyProtocolContract()
{
    TestAssert.Assert(ProxyApiRoutes.Proxy == "/api/v1.0/proxy" && ProxyApiRoutes.ProfilePattern.StartsWith("/profiles/", StringComparison.Ordinal),
        "Proxy routes must keep one versioned public base and group-relative patterns.");
    TestAssert.Assert(ProxyApiRoutes.Traffic == ProxyApiRoutes.Proxy + "/traffic", "Proxy traffic route changed unexpectedly.");
    var overview = new ProxyOverviewDto("test-engine", new(true, true, true, true, true, true), new(true, true, false, false, false, true, new(true, false, true, false)),
        new("test-engine", ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, "1.0.0", null, true, false),
        new(ProxyRuntimeState.Running, ProxyTunState.Disabled, ProxyHealthState.Healthy, true, true, true), ProxyOperatingMode.ListenerOnly,
        new(Guid.NewGuid(), "profile", "test-engine", true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), 0, new(false, false, null));
    var json = JsonSerializer.Serialize(overview, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(!json.Contains("secret", StringComparison.OrdinalIgnoreCase) && !json.Contains("token", StringComparison.OrdinalIgnoreCase)
        && !json.Contains("yaml", StringComparison.OrdinalIgnoreCase) && !json.Contains("\"externalPath\"", StringComparison.OrdinalIgnoreCase),
        "Proxy public contracts must not serialize secret, raw configuration, or host-path material.");
    var codes = typeof(ProxyProblemCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => field.GetRawConstantValue() as string ?? string.Empty).ToArray();
    TestAssert.Assert(codes.Length > 0 && codes.All(code => code.StartsWith("proxy.", StringComparison.Ordinal)
        && code == code.ToLowerInvariant() && code.Count(character => character == '.') == 1), "Proxy problem codes must be lower-case dotted values.");
}

internal static async Task VerifyMihomoControllerSafetyAsync()
{
    var publicBindingRejected = false;
    try { _ = new MihomoControllerClient(new HttpClient(), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://198.51.100.9:9090") }); }
    catch (InvalidOperationException) { publicBindingRejected = true; }
    TestAssert.Assert(publicBindingRejected, "Public controller binding was accepted.");

    string? authorization = null;
    var handler = new DelegateHandler(async request =>
    {
        authorization = request.Headers.Authorization?.ToString();
        var payload = request.RequestUri!.PathAndQuery switch
        {
            "/proxies" => "{\"proxies\":{\"AUTO\":{\"type\":\"Selector\",\"now\":\"node-a\",\"all\":[\"node-a\",\"node-b\"]}}}",
            "/traffic" => "{\"up\":12,\"down\":34,\"upTotal\":56,\"downTotal\":78}",
            "/memory" => "{\"inuse\":90,\"oslimit\":0}",
            "/connections" => "{\"connections\":null}",
            _ when request.RequestUri.PathAndQuery.StartsWith("/logs", StringComparison.Ordinal) => "{\"type\":\"info\",\"payload\":\"Authorization: Bearer controller-secret token=private-value\"}\n",
            _ => "{}",
        };
        await Task.CompletedTask;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    });
    var client = new MihomoControllerClient(new HttpClient(handler), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/") });
    var groups = await client.GetGroupsAsync(CancellationToken.None);
    TestAssert.Assert(groups.Succeeded && groups.Value!.Single().Selected == "node-a", "Mihomo groups were not mapped to neutral contracts.");
    var traffic = await client.GetTrafficAsync(CancellationToken.None);
    TestAssert.Assert(traffic is { UploadBytesPerSecond: 12, DownloadBytesPerSecond: 34, UploadTotalBytes: 56, DownloadTotalBytes: 78, MemoryBytes: 90 },
        "Mihomo traffic was not mapped to neutral counters.");
    var emptyConnections = await client.GetConnectionsAsync(CancellationToken.None);
    TestAssert.Assert(emptyConnections.Succeeded && emptyConnections.Value!.Count == 0, "Mihomo null tracker slice must represent an empty connection list.");
    var logs = await client.GetLogsAsync(10, CancellationToken.None);
    var log = logs.Value?.Single();
    TestAssert.Assert(logs.Succeeded && log is not null && !log.Message.Contains("controller-secret", StringComparison.Ordinal)
        && !log.Message.Contains("private-value", StringComparison.Ordinal), "Mihomo controller logs were not sanitized.");
    TestAssert.Assert(authorization == "Bearer controller-secret", "Controller secret was not kept in the Server-only authorization header.");

    var unauthorizedHandler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
    var unauthorizedClient = new MihomoControllerClient(new HttpClient(unauthorizedHandler), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/") });
    var unauthorized = await unauthorizedClient.IsReachableAsync(CancellationToken.None);
    TestAssert.Assert(!unauthorized.Succeeded && unauthorized.ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed,
        "A controller 401 was not exposed as an authentication failure.");

    var calls = 0;
    string? reloadPayload = null;
    var interruptedReloadHandler = new DelegateHandler(async request =>
    {
        calls++;
        if (calls == 1)
        {
            reloadPayload = await request.Content!.ReadAsStringAsync();
            throw new HttpRequestException("The controller restarted during reload.");
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
    });
    var interruptedReloadClient = new MihomoControllerClient(new HttpClient(interruptedReloadHandler), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/") });
    TestAssert.Assert(await interruptedReloadClient.ReloadAsync(CancellationToken.None) is null && calls == 2 && reloadPayload == "{\"path\":\"\",\"payload\":\"\"}",
        "A recovered controller connection after reload was incorrectly reported as a TUN configuration failure.");

    var rejectedReloadHandler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("{\"message\":\"invalid TUN configuration\"}"),
    }));
    var rejectedReloadClient = new MihomoControllerClient(new HttpClient(rejectedReloadHandler), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/") });
    TestAssert.Assert(await rejectedReloadClient.ReloadAsync(CancellationToken.None) == ProxyProblemCodes.ConfigApplyFailed,
        "An HTTP configuration rejection was misclassified as an unavailable controller.");
}

internal static async Task VerifyMihomoLiveStreamsAsync()
{
    var options = new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/"), TimeoutSeconds = 2 };
    var handler = new DelegateHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new OpenControllerStream(request.RequestUri!.AbsolutePath switch
        {
            "/traffic" => "{\"up\":12,\"down\":34,\"upTotal\":56,\"downTotal\":78}\n",
            "/memory" => "{\"inuse\":90}\n",
            "/connections" => "{\"connections\":[{\"id\":\"fixture\",\"metadata\":{\"network\":\"tcp\",\"sourceIP\":\"::1\",\"sourcePort\":\"1234\",\"destinationIP\":\"192.0.2.1\",\"destinationPort\":\"443\",\"host\":\"example.test\"},\"rule\":\"Domain\",\"chains\":[\"DIRECT\"],\"start\":\"2026-10-02T00:00:00Z\"}]}",
            _ => "{\"type\":\"info\",\"payload\":\"token=private-value\"}\n",
        }, complete: request.RequestUri.AbsolutePath == "/connections")),
    }));
    var client = new MihomoControllerClient(new HttpClient(handler), new StaticProxySecretStore(), options);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var traffic = await client.GetTrafficAsync(CancellationToken.None);
    TestAssert.Assert(traffic.ProblemCode == "" && traffic.MemoryBytes == 90 && watch.Elapsed < TimeSpan.FromSeconds(1),
        "An endless flow stream must return its first sample without waiting for EOF.");
    var connections = await client.GetConnectionsAsync(CancellationToken.None);
    TestAssert.Assert(connections.Value?.Single() is { Network: "tcp", Source: "[::1]:1234", Destination: "example.test:443", Chains: "DIRECT" },
        "Connection metadata, host and endpoint ports were not mapped.");
    var logs = await client.GetLogsAsync(1, CancellationToken.None);
    TestAssert.Assert(logs.Succeeded && logs.Value!.Count == 1 && !logs.Value[0].Message.Contains("private-value"),
        "A live NDJSON log must be sampled and sanitized without EOF.");
    var quietClient = new MihomoControllerClient(new HttpClient(new QuietControllerHandler()), new StaticProxySecretStore(), options);
    var quietLogs = await quietClient.GetLogsAsync(10, CancellationToken.None);
    TestAssert.Assert(quietLogs.Succeeded && quietLogs.Value!.Count == 0, "A quiet log window was reported as a timeout failure.");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var propagated = false;
    try { await client.GetTrafficAsync(cancelled.Token); } catch (OperationCanceledException) { propagated = true; }
    TestAssert.Assert(propagated, "Caller cancellation must propagate out of live sampling.");
    var oversized = new MihomoControllerClient(new HttpClient(new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new OpenControllerStream(new string('x', 65_537))) }))), new StaticProxySecretStore(), options);
    TestAssert.Assert((await oversized.GetTrafficAsync(CancellationToken.None)).ProblemCode == ProxyProblemCodes.ControllerResponseInvalid,
        "A stream without a bounded JSON line was accepted.");
    var unauthorized = new MihomoControllerClient(new HttpClient(new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))), new StaticProxySecretStore(), options);
    TestAssert.Assert((await unauthorized.GetLogsAsync(10, CancellationToken.None)).ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed,
        "Stream authentication errors must remain distinguishable from quiet logs.");
    var engine = new MihomoEngine(unauthorized, new UnavailableMihomoConfigurationValidator(), new TestProxyPaths(Path.GetTempPath()));
    var failedRead = false;
    try { await engine.GetConnectionsAsync(CancellationToken.None); }
    catch (ProxyObservationException error) { failedRead = error.ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed; }
    TestAssert.Assert(failedRead, "A failed connection read must not become a successful empty list.");
}

internal static async Task VerifyMihomoProxyGroupOrderingAsync(string root)
{
    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-group-order"));
    Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
    await File.WriteAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"), """
        proxy-groups:
          - { name: 节点选择, type: select, proxies: [自动选择] }
          - name: 自动选择
            type: url-test
          - { name: ChatGPT, type: select, proxies: [节点选择] }
        rules: []
        """);

    var controller = new StaticGroupMihomoController(
    [
        new ProxyGroupDto("自动选择", "url-test", null, []),
        new ProxyGroupDto("ChatGPT", "select", null, []),
        new ProxyGroupDto("节点选择", "select", null, []),
    ]);
    var engine = new MihomoEngine(controller, new UnavailableMihomoConfigurationValidator(), paths);
    var groups = await engine.GetGroupsAsync(CancellationToken.None);
    TestAssert.Assert(groups.Select(group => group.Name).SequenceEqual(["节点选择", "自动选择", "ChatGPT"]),
        "Mihomo proxy groups did not retain the order from active.yaml.");
}

internal static async Task VerifyProxyDiagnosticLogsAsync(string root)
{
    var diagnostics = new ProxyDiagnosticLogStore(new TestProxyPaths(Path.Combine(root, "proxy-diagnostics")));
    await diagnostics.WriteAsync("warning", "Managed Mihomo service start failed: token=private-value", CancellationToken.None);
    var entries = await diagnostics.ReadAsync(10, CancellationToken.None);
    TestAssert.Assert(entries.Count == 1 && entries[0].Level == "warning" && !entries[0].Message.Contains("private-value", StringComparison.Ordinal)
        && entries[0].Message.Contains("[REDACTED]", StringComparison.Ordinal), "Proxy installation diagnostics were not retained and sanitized.");

    var engine = new MihomoEngine(new HealthyMihomoController(), new UnavailableMihomoConfigurationValidator(), new TestProxyPaths(Path.Combine(root, "proxy-diagnostics")), diagnostics);
    var combined = await engine.GetLogsAsync(10, CancellationToken.None);
    TestAssert.Assert(combined.Any(entry => entry.Message.Contains("Managed Mihomo service start failed", StringComparison.Ordinal)),
        "Proxy diagnostic logs were not exposed when the controller log was unavailable.");
}

internal static async Task VerifyMihomoRuntimeSafetyAsync(string root)
{
    if (MihomoRuntimeManifest.CurrentRid() != "linux-x64") return;
    var archive = CreateMihomoFixtureArchive();
    var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
    var release = new MihomoRuntimeRelease(MihomoRuntimeManifest.SupportedVersion, "linux-x64", "mihomo-linux-amd64-v1.19.30.gz", "gz", digest);
    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-runtime"));
    var privileged = new TestProxyPrivilegedOperations();
    var manager = new MihomoRuntimeManager(paths, new FixtureHttpClientFactory(archive, "https://release-assets.githubusercontent.com/fixture"), privileged, new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });

    foreach (var redirect in new[] { "http://release-assets.githubusercontent.com/fixture", "https://untrusted.example/fixture", "https://github.com/loop" })
    {
        var rejectedPrivileges = new TestProxyPrivilegedOperations();
        var rejectedManager = new MihomoRuntimeManager(new TestProxyPaths(Path.Combine(root, Guid.NewGuid().ToString("N"))),
            new FixtureHttpClientFactory(archive, redirect), rejectedPrivileges, new TestMihomoRuntimeProbe(), new HealthyMihomoController(),
            new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });
        var rejected = await rejectedManager.InstallManagedAsync(MihomoEngine.Id, release.Version, CancellationToken.None);
        TestAssert.Assert(rejected.ProblemCode == ProxyProblemCodes.RuntimeIntegrityFailed && !rejectedPrivileges.InstalledService,
            "Mihomo accepted an unsafe redirect or redirect loop.");
    }

    var missingExternal = await manager.DetectExternalAsync(MihomoEngine.Id, Path.Combine(root, "does-not-exist"), CancellationToken.None);
    TestAssert.Assert(missingExternal.ProblemCode == ProxyProblemCodes.ExternalRuntimeInvalid && missingExternal.ExternalPathConfigured,
        "A missing external runtime was accepted or exposed a host path.");

    var installed = await manager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    TestAssert.Assert(installed.State == ProxyRuntimeState.Running && installed.IntegrityVerified && installed.Version == MihomoRuntimeManifest.SupportedVersion,
        "A verified Mihomo fixture did not activate only after controller health.");
    TestAssert.Assert(privileged.InstalledService && privileged.RestartCount == 1, "Managed Mihomo did not use the constrained native-service operations.");
    var restartCountBeforeStatusRead = privileged.RestartCount;
    var statusRead = await manager.GetAsync(MihomoEngine.Id, CancellationToken.None);
    TestAssert.Assert(statusRead.State == ProxyRuntimeState.Running && privileged.RestartCount == restartCountBeforeStatusRead,
        "Reading Mihomo status restarted the runtime and could refresh subscriptions.");

    var delayedPaths = new TestProxyPaths(Path.Combine(root, "mihomo-delayed-controller"));
    var delayedPrivileged = new TestProxyPrivilegedOperations();
    var delayedController = new DelayedHealthyMihomoController(unavailableResponses: 2);
    var delayedManager = new MihomoRuntimeManager(delayedPaths, new FixtureHttpClientFactory(archive), delayedPrivileged,
        new TestMihomoRuntimeProbe(), delayedController, new StaticProxySecretStore(),
        new MihomoControllerOptions { StartupReadinessSeconds = 1 }, new MihomoRuntimeManifest { Releases = [release] });
    var delayedInstall = await delayedManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    TestAssert.Assert(delayedInstall.State == ProxyRuntimeState.Running && delayedController.HealthChecks == 3,
        "Managed Mihomo was rolled back before its loopback controller had time to bind.");

    var crossFilesystemRoot = Path.Combine("/var/tmp", "relaxkonos-mihomo-runtime-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var crossFilesystemPrivileged = new TestProxyPrivilegedOperations();
        var crossFilesystemManager = new MihomoRuntimeManager(new TestProxyPaths(crossFilesystemRoot), new FixtureHttpClientFactory(archive),
            crossFilesystemPrivileged, new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });
        var crossFilesystemInstall = await crossFilesystemManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
        TestAssert.Assert(crossFilesystemInstall.State == ProxyRuntimeState.Running && crossFilesystemInstall.IntegrityVerified,
            "A verified Mihomo archive could not be atomically installed when the runtime directory was on another filesystem from /tmp.");
    }
    finally
    {
        if (Directory.Exists(crossFilesystemRoot)) Directory.Delete(crossFilesystemRoot, recursive: true);
    }

    var firstInstallPrivileged = new TestProxyPrivilegedOperations { FailServiceInstallation = true, FailUninstalledServiceRemoval = true };
    var firstInstallManager = new MihomoRuntimeManager(new TestProxyPaths(Path.Combine(root, "mihomo-first-install-failure")), new FixtureHttpClientFactory(archive),
        firstInstallPrivileged, new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });
    var firstInstallFailure = await firstInstallManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    TestAssert.Assert(firstInstallFailure.ProblemCode == ProxyProblemCodes.PrivilegedOperationUnavailable,
        "A failed first-time service installation was incorrectly reported as a recovery-required failure.");

    privileged.FailReplacement = true;
    var failedUpdate = await manager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    var afterFailedUpdate = await manager.GetAsync(MihomoEngine.Id, CancellationToken.None);
    TestAssert.Assert(failedUpdate.ProblemCode == ProxyProblemCodes.PrivilegedOperationUnavailable && afterFailedUpdate.Version == MihomoRuntimeManifest.SupportedVersion
        && afterFailedUpdate.State == ProxyRuntimeState.Running,
        "A healthy managed Mihomo runtime was not reported as running after status refresh.");

    var traversalArchive = CreateMihomoTraversalArchive();
    var traversalDigest = Convert.ToHexString(SHA256.HashData(traversalArchive)).ToLowerInvariant();
    var traversalManager = new MihomoRuntimeManager(new TestProxyPaths(Path.Combine(root, "mihomo-traversal")), new FixtureHttpClientFactory(traversalArchive),
        new TestProxyPrivilegedOperations(), new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(),
        new MihomoRuntimeManifest { Releases = [release with { ArchiveFormat = "zip", AssetName = "mihomo-windows-amd64-v1.19.30.zip", Sha256 = traversalDigest, Rid = "linux-x64" }] });
    var traversal = await traversalManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    TestAssert.Assert(traversal.ProblemCode == ProxyProblemCodes.RuntimeIntegrityFailed, "A path-traversal runtime archive was accepted.");
}

internal static async Task VerifyLinuxMihomoRuntimeLinkActivationAsync(string root)
{
    if (!OperatingSystem.IsLinux() || MihomoRuntimeManifest.CurrentRid() != "linux-x64") return;

    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-link-activation"));
    var versions = paths.GetEngineVersionsDirectory(MihomoEngine.Id);
    var releaseId = MihomoRuntimeManifest.SupportedVersion + "-linux-x64";
    var release = Path.Combine(versions, releaseId);
    var previous = Path.Combine(versions, "previous-release");
    var active = Path.Combine(versions, "current");
    var temporary = active + ".new";
    Directory.CreateDirectory(release);
    Directory.CreateDirectory(previous);
    await File.WriteAllTextAsync(Path.Combine(release, "mihomo"), "fixture");
    Directory.CreateSymbolicLink(active, previous);
    Directory.CreateSymbolicLink(temporary, previous);

    var operations = new NativeMihomoPrivilegedOperations(paths);
    var result = await operations.InstallRuntimeAsync(new InstallProxyRuntimeOperation(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, releaseId), CancellationToken.None);

    TestAssert.Assert(result.Succeeded, "Managed Mihomo could not atomically activate a directory symbolic link on Linux.");
    TestAssert.Assert(!File.Exists(temporary) && !Directory.Exists(temporary), "Managed Mihomo activation left its temporary symbolic link behind.");
    TestAssert.Assert(Path.GetFullPath(Path.Combine(versions, new DirectoryInfo(active).LinkTarget!)) == Path.GetFullPath(release),
        "Managed Mihomo activation did not atomically replace the active runtime link.");
}

internal static byte[] CreateMihomoFixtureArchive()
{
    var binary = new byte[64]; binary[0] = 0x7f; binary[1] = (byte)'E'; binary[2] = (byte)'L'; binary[3] = (byte)'F'; binary[4] = 2; binary[5] = 1;
    BitConverter.GetBytes((ushort)62).CopyTo(binary, 18);
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(binary);
    return output.ToArray();
}

internal static byte[] CreateMihomoTraversalArchive()
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        var traversal = archive.CreateEntry("../mihomo");
        using var writer = traversal.Open(); writer.Write([1, 2, 3]);
    }
    return output.ToArray();
}

internal static void VerifyFrpTomlSafety()
{
    TestAssert.Assert(TunnelValidation.ValidateDefinition("bad\nname", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null) == "tunnel.definition_invalid",
        "Tunnel name validation allowed TOML control characters.");
    TestAssert.Assert(TunnelValidation.ValidateDefinition("http", TunnelProtocol.Http, "::1", 8080, null, "app.example.test") is null,
        "IPv6 loopback HTTP tunnel was rejected.");
    var profile = new TunnelServerProfileDto(Guid.NewGuid(), "edge", "frps.example.test", 7000, TunnelAuthKind.Token, true,
        TunnelTlsMode.Force, TunnelRuntimeMode.Managed, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var tunnel = new TunnelDefinitionDto(Guid.NewGuid(), profile.Id, "api", "frp", TunnelProtocol.Https, "::1", 8443,
        null, "app.example.test", true, true, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var toml = FrpTomlGenerator.Generate(profile, [tunnel], "token-with-\\-and-\"quote");
    TestAssert.Assert(toml.Contains("token = \"token-with-\\\\-and-\\\"quote\"", StringComparison.Ordinal), "FRP TOML token escaping changed.");
    TestAssert.Assert(toml.Contains("customDomains = [\"app.example.test\"]", StringComparison.Ordinal) && toml.Contains("[proxies.transport]", StringComparison.Ordinal),
        "FRP TOML generator lost HTTPS domain or transport options.");
}

internal static async Task VerifyFrpRuntimeInstallAndRollbackAsync(string root)
{
    if (!OperatingSystem.IsLinux()) return;
    var archive = CreateFrpFixtureArchive();
    var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
    var releases = new[]
    {
        new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = digest, ArchiveFormat = "tar.gz" },
        new FrpRuntimeRelease { Version = "v0.71.1", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_linux_amd64.tar.gz", Sha256 = digest, ArchiveFormat = "tar.gz" },
    };
    var runtimeRoot = Path.Combine(root, "frp-runtime"); Directory.CreateDirectory(runtimeRoot);
    var env = new TestHostEnvironment(runtimeRoot);
    var manager = new FrpRuntimeManager(env, new FixtureHttpClientFactory(archive), Options.Create(new FrpRuntimeOptions { Releases = releases }), new RelaxKonOS.Server.Privileged.ManagedRuntimeOperations(new CapturingPrivilegedTransport()));
    var first = await manager.InstallManagedFrpcAsync("v0.71.0", new SilentInstallationProgress(), CancellationToken.None);
    TestAssert.Assert(first.Succeeded, "Verified FRP fixture did not install.");
    await VerifyFrpApplyLifecycleAsync(root, env, manager);
    var second = await manager.InstallManagedFrpcAsync("v0.71.1", new SilentInstallationProgress(), CancellationToken.None);
    TestAssert.Assert(second.Succeeded, "Second verified FRP fixture did not install.");
    var active = await manager.GetManagedFrpcStatusAsync(CancellationToken.None);
    TestAssert.Assert(active.Version == "v0.71.1" && active.PreviousVersion == "v0.71.0" && active.IntegrityVerified, "Runtime activation did not preserve previous version state.");
    var rolledBack = await manager.RollbackManagedFrpcAsync(CancellationToken.None);
    TestAssert.Assert(rolledBack.Succeeded && (await manager.GetManagedFrpcStatusAsync(CancellationToken.None)).Version == "v0.71.0", "Runtime rollback did not restore verified previous version.");
    var uninstalled = await manager.UninstallManagedFrpcAsync(CancellationToken.None);
    TestAssert.Assert(uninstalled.Succeeded && (await manager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled,
        "Runtime uninstall did not clear the managed runtime state.");
    TestAssert.Assert(!Directory.Exists(Path.Combine(runtimeRoot, "data", "runtimes", "frp")), "Runtime uninstall left managed runtime files behind.");

    var invalidChecksum = await manager.InstallManagedFrpcAsync("v0.99.0", new SilentInstallationProgress(), CancellationToken.None);
    TestAssert.Assert(!invalidChecksum.Succeeded && invalidChecksum.ProblemCode == "tunnel.runtime_release_not_configured", "Unconfigured runtime version was accepted.");

    var badChecksumRoot = Path.Combine(root, "frp-runtime-bad-checksum"); Directory.CreateDirectory(badChecksumRoot);
    var badChecksumManager = new FrpRuntimeManager(new TestHostEnvironment(badChecksumRoot), new FixtureHttpClientFactory(archive), Options.Create(new FrpRuntimeOptions
    {
        Releases = [new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = new string('0', 64), ArchiveFormat = "tar.gz" }],
    }), new RelaxKonOS.Server.Privileged.ManagedRuntimeOperations(new CapturingPrivilegedTransport()));
    var badChecksum = await badChecksumManager.InstallManagedFrpcAsync("v0.71.0", new SilentInstallationProgress(), CancellationToken.None);
    TestAssert.Assert(!badChecksum.Succeeded && badChecksum.ProblemCode == "tunnel.runtime_checksum_failed", "Wrong checksum was accepted.");
    TestAssert.Assert((await badChecksumManager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled, "Checksum failure changed the active runtime.");

    var maliciousArchive = CreateMaliciousFrpFixtureArchive();
    var maliciousDigest = Convert.ToHexString(SHA256.HashData(maliciousArchive)).ToLowerInvariant();
    var maliciousRoot = Path.Combine(root, "frp-runtime-malicious"); Directory.CreateDirectory(maliciousRoot);
    var maliciousManager = new FrpRuntimeManager(new TestHostEnvironment(maliciousRoot), new FixtureHttpClientFactory(maliciousArchive), Options.Create(new FrpRuntimeOptions
    {
        Releases = [new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = maliciousDigest, ArchiveFormat = "tar.gz" }],
    }), new RelaxKonOS.Server.Privileged.ManagedRuntimeOperations(new CapturingPrivilegedTransport()));
    var malicious = await maliciousManager.InstallManagedFrpcAsync("v0.71.0", new SilentInstallationProgress(), CancellationToken.None);
    TestAssert.Assert(!malicious.Succeeded && malicious.ProblemCode == "tunnel.runtime_archive_unexpected_entry", "Unexpected archive content was accepted.");
    TestAssert.Assert((await maliciousManager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled, "Rejected archive changed the active runtime.");
}

internal static byte[] CreateFrpFixtureArchive()
{
    using var target = new MemoryStream();
    using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        Write("frp/frpc", "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo frpc-fixture; exit 0; fi\nif [ \"$1\" = \"verify\" ]; then exit 0; fi\nif [ \"$1\" = \"-c\" ]; then echo 'login to server success'; sleep 30; fi\n");
        Write("frp/frps", "#!/bin/sh\necho frps-fixture\n");
        void Write(string name, string content) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
    }
    return target.ToArray();
}

internal static byte[] CreateMaliciousFrpFixtureArchive()
{
    using var target = new MemoryStream();
    using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/frpc") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho frpc-fixture\n")) });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/frps") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho frps-fixture\n")) });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/unexpected-plugin") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("not allowed")) });
    }
    return target.ToArray();
}

internal static async Task VerifyFrpApplyLifecycleAsync(string root, IHostEnvironment environment, IRuntimeManager runtime)
{
    var independentTransport = new IndependentManagedRuntimeTransport();
    var path = Path.Combine(root, "frp-apply-lifecycle.db");
    var services = new ServiceCollection();
    services.AddDbContext<RelaxKonOSDbContext>(options => options.UseSqlite($"Data Source={path}"));
    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "frp-apply-keys")));
    services.AddScoped<ISecretStore, DataProtectionSecretStore>();
    services.AddScoped<ITunnelAudit, TunnelAudit>();
    services.AddScoped<ITunnelService, TunnelService>();
    services.AddSingleton<IRuntimeManager>(runtime);
    services.AddSingleton<ITunnelProvider>(provider => new FrpTunnelProvider(provider.GetRequiredService<IServiceScopeFactory>(), environment, provider.GetRequiredService<IRuntimeManager>(), new RelaxKonOS.Server.Privileged.ManagedRuntimeOperations(independentTransport)));
    await using var container = services.BuildServiceProvider();
    await using (var scope = container.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>(); await db.Database.EnsureCreatedAsync();
        var service = scope.ServiceProvider.GetRequiredService<ITunnelService>();
        var profile = await service.UpsertProfileAsync(null, new UpsertTunnelServerProfileRequest("managed", "frps.example.test", 7000, TunnelAuthKind.None, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), "apply-user", CancellationToken.None);
        await service.UpsertTunnelAsync(null, new UpsertTunnelDefinitionRequest(profile.Id, "ssh", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null, true, false, false), "apply-user", CancellationToken.None);
        var provider = scope.ServiceProvider.GetRequiredService<ITunnelProvider>();
        try
        {
            var applied = await provider.ApplyAsync(profile.Id, "apply-user", CancellationToken.None);
            TestAssert.Assert(applied.Succeeded && applied.State == TunnelConnectionState.Starting, "Managed FRP desired state was not started.");
            IReadOnlyList<TunnelDefinitionDto> current = [];
            for (var attempt = 0; attempt < 10; attempt++)
            {
                current = await provider.ListAsync("apply-user", CancellationToken.None);
                if (current.Single().State == TunnelConnectionState.Connected) break;
                await Task.Delay(100);
            }
            TestAssert.Assert(current.Single().State == TunnelConnectionState.Connected, "Successful FRP login was overwritten by the startup state.");
            var reopened = new FrpTunnelProvider(container.GetRequiredService<IServiceScopeFactory>(), environment, runtime,
                new RelaxKonOS.Server.Privileged.ManagedRuntimeOperations(independentTransport));
            TestAssert.Assert((await reopened.ListAsync("apply-user", CancellationToken.None)).Single().State == TunnelConnectionState.Connected,
                "Reopening the FRPC provider lost its independent service applied identity.");
            var original = current.Single();
            await service.UpsertTunnelAsync(original.Id, new UpsertTunnelDefinitionRequest(profile.Id, original.Name, original.Protocol,
                original.LocalHost, 23, original.RemotePort, null, true, false, false, original.Revision), "apply-user", CancellationToken.None);
            current = await provider.ListAsync("apply-user", CancellationToken.None);
            TestAssert.Assert(current.Single().State == TunnelConnectionState.SavedNotApplied && current.Single().ProblemCode == "tunnel.definition_not_applied",
                "Live FRP login incorrectly proved a newer desired definition was applied.");
            TestAssert.Assert((await provider.ApplyAsync(profile.Id, "apply-user", CancellationToken.None)).Succeeded, "Changed desired state could not reapply.");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                current = await provider.ListAsync("apply-user", CancellationToken.None);
                if (current.Single().State == TunnelConnectionState.Connected) break;
                await Task.Delay(100);
            }
            TestAssert.Assert(current.Single().State == TunnelConnectionState.Connected, "Reapplied desired revision did not connect.");
            TestAssert.Assert((await provider.GetLogsAsync(profile.Id, "apply-user", CancellationToken.None))?.All(entry => !entry.Message.Contains("token", StringComparison.OrdinalIgnoreCase)) == true, "Runtime log exposed a token.");
            TestAssert.Assert((await provider.StopAsync(profile.Id, "apply-user", CancellationToken.None)).Succeeded, "Managed FRP process could not be stopped.");
        }
        finally { await provider.StopAsync(profile.Id, "apply-user", CancellationToken.None); }
    }
}

internal static async Task VerifyTunnelSecretLifecycleAsync(string root)
{
    var path = Path.Combine(root, "tunnel-secret-lifecycle.db");
    var dbOptions = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={path}").Options;
    await using var db = new RelaxKonOSDbContext(dbOptions); await db.Database.EnsureCreatedAsync();
    var protection = DataProtectionProvider.Create(Path.Combine(root, "data-protection"));
    var secrets = new DataProtectionSecretStore(db, protection);
    var service = new TunnelService(db, secrets, new TunnelAudit(db));
    const string user = "tunnel-test-user";
    var created = await service.UpsertProfileAsync(null, new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000,
        TunnelAuthKind.Token, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), user, CancellationToken.None);
    try
    {
        await service.UpsertTunnelAsync(null, new UpsertTunnelDefinitionRequest(created.Id, "invalid", TunnelProtocol.Http, "127.0.0.1", 8080, 6000, null, true, false, false), user, CancellationToken.None);
        throw new InvalidOperationException("Invalid HTTP tunnel was accepted.");
    }
    catch (TunnelValidationException exception) { TestAssert.Assert(exception.ProblemCode == "tunnel.domain_required", "Invalid tunnel did not return stable problem code."); }
    await service.SetProfileTokenAsync(created.Id, "saved-profile-token", user, CancellationToken.None);
    var safe = await service.GetProfileAsync(created.Id, user, CancellationToken.None) ?? throw new InvalidOperationException("Tunnel profile disappeared.");
    TestAssert.Assert(safe.TokenConfigured && safe.Token == "saved-profile-token", "Profile reads omitted the saved Token.");
    TestAssert.Assert((await service.ListProfilesAsync(user, CancellationToken.None)).Single().Token == safe.Token, "Profile list omitted the saved Token.");
    var oldestAudit = DateTimeOffset.UtcNow.AddMinutes(-2);
    db.TunnelAuditEntries.AddRange(
        new TunnelAuditEntry { Id = Guid.NewGuid(), ActorUserId = user, Action = "frps.start", Result = "succeeded", CreatedAt = oldestAudit },
        new TunnelAuditEntry { Id = Guid.NewGuid(), ActorUserId = user, Action = "frps.stop", Result = "succeeded", CreatedAt = oldestAudit.AddMinutes(1) });
    await db.SaveChangesAsync();
    var frpsAudit = await new TunnelAudit(db).ListFrpsAsync(CancellationToken.None);
    TestAssert.Assert(frpsAudit.Count == 2 && frpsAudit[0].Action == "frps.stop" && frpsAudit[1].Action == "frps.start",
        "FRPS audit history was not returned in descending timestamp order.");
    var updated = await service.UpsertProfileAsync(created.Id, new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000,
        TunnelAuthKind.None, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null, safe.Revision), user, CancellationToken.None);
    TestAssert.Assert(!updated.TokenConfigured && !await db.TunnelSecrets.AnyAsync(), "Changing auth away from token left an orphan secret.");
    TestAssert.Assert(await service.DeleteProfileAsync(created.Id, user, CancellationToken.None), "Unused profile could not be deleted.");
}

}

internal sealed class QuietControllerHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException();
    }
}
internal sealed class OpenControllerStream(string payload, bool complete = false) : Stream
{
    private readonly byte[] _payload = Encoding.UTF8.GetBytes(payload);
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position == _payload.Length)
        {
            if (complete) return 0;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var length = Math.Min(buffer.Length, _payload.Length - _position);
        _payload.AsMemory(_position, length).CopyTo(buffer); _position += length;
        return length;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
