internal static class ProxyConfigurationChecks
{
internal static async Task VerifyMihomoGeoDataStagingAsync(string root)
{
    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-geodata"));
    var bundled = Path.Combine(root, "mihomo-geodata-bundled");
    Directory.CreateDirectory(bundled);
    var bundledFiles = new[] { "geoip.metadb", "geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb" };
    var bundledHashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var fileName in bundledFiles)
    {
        var bytes = Encoding.UTF8.GetBytes("bundled-" + fileName);
        await File.WriteAllBytesAsync(Path.Combine(bundled, fileName), bytes);
        bundledHashes[fileName] = Convert.ToHexString(SHA256.HashData(bytes));
    }
    var source = Path.Combine(root, "geoip.metadb");
    var content = Encoding.UTF8.GetBytes("test-geodata");
    await File.WriteAllBytesAsync(source, content);
    var service = new MihomoGeoDataService(paths, bundledDataDirectory: bundled, bundledFileHashes: bundledHashes);
    TestAssert.Assert((await service.GetAsync(CancellationToken.None)).IsConfigured == false, "A missing GeoIP database was reported as configured.");
    TestAssert.Assert(await service.EnsureBundledAsync(CancellationToken.None) is null, "Bundled GEO data could not be staged.");
    TestAssert.Assert(File.Exists(Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), "geosite.dat")), "Bundled GeoSite data was not copied to Mihomo's protected data directory.");
    var loadedGeoSite = Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), "geosite.dat");
    using (new FileStream(loadedGeoSite, FileMode.Open, FileAccess.Read, FileShare.Read))
        TestAssert.Assert(await service.EnsureBundledAsync(CancellationToken.None) is null,
            "Verified GEO data was unnecessarily replaced while Mihomo could be using it.");
    TestAssert.Assert(await service.ConfigureFromServerFileAsync(source, CancellationToken.None) is null, "A Server-local geoip.metadb could not be staged.");
    var staged = Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), "geoip.metadb");
    TestAssert.Assert((await service.GetAsync(CancellationToken.None)).IsConfigured && (await File.ReadAllBytesAsync(staged)).SequenceEqual(content),
        "The GeoIP database was not copied to Mihomo's protected data directory.");
    TestAssert.Assert(await service.EnsureBundledAsync(CancellationToken.None) is null && (await File.ReadAllBytesAsync(staged)).SequenceEqual(content),
        "The bundled GEO data overwrote an administrator-selected GeoIP database.");
    TestAssert.Assert(await service.ConfigureFromServerFileAsync(Path.ChangeExtension(source, ".mmdb"), CancellationToken.None) == ProxyProblemCodes.GeodataInvalid,
        "Unsupported GeoIP file extensions were accepted.");
}

internal static async Task VerifyMihomoGeoDataStartupProvisioningAsync()
{
    var geoData = new RecordingGeoDataService();
    var hostedService = new MihomoGeoDataHostedService(geoData);
    await hostedService.StartAsync(CancellationToken.None);
    TestAssert.Assert(geoData.EnsureCalls == 1,
        "Server startup did not provision bundled GEO data before subscription import.");
}

