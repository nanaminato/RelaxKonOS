internal static class ProxyConfigurationChecks
{
internal static async Task VerifyProxyConfigurationTransactionAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-configuration.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var profiles = new SqliteProxyProfileRepository(new TestHostEnvironment(root), Options.Create(new StorageOptions { DatabasePath = databasePath }));
    var profile = await profiles.UpsertAsync(null, "Transaction", MihomoEngine.Id, null, CancellationToken.None);
    var engine = new TransactionTestEngine();
    var paths = new TestProxyPaths(Path.Combine(root, "proxy-configuration-files"));
    var publication = new MihomoPublicationStore(paths, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
    var service = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), profiles, new StaticProxySecretStore(), new MihomoControllerOptions(), publication);
    TestAssert.Assert(await service.ApplyAsync(profile.Id, "mode: rule\ngeodata-mode: true\ngeo-auto-update: true\ngeox-url:\n  geoip: https://untrusted.example/geoip.dat\n\"external-controller\": 192.0.2.4:9090\n\"secret\": stale-secret\n", CancellationToken.None) is null,
        "Valid Proxy YAML was not applied.");
    TestAssert.Assert(engine.ValidatedConfigurations.FirstOrDefault() is { } validated
        && validated.Contains("geodata-mode: false\n", StringComparison.Ordinal)
        && validated.Contains("geo-auto-update: false\n", StringComparison.Ordinal)
        && !validated.Contains("untrusted.example", StringComparison.Ordinal),
        "Subscription validation did not use the managed offline GEO configuration.");
    var appliedProfile = await profiles.GetAsync(profile.Id, CancellationToken.None);
    TestAssert.Assert(appliedProfile is { IsActive: true } && appliedProfile.Revision == profile.Revision + 1,
        "Successful configuration application did not update active profile metadata exactly once.");
    var second = await profiles.UpsertAsync(null, "Second", MihomoEngine.Id, null, CancellationToken.None);
    TestAssert.Assert(await service.StoreAsync(second.Id, "mode: global\n", CancellationToken.None) is null,
        "Second profile configuration was not stored.");
    engine.FailNextReload = true;
    using (var caller = new CancellationTokenSource())
    {
        var reloads = engine.ReloadCalls;
        engine.BeforeNextReload = caller.Cancel;
        TestAssert.Assert(await service.ActivateStoredAsync(second.Id, caller.Token) == ProxyProblemCodes.ConfigApplyFailed
            && caller.IsCancellationRequested && engine.ReloadCalls == reloads + 2,
            "The accepted failed activation must complete one candidate reload and one compensation despite caller cancellation.");
    }
    TestAssert.Assert(await profiles.GetAsync(profile.Id, CancellationToken.None) == appliedProfile
        && await profiles.GetAsync(second.Id, CancellationToken.None) == second,
        "Failed profile activation changed the active profile despite rolling back its YAML.");
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
    using (var caller = new CancellationTokenSource())
    {
        var reloads = engine.ReloadCalls;
        engine.BeforeNextReload = caller.Cancel;
        TestAssert.Assert(await service.ActivateStoredAsync(second.Id, caller.Token) is null
            && caller.IsCancellationRequested && engine.ReloadCalls == reloads + 1
            && await profiles.GetAsync(second.Id, CancellationToken.None) is { IsActive: true } activeSecond
            && activeSecond.Revision == second.Revision + 1
            && !(await profiles.GetAsync(profile.Id, CancellationToken.None))!.IsActive,
            "Accepted profile activation must commit the active metadata exactly once despite caller cancellation.");
    }
    var subscriptions = new SqliteProxySubscriptionRepository(new TestHostEnvironment(root),
        Options.Create(new StorageOptions { DatabasePath = databasePath }),
        new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
    var subscription = await subscriptions.CreateAsync("second-feed", second.Id, "https://feed.example.test",
        ProxySubscriptionDownloadRoute.Direct, default);
    var refreshedAt = DateTimeOffset.UtcNow;
    var refresh = new ProxyQueuedSubscriptionRefresh(Guid.NewGuid(), new(subscription.Id, refreshedAt), "mode: global\n");
    TestAssert.Assert(await service.ActivateStoredAsync(second.Id, default, refresh) is null
        && (await subscriptions.GetAsync(subscription.Id, default))!.Subscription.LastUpdatedAt == refreshedAt,
        "A queued refresh recognizes its own stored GEO-normalized draft and commits its timestamp.");
    var beforePending = await File.ReadAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"));
    var activationReloads = engine.ReloadCalls;
    TestAssert.Assert(await service.ActivateStoredAsync(profile.Id, default,
            activation: new(Guid.NewGuid(), subscription.Id)) == ProxyProblemCodes.RecoveryRequired
        && engine.ReloadCalls == activationReloads && (await publication.ReadAsync(default)).DirectReload is null,
        "A queued activation must validate its original subscription/profile binding before reloading.");
    var failedActivation = Guid.NewGuid();
    engine.FailNextReload = true;
    TestAssert.Assert(await service.ActivateStoredAsync(second.Id, default, activation: new(failedActivation, subscription.Id))
        == ProxyProblemCodes.ConfigApplyFailed && engine.ReloadCalls == activationReloads + 2
        && (await publication.ReadAsync(default)).SubscriptionActivationCompletions?.GetValueOrDefault(failedActivation) is { Confirmed: false }
        && (await subscriptions.GetAsync(subscription.Id, default))!.Subscription.LastUpdatedAt == refreshedAt,
        "A definite direct rollback must leave the original activation failure receipt and preserve the subscription timestamp.");
    var activationRecovery = new RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeRecoveryService(publication, null!, null!, null!, null!, profiles);
    TestAssert.Assert(await activationRecovery.RetryAsync(failedActivation, default) is
        { Outcome: RelaxKonOS.Protocol.ManagedRuntimes.RuntimeSwitchOutcome.Failed, ProblemCode: ProxyProblemCodes.ConfigApplyFailed } failure
        && failure.OperationId == failedActivation && engine.ReloadCalls == activationReloads + 2,
        "Named recovery of the original failed activation must return the saved outcome without touching the runtime.");
    var staleRefresh = new ProxyQueuedSubscriptionRefresh(Guid.NewGuid(), new(Guid.NewGuid(), DateTimeOffset.UtcNow), "mode: direct\n");
    TestAssert.Assert(await service.ActivateStoredAsync(second.Id, default, staleRefresh) == ProxyProblemCodes.ConfigApplyFailed
        && await File.ReadAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml")) == beforePending,
        "A queued refresh cannot activate a different draft under its original identity.");
    var pendingMaterial = new RelaxKonOS.Protocol.ManagedRuntimes.MihomoPublishedRuntime("# pending publication\n", [], 9090,
        "pending-controller-secret", [new("127.0.0.1", 7890)]);
    var managed = Guid.NewGuid();
    await publication.BeginAsync(managed, (await publication.ReadAsync(default)).Revision, pendingMaterial, default);
    TestAssert.Assert(await service.StoreAsync(second.Id, "mode: direct\n", default) == ProxyProblemCodes.RecoveryRequired
        && await service.ActivateStoredAsync(profile.Id, default) == ProxyProblemCodes.RecoveryRequired
        && await File.ReadAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml")) == beforePending,
        "Direct profile writes bypassed a pending managed publication.");
    await publication.AbortAsync(managed, null, notStarted: true, default);
    var beforeUnknown = await profiles.GetAsync(second.Id, default);
    var reloadsBeforeUnknown = engine.ReloadCalls;
    var unknownActivation = Guid.NewGuid();
    engine.NextReloadProblem = ProxyProblemCodes.RecoveryRequired;
    TestAssert.Assert(await service.ActivateStoredAsync(second.Id, default, activation: new(unknownActivation, subscription.Id)) == ProxyProblemCodes.RecoveryRequired
        && engine.ReloadCalls == reloadsBeforeUnknown + 1 && await profiles.GetAsync(second.Id, default) == beforeUnknown
        && await File.ReadAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml")) == beforePending,
        "Unknown profile reload must preserve the prior file and metadata without another controller mutation.");
    var checkpoint = (await publication.ReadAsync(default)).DirectReload;
    TestAssert.Assert(checkpoint?.OperationId == unknownActivation && checkpoint.Profile?.ActivationSubscriptionId == subscription.Id
        && await service.ActivateStoredAsync(profile.Id, default) == ProxyProblemCodes.RecoveryRequired
        && engine.ReloadCalls == reloadsBeforeUnknown + 1 && (await publication.ReadAsync(default)).DirectReload == checkpoint,
        "The original direct checkpoint must block a subsequent activation without dispatching.");
    var queue = new ProxyOperationStore(paths, NullLogger<ProxyOperationStore>.Instance, new TestProxyWorkLeases(), publication);
    var receiptPath = Path.Combine(paths.GetStateDirectory(), "proxy-operations.json");
    var running = new ProxyOperationDto(unknownActivation, "subscription.activate", ProxyOperationState.Running,
        "running", "", DateTimeOffset.UtcNow, null);
    await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(new[] { new { IdempotencyKey = "activation", Operation = running } }));
    var pendingActivation = await queue.GetAsync(unknownActivation, default);
    TestAssert.Assert(pendingActivation is { State: ProxyOperationState.Interrupted, Stage: "publication_pending", CompletedAt: null }
        && await queue.EnqueueAsync("activation", running.Kind, (_, _) => throw new Exception("Replayed activation"), default) == pendingActivation
        && await activationRecovery.RetryAsync(unknownActivation, default) is
            { Outcome: RelaxKonOS.Protocol.ManagedRuntimes.RuntimeSwitchOutcome.RecoveryRequired } unresolved
        && unresolved.OperationId == unknownActivation && engine.ReloadCalls == reloadsBeforeUnknown + 1,
        "An unconfirmed activation must keep the same pending queue and runtime identities without reloading.");
    var activePath = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
    await File.WriteAllTextAsync(activePath, "# independently changed configuration\n");
    await publication.Gate.WaitAsync();
    try { await publication.RecoverDirectReloadUnderGateAsync(await publication.ReadAsync(default), profiles, default); }
    finally { publication.Gate.Release(); }
    TestAssert.Assert(await File.ReadAllTextAsync(activePath) == "# independently changed configuration\n"
        && await publication.ReadPendingIdentityAsync(default) == checkpoint!.OperationId,
        "Direct recovery must preserve independently changed disk content and the original schedule identity.");

    var confirmedPaths = new TestProxyPaths(Path.Combine(root, "confirmed-profile"));
    var confirmedStore = new MihomoPublicationStore(confirmedPaths, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
    var confirmedFile = Path.Combine(Directory.CreateDirectory(confirmedPaths.GetProtectedConfigurationDirectory()).FullName, "active.yaml");
    const string candidate = "mode: rule\n";
    var selectedBefore = (await profiles.GetAsync(profile.Id, default))!;
    var activeBefore = (await profiles.GetActiveAsync(default))!;
    var selection = new MihomoProfilePublication(profile.Id, selectedBefore.Revision, activeBefore.Id,
        activeBefore.Revision, selectedBefore.CreatedAt, activeBefore.CreatedAt);
    MihomoDirectReload confirmed;
    await confirmedStore.Gate.WaitAsync();
    try
    {
        confirmed = await confirmedStore.BeginDirectReloadAsync(beforePending, candidate, default, selection);
        await confirmedStore.ConfirmDirectProfileReloadAsync(confirmed.OperationId);
    }
    finally { confirmedStore.Gate.Release(); }
    var named = new RelaxKonOS.Server.ManagedRuntimes.MihomoRuntimeRecoveryService(confirmedStore, null!, null!, null!, null!, profiles);
    await File.WriteAllTextAsync(confirmedFile, "# independent configuration\n");
    var blocked = await named.RetryAsync(confirmed.OperationId, default);
    TestAssert.Assert(blocked.OperationId == confirmed.OperationId && blocked.ProblemCode == "runtime.direct_profile_commit_pending"
        && await profiles.GetAsync(profile.Id, default) == selectedBefore
        && await File.ReadAllTextAsync(confirmedFile) == "# independent configuration\n"
        && await confirmedStore.ReadPendingIdentityAsync(default) == confirmed.OperationId,
        "Acknowledged profile recovery must preserve an independent edit and leave SQLite unchanged.");
    await File.WriteAllTextAsync(confirmedFile, candidate);
    var completed = await named.RetryAsync(confirmed.OperationId, default);
    TestAssert.Assert(completed.Outcome == RelaxKonOS.Protocol.ManagedRuntimes.RuntimeSwitchOutcome.Succeeded
        && await named.RetryAsync(confirmed.OperationId, default) == completed
        && await confirmedStore.ReadPendingIdentityAsync(default) is null
        && await profiles.GetAsync(profile.Id, default) is { IsActive: true } committed && committed.Revision == selectedBefore.Revision + 1,
        "The original acknowledged profile receipt must survive repeated recovery without incrementing its revision.");
}

internal static async Task VerifyProxyTunSafetyAsync(string root)
{
    var platform = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true };
    var runtime = new TestProxyTunRuntimeController();
    var service = new ProxyTunSafetyService(new TestProxyPaths(Path.Combine(root, "proxy-tun")), platform, runtime);
    TestAssert.Assert(await service.EnableAsync(Guid.NewGuid(), null, CancellationToken.None) is null, "TUN safety transaction rejected a safe management route.");
    TestAssert.Assert((await service.GetStatusAsync(CancellationToken.None)).HasRecoveryMarker, "TUN marker was not durable before network activation.");
    var original = await service.CaptureRecoveryAsync(includeCurrentSession: true, default);
    TestAssert.Assert(original?.OperationId == runtime.LastOperationId, "Activation did not receive the original marker identity.");
    TestAssert.Assert(await service.EmergencyDisableAsync(CancellationToken.None) is null && platform.RestoreCount == 1
        && original?.OperationId == runtime.LastOperationId,
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

    var markerPath = Path.Combine(unfinishedPaths.GetStateDirectory(), "proxy-tun-recovery.json");
    var unknownRuntime = new TestProxyTunRuntimeController();
    var unknownPlatform = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true };
    var unknownSafety = new ProxyTunSafetyService(unfinishedPaths, unknownPlatform, unknownRuntime);
    foreach (var invalid in new[] { "{broken", JsonSerializer.Serialize(new
    {
        OperationId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow,
        Snapshot = snapshot with { DefaultGateway = "" }, ActivationCompleted = true
    }) })
    {
        await File.WriteAllTextAsync(markerPath, invalid);
        var reference = await unknownSafety.CaptureRecoveryAsync(includeCurrentSession: true, default);
        TestAssert.Assert(reference is not null && await unknownSafety.RecoverAsync(reference, default) == ProxyProblemCodes.RecoveryRequired
            && await unknownSafety.EmergencyDisableAsync(default) == ProxyProblemCodes.RecoveryRequired
            && unknownRuntime.DisableCount == 0 && unknownPlatform.RestoreCount == 0
            && await File.ReadAllTextAsync(markerPath) == invalid,
            "Unknown TUN recovery must preserve evidence without invoking runtime or route mutations.");
    }
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