internal static async Task VerifyProxyConfigurationTransactionAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-configuration.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var profiles = new SqliteProxyProfileRepository(new TestHostEnvironment(root), Options.Create(new StorageOptions { DatabasePath = databasePath }));
    var profile = await profiles.UpsertAsync(null, "Transaction", MihomoEngine.Id, null, CancellationToken.None);
    var engine = new TransactionTestEngine();
    var paths = new TestProxyPaths(Path.Combine(root, "proxy-configuration-files"));
    var service = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), profiles, new StaticProxySecretStore(), new MihomoControllerOptions());
    TestAssert.Assert(await service.ApplyAsync(profile.Id, "mode: rule\ngeodata-mode: true\ngeo-auto-update: true\ngeox-url:\n  geoip: https://untrusted.example/geoip.dat\n\"external-controller\": 192.0.2.4:9090\n\"secret\": stale-secret\n", CancellationToken.None) is null,
        "Valid Proxy YAML was not applied.");
    TestAssert.Assert(engine.ValidatedConfigurations.FirstOrDefault() is { } validated
        && validated.Contains("geodata-mode: false\n", StringComparison.Ordinal)
        && validated.Contains("geo-auto-update: false\n", StringComparison.Ordinal)
        && !validated.Contains("untrusted.example", StringComparison.Ordinal),
        "Subscription validation did not use the managed offline GEO configuration.");
    engine.FailNextReload = true;
    TestAssert.Assert(await service.ApplyAsync(profile.Id, "mode: global\n", CancellationToken.None) == ProxyProblemCodes.ConfigApplyFailed,
        "Failed reload did not report a transactional apply failure.");
    var active = await File.ReadAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"));
    var normalizedActive = active.Replace("\r\n", "\n", StringComparison.Ordinal);
    TestAssert.Assert(normalizedActive.Contains("mode: rule\n", StringComparison.Ordinal)
        && normalizedActive.Contains("external-controller: 127.0.0.1:9090\n", StringComparison.Ordinal)
        && normalizedActive.Contains("secret: \"controller-secret\"\n", StringComparison.Ordinal)
        && normalizedActive.Contains("geodata-mode: false\n", StringComparison.Ordinal)
        && normalizedActive.Contains("geo-auto-update: false\n", StringComparison.Ordinal)
        && !normalizedActive.Contains("192.0.2.4", StringComparison.Ordinal)
        && !normalizedActive.Contains("stale-secret", StringComparison.Ordinal)
        && !normalizedActive.Contains("untrusted.example", StringComparison.Ordinal),
        "Managed Proxy configuration did not preserve its server-owned controller and GEO settings.");
}

internal static async Task VerifyProxyTunSafetyAsync(string root)
{
    var platform = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true };
    var runtime = new TestProxyTunRuntimeController();
    var service = new ProxyTunSafetyService(new TestProxyPaths(Path.Combine(root, "proxy-tun")), platform, runtime);
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) is null, "TUN safety transaction rejected a safe management route.");
    TestAssert.Assert((await service.GetStatusAsync(CancellationToken.None)).HasRecoveryMarker, "TUN marker was not durable before network activation.");
    TestAssert.Assert(await service.EmergencyDisableAsync(CancellationToken.None) is null && platform.RestoreCount == 1,
        "Emergency TUN disable did not restore the captured management route.");
    platform.SnapshotSafe = false;
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) == ProxyProblemCodes.ManagementRouteUnsafe && platform.ApplyCount == 1,
        "An unsafe management route was allowed to change the network.");
    platform.SnapshotSafe = true; platform.ApplySucceeds = false;
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) == ProxyProblemCodes.TunActivationFailed && !(await service.GetStatusAsync(CancellationToken.None)).HasRecoveryMarker,
        "Failed TUN activation did not rollback and clear its marker.");
    platform.ApplySucceeds = true; platform.ManagementRouteVerifies = false;
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) == ProxyProblemCodes.ManagementRouteUnsafe && platform.RestoreCount == 3,
        "TUN activation that cut the management path was not rolled back.");
}

internal static async Task VerifyMihomoTunActivationPreservationAsync(string root)
{
    var settings = new ProxySettingsDto(false, false, true, true, false, "warning", 7890, false, "127.0.0.1", ProxyTunSettingsDto.Default);
    var live = string.Join('\n',
    [
        "mode: rule", "mixed-port: 7890", "tun:", "  enable: true", "  stack: mixed", "  device: \"Mihomo\"", "  auto-route: true", "  dns-hijack:",
        "    - \"any:53\"", "  mtu: 1500", "  route-exclude-address:", "    - \"127.0.0.0/8\"", "    - \"::1/128\"", "    - \"192.168.0.0/16\"", "",
    ]);
    var activation = MihomoManagedConfiguration.ReadTunActivation(live);
    TestAssert.Assert(activation.Enabled && activation.RouteExclusions.SequenceEqual(["127.0.0.0/8", "::1/128", "192.168.0.0/16"]),
        "The live TUN activation was not read back from a managed configuration.");

    var rewritten = MihomoManagedConfiguration.WithManagedTunSettings(live, settings, activation.Enabled, activation.RouteExclusions);
    var rewrittenActivation = MihomoManagedConfiguration.ReadTunActivation(rewritten);
    TestAssert.Assert(rewrittenActivation.Enabled && rewrittenActivation.RouteExclusions.SequenceEqual(activation.RouteExclusions),
        "A settings rewrite silently disabled TUN or dropped its route-exclusion boundary.");
    TestAssert.Assert(rewritten.Split('\n').Count(line => line.TrimEnd('\r') == "tun:") == 1,
        "A settings rewrite duplicated or dropped the managed TUN block.");
    TestAssert.Assert(!MihomoManagedConfiguration.ReadTunActivation("mode: rule\ntun:\n  enable: false\n").Enabled,
        "A disabled TUN was read back as enabled.");
    TestAssert.Assert(MihomoManagedConfiguration.ReadTunActivation("tun:\n  enable: true\n  route-exclude-address: [\"192.168.0.0/16\"]\n")
            .RouteExclusions.Single() == "192.168.0.0/16",
        "An inline route-exclude-address sequence was silently lost.");

    var platform = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true };
    var runtime = new TestProxyTunRuntimeController();
    var service = new ProxyTunSafetyService(new TestProxyPaths(Path.Combine(root, "proxy-tun-stale")), platform, runtime);
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) is null, "TUN activation failed before the stale-marker check.");
    runtime.EngineReportsEnabled = false;
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) is null,
        "A completed TUN marker that the engine contradicts still blocked a later activation.");
    TestAssert.Assert((await service.GetStatusAsync(CancellationToken.None)).HasRecoveryMarker,
        "The replacement TUN session did not record its own marker.");
    runtime.ObservationFails = true;
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) == ProxyProblemCodes.RecoveryRequired,
        "An unobserved engine was allowed to invalidate a completed TUN marker.");
    runtime.ObservationFails = false;

    var unfinishedPaths = new TestProxyPaths(Path.Combine(root, "proxy-tun-unfinished"));
    Directory.CreateDirectory(unfinishedPaths.GetStateDirectory());
    var snapshot = new ProxyManagementRouteSnapshot("test", DateTimeOffset.UtcNow, true, "eth0", "192.0.2.1", ["loopback"], []);
    await File.WriteAllTextAsync(Path.Combine(unfinishedPaths.GetStateDirectory(), "proxy-tun-recovery.json"),
        JsonSerializer.Serialize(new { OperationId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), Snapshot = snapshot, CreatedAt = DateTimeOffset.UtcNow, ActivationCompleted = false }));
    var unfinishedService = new ProxyTunSafetyService(unfinishedPaths,
        new TestProxyNetworkSafetyPlatform { SnapshotSafe = true }, new TestProxyTunRuntimeController());
    TestAssert.Assert(await unfinishedService.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) == ProxyProblemCodes.RecoveryRequired,
        "An unfinished TUN marker was treated as stale instead of requiring recovery.");
}

internal static async Task VerifyHostNetworkSafetyDiscoveryAsync()
{
    if (!OperatingSystem.IsLinux()) return;
    var snapshot = await new HostProxyNetworkSafetyPlatform().CaptureManagementRouteAsync(null, CancellationToken.None);
    if (snapshot is null) return; // Minimal containers may have no usable host route; that is fail-closed.
    TestAssert.Assert(snapshot.ManagementPathSafe && !string.IsNullOrWhiteSpace(snapshot.EgressInterface)
        && snapshot.SystemBypass.Contains("loopback") && snapshot.SystemBypass.Contains("relaxkonos-listeners")
        && snapshot.SystemBypass.Contains("default-gateway") && snapshot.SystemBypass.Contains("ssh"),
        "Linux management-route snapshot omitted mandatory system bypass protections.");
}

}
