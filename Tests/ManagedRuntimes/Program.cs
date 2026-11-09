using System.Text.Json;
using RelaxKonOS.Protocol.ManagedRuntimes;

if (args is ["runtime-proxy-direct-reload-crash", var directRoot, var directState, var directRelay])
{
    var result = await ProxyAuthenticatedPublicationChecks.ExecuteDirectReloadFixtureAsync(directRoot, directState, directRelay, default);
    throw new InvalidOperationException($"The private direct writer unexpectedly returned {result}.");
}

if (args is ["runtime-proxy-direct-profile-crash", var profileRoot, var profileState, var profileRelay,
    var profileBinary, var profileId, var profileBarrier])
{
    await ProxyAuthenticatedPublicationChecks.ExecuteDirectProfileFixtureAsync(profileRoot, profileState, profileRelay,
        profileBinary, Guid.Parse(profileId), profileBarrier, default);
    throw new InvalidOperationException("The private profile writer unexpectedly returned.");
}

if (args is ["runtime-proxy-direct-settings-crash", var settingsRoot, var settingsState, var settingsRelay,
    var settingsSocket, var settingsBarrier])
{
    await ProxyAuthenticatedPublicationChecks.ExecuteDirectSettingsCommitFixtureAsync(settingsRoot, settingsState,
        settingsRelay, settingsSocket, settingsBarrier, default);
    throw new InvalidOperationException("The private settings writer unexpectedly returned.");
}

if (args is ["runtime-proxy-direct-compensation-recover", var recoveryRoot, var recoveryState,
    var recoveryRelay, var recoverySocket, var recoveryId])
{
    var recovery = await ProxyAuthenticatedPublicationChecks.RecoverDirectCompensationFixtureAsync(recoveryRoot,
        recoveryState, recoveryRelay, recoverySocket, Guid.Parse(recoveryId), default);
    Console.WriteLine(JsonSerializer.Serialize(recovery));
    return;
}

if (args is ["docker-service-override-only"])
{
    Console.WriteLine($"Docker service override checks passed: {DockerServiceOverrideChecks.Run()}");
    return;
}

if (args is ["component-package-resolution-only"])
{
    Console.WriteLine($"Component package resolution checks passed: {ComponentPackageResolutionChecks.Run()}");
    return;
}

if (args is ["runtime-commit-witness-only"])
{
    Console.WriteLine($"Runtime archive/commit witness checks passed: {await SwitchChecks.RunAsync()}");
    Console.WriteLine($"Runtime removal/commit witness checks passed: {await RemovalChecks.RunAsync()}");
    Console.WriteLine($"Runtime configuration transaction checks passed: {await ConfigurationTransactionChecks.RunAsync()}");
    Console.WriteLine($"Runtime commit fresh observation checks passed: {await CommitObservationChecks.RunAsync()}");
    return;
}

if (args is ["runtime-configuration-network-sync", "success" or "failure"])
{
    await ConfigurationTransactionChecks.CheckNetworkActivationSyncAsync(syncFailed: args[1] == "failure");
    Console.WriteLine($"PASS TUN ARCHIVE ACTIVATION SYNC: {args[1]}");
    return;
}

if (args is ["runtime-configuration-network-recovery-sync", "success" or "failure" or "cleanup-failure"])
{
    var fault = args[1] switch
    {
        "failure" => ConfigurationTransactionChecks.NetworkRecoverySyncFault.BeforeRecovery,
        "cleanup-failure" => ConfigurationTransactionChecks.NetworkRecoverySyncFault.BeforeCleanup,
        _ => ConfigurationTransactionChecks.NetworkRecoverySyncFault.None
    };
    await ConfigurationTransactionChecks.CheckNetworkRecoverySyncAsync(fault);
    Console.WriteLine($"PASS TUN ARCHIVE RECOVERY SYNC: {args[1]}");
    return;
}

if (args is ["runtime-configuration-network-terminal-sync", "activation" or "compensation", "none" or "terminal" or "query" or "admission"])
{
    await ConfigurationTransactionChecks.CheckNetworkTerminalSyncAsync(compensate: args[1] == "compensation", fault: args[2]);
    Console.WriteLine($"PASS TUN ARCHIVE TERMINAL SYNC: {args[1]} {args[2]}");
    return;
}

if (args is ["mihomo-removal-publication-only"])
{
    Console.WriteLine($"Mihomo removal publication checks passed: {await MihomoRemovalPublicationChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-installation-publication-only"])
{
    Console.WriteLine($"Mihomo installation publication checks passed: {await MihomoInstallationPublicationChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-registration-only"])
{
    Console.WriteLine($"Mihomo default publication registration checks passed: {await MihomoRegistrationChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-profile-only"])
{
    Console.WriteLine($"Mihomo profile publication checks passed: {await MihomoProfilePublicationChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-publication-only"])
{
    Console.WriteLine($"Mihomo Server publication transaction checks passed: {await MihomoPublicationTransactionChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-runtime-http-only"])
{
    Console.WriteLine($"Mihomo runtime HTTP checks passed: {await MihomoRuntimeHttpChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-lifecycle-only"])
{
    Console.WriteLine($"Mihomo lifecycle boundary checks passed: {await MihomoLifecycleBoundaryChecks.RunAsync()}");
    return;
}

if (args is ["runtime-download-proxy-only"])
{
    Console.WriteLine($"Runtime download proxy/retry checks passed: {await DownloadProxyChecks.RunAsync()}");
    return;
}

if (args is ["mihomo-resources-only"])
{
    Console.WriteLine($"Mihomo GEO resource checks passed: {await MihomoResourceChecks.RunAsync()}");
    return;
}

if (args is ["runtime-configuration-crash", var configurationRoot, var configurationOperation])
{
    await ConfigurationTransactionChecks.CrashAsync(configurationRoot, Guid.Parse(configurationOperation));
    return;
}

if (args is ["runtime-configuration-only"])
{
    Console.WriteLine($"Runtime configuration transaction checks passed: {await ConfigurationTransactionChecks.RunAsync()}");
    return;
}

if (args is ["-n", var helperFixture] && Path.GetFileName(helperFixture) == InstallationChecks.FixtureFile)
{
    await InstallationChecks.RunHelperFixtureAsync();
    return;
}

if (args is ["runtime-installation-only"])
{
    Console.WriteLine($"Runtime installation coordinator checks passed: {await InstallationChecks.RunAsync()}");
    return;
}

if (args is ["runtime-authorization-only"])
{
    Console.WriteLine($"Runtime current authorization checks passed: {await AuthorizationChecks.RunAsync()}");
    return;
}

if (args is ["runtime-lifecycle-only"])
{
    Console.WriteLine($"Runtime lifecycle boundary checks passed: {await LifecycleChecks.RunAsync()}");
    return;
}

if (args is ["runtime-managed-service-expiry-client", var startupSocket, var startupRoot, "--disabled-startup"])
{
    await ManagedServiceChecks.RunDisabledStartupAsync(startupSocket, startupRoot);
    return;
}
if (args is ["runtime-managed-service-expiry-client", var expirySocket, var expiryRoot])
{
    await ManagedServiceChecks.RunExpiryClientAsync(expirySocket, expiryRoot);
    return;
}
if (args is ["runtime-managed-service-client", var singBoxSocket])
{
    await ManagedServiceChecks.RunSocketClientAsync(singBoxSocket);
    return;
}
if (args is ["runtime-proxy-systemd", var nativeHelper, var nativeServer, var nativeSingBox])
{
    await ProxySystemdRecoveryChecks.RunAsync(nativeHelper, nativeServer, nativeSingBox);
    return;
}
if (args is ["runtime-proxy-systemd", var authHelper, var authServer, var authSingBox, "--authenticated"])
{
    await ProxySystemdRecoveryChecks.RunAsync(authHelper, authServer, authSingBox, authenticated: true);
    return;
}
if (args is ["runtime-proxy-systemd-expiry-worker", var nativeSocket, var nativeRoot])
{
    await ProxySystemdRecoveryChecks.RunExpiryWorkerAsync(nativeSocket, nativeRoot);
    return;
}
if (args is ["runtime-proxy-systemd-interrupt-publication", var publicationRoot])
{
    await ProxySystemdRecoveryChecks.RunInterruptedPublicationAsync(publicationRoot);
    return;
}
if (args is ["runtime-proxy-systemd-tun-worker", var tunRoot, var tunAddress])
{
    await ProxySystemdGuardProbe.RunTunWorkerAsync(tunRoot, System.Net.IPAddress.Parse(tunAddress));
    return;
}
if (args is ["runtime-mihomo-tun-descriptor-worker", var descriptorRoot] && OperatingSystem.IsLinux())
{
    await MihomoTunDescriptorAcceptance.RunAsync(descriptorRoot);
    return;
}
if (args is ["runtime-mihomo-tun-freeze", var freezeInput] && OperatingSystem.IsLinux())
{
    await MihomoTunDescriptorAcceptance.FreezeAsync(freezeInput);
    return;
}
if (args is ["runtime-mihomo-tun-checkpoint", var networkRoot, var networkOperation, var networkAction] && OperatingSystem.IsLinux())
{
    try { await MihomoTunDescriptorAcceptance.CheckpointAsync(networkRoot, Guid.ParseExact(networkOperation, "N"), networkAction); }
    catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException error)
    {
        Console.Error.WriteLine(error.ProblemCode);
        Environment.ExitCode = 3;
    }
    return;
}
if (args is ["runtime-mihomo-tun-retained-removal", var tunRemovalRoot, var tunRemovalSocket, var tunRemovalId, var tunRemovalPhase]
    && OperatingSystem.IsLinux())
{
    await MihomoTunRetainedRemovalAcceptance.RunAsync(tunRemovalRoot, tunRemovalSocket, Guid.Parse(tunRemovalId), tunRemovalPhase);
    return;
}
if (args is ["runtime-squid-package-receipt", var squidPackageRoot, var squidPackageSocket, var squidPackagePhase] && OperatingSystem.IsLinux())
{
    await SquidPackageReceiptAcceptance.RunAsync(squidPackageRoot, squidPackageSocket, squidPackagePhase);
    return;
}
if (args is ["runtime-squid-publication-replay", var squidRoot, var squidSocket, var squidOperation, var squidPort] && OperatingSystem.IsLinux())
{
    await MihomoTunFullServerAcceptance.ProbeSquidReceiptReplayAsync(squidRoot, squidSocket, Guid.Parse(squidOperation), int.Parse(squidPort));
    return;
}
if (args is ["runtime-mihomo-tun-helper-profile", var tunProfileRoot, var tunProfileSocket, var tunProfileMode,
        var tunProfilePublication, var tunProfileExecution, var tunProfileIdentity] && OperatingSystem.IsLinux())
{
    await MihomoTunFullServerAcceptance.ProbeHelperProfileAsync(tunProfileRoot, tunProfileSocket, tunProfileMode,
        Guid.Parse(tunProfilePublication), Guid.Parse(tunProfileExecution), tunProfileIdentity);
    return;
}
if (args is ["runtime-mihomo-tun-named-retry", var tunRetryRoot, var tunRetrySocket, var tunRetryPublication, var tunRetryExecution]
    && OperatingSystem.IsLinux())
{
    await MihomoTunFullServerAcceptance.ProbeNamedStartRecoveryAsync(tunRetryRoot, tunRetrySocket,
        Guid.Parse(tunRetryPublication), Guid.Parse(tunRetryExecution));
    return;
}
if (args is ["runtime-mihomo-tun-full-server", var tunFullRoot, var tunFullSocket, var tunFullPhase] && OperatingSystem.IsLinux())
{
    await MihomoTunFullServerAcceptance.RunAsync(tunFullRoot, tunFullSocket, tunFullPhase);
    return;
}
if (args is ["runtime-mihomo-tun-ipc-server", var ipcTunRoot, var ipcTunPhase] && OperatingSystem.IsLinux())
{
    try { await MihomoTunDescriptorAcceptance.RunIpcServerAsync(ipcTunRoot, ipcTunPhase); }
    catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException error)
    {
        Console.Error.WriteLine(error.ProblemCode);
        Environment.ExitCode = 3;
    }
    return;
}
if (args is ["runtime-managed-service-live", var singBoxExecutable])
{
    Console.WriteLine($"PASS LIVE SING-BOX HELPER PUBLICATION: {await ManagedServiceChecks.RunAsync(singBoxExecutable)} checks");
    return;
}
if (args is ["runtime-managed-service-only"])
{
    Console.WriteLine($"Managed runtime service checks passed: {await ManagedServiceChecks.RunAsync()}");
    return;
}

if (args is ["runtime-removal-only"])
{
    Console.WriteLine($"Runtime archive removal checks passed: {await RemovalChecks.RunAsync()}");
    return;
}

if (args is ["runtime-recovery-only"])
{
    Console.WriteLine($"Runtime recovery entry checks passed: {await RecoveryChecks.RunAsync()}");
    return;
}

if (args is ["runtime-cancel-only"])
{
    Console.WriteLine($"Runtime cancellation checks passed: {await CancellationChecks.RunAsync()}");
    return;
}

if (args is ["runtime-dependency-only"])
{
    Console.WriteLine($"Runtime dependency checks passed: {DependencyChecks.Run()}");
    return;
}

if (args is ["run", "-c", var fixtureConfiguration])
{
    await LinuxSystemdHostChecks.RunFixtureAsync(fixtureConfiguration);
    return;
}

if (args is ["linux-systemd-host"] && OperatingSystem.IsLinux())
{
    Console.WriteLine($"Linux systemd host checks passed: {await LinuxSystemdHostChecks.RunAsync()}");
    return;
}

if (args is ["linux-listeners-only"])
{
    Console.WriteLine($"Linux listener checks passed: {LinuxListenerChecks.Run()}");
    return;
}

if (args is ["native-sing-box-archive", var nativeArchive])
{
    Console.WriteLine($"Real sing-box archive adapter checks passed: {await SingBoxArchiveChecks.RunAsync(nativeArchive)}");
    return;
}

if (args is ["native-sing-box-runtime", var olderRuntime, var currentRuntime] && OperatingSystem.IsLinux())
{
    Console.WriteLine($"Real sing-box runtime acceptance checks passed: {await SingBoxRuntimeAcceptance.RunAsync(olderRuntime, currentRuntime)}");
    return;
}

if (args is ["native-mihomo-archive", var olderMihomo, var currentMihomo] && OperatingSystem.IsLinux())
{
    Console.WriteLine($"Real Mihomo archive acceptance checks passed: {await MihomoArchiveAcceptance.RunAsync(olderMihomo, currentMihomo)}");
    return;
}

if (args is ["native-mihomo-systemd", var olderSystemMihomo, var currentSystemMihomo] && OperatingSystem.IsLinux())
{
    Console.WriteLine($"Real Mihomo systemd acceptance checks passed: {await MihomoArchiveAcceptance.RunAsync(olderSystemMihomo, currentSystemMihomo, systemd: true)}");
    return;
}

if (args is ["native-probe-fixture", var mode])
{
    NativeProcessChecks.RunFixture(mode);
    return;
}

if (args is ["runtime-switch-crash", var crashRoot, var phase, var operationId])
{
    await SwitchChecks.RunCrashChildAsync(crashRoot, phase, Guid.Parse(operationId));
    return;
}

var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
var target = new RuntimeTarget("linux", "arm64", "debian-13", "glibc", RuntimeInstallMode.Archive, "systemd");
var source = new RuntimeSourceIdentity("sing-box-official", "SagerNet/sing-box", true, RuntimeTrust.Confirmed, 3);
var artifact = new RuntimeArtifact("immutable-2", new string('a', 64), 1024, "tar.gz", "sing-box", "signed-manifest:key-1",
    new(1024, 8, [new("sing-box", 4, new string('b', 64), true)]));
var release = new RuntimeRelease("opaque-2", "sing-box", "v2.0.0", "2.0.0", RuntimeChannel.Stable,
    source.SourceId, target, artifact, "test-capability", true, RuntimeTrust.Confirmed, RuntimeEvidence.Unverified, now);
var catalog = new RuntimeCatalog(source.SourceId, 7, 3, now.AddHours(1), true, [release]);
var installation = new RuntimeInstallation(Guid.NewGuid(), "host-1", "sing-box", 5,
    RuntimeOwnership.Managed, target, "opaque-1", false);
var request = new RuntimePlanRequest(installation.InstallationId, installation.HostId, RuntimeChange.Update,
    5, 7, 3, release.ReleaseId, "operation-1");
var count = 0;
// Planning now demands a Host measurement for the requested release; the admission checks below are about
// trust, ownership and revisions, so they hand it a report where every declared requirement is satisfied.
RuntimeDependencyReport Measured(RuntimeRelease? candidate = null) =>
    new([.. RuntimeArchiveDependencies.For(candidate ?? release).Select(x =>
        new RuntimeDependencyObservation(x.Kind, x.Identity, RuntimeDependencyState.Satisfied, x.MinimumBytes))]);
RuntimePlanResult Plan(RuntimePlanRequest? r = null, RuntimeInstallation? i = null,
    RuntimeSourceIdentity? s = null, RuntimeCatalog? c = null, long highWater = 7, RuntimeDependencyReport? d = null) =>
    RuntimePlanning.Create(r ?? request, i ?? installation, s ?? source, c ?? catalog, highWater, now, d ?? Measured());
void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
void Denied(string code, RuntimePlanResult result) => Check(!result.Allowed && result.Plan is null
    && result.Problems.Contains("runtime." + code), code);

Check(Plan().Allowed, "Authenticated supported release can be proposed before native validation");
Denied("state_unknown", Plan(i: installation with { Ownership = RuntimeOwnership.Unknown }));
Denied("external_installation", Plan(i: installation with { Ownership = RuntimeOwnership.External }));
Denied("recovery_required", Plan(i: installation with { RecoveryRequired = true }));
Denied("state_revision_conflict", Plan(r: request with { ExpectedStateRevision = 4 }));
Denied("installation_mismatch", Plan(r: request with { HostId = "host-2" }));
Denied("idempotency_key_invalid", Plan(r: request with { IdempotencyKey = " " }));
Denied("source_untrusted", Plan(s: source with { Enabled = false }));
Denied("source_untrusted", Plan(s: source with { Trust = RuntimeTrust.Revoked }));
Denied("trust_revision_conflict", Plan(s: source with { TrustRevision = 4 }));
Denied("catalog_revision_conflict", Plan(r: request with { ExpectedCatalogRevision = 6 }));
Denied("catalog_replayed", Plan(highWater: 8));
Denied("catalog_expired", Plan(c: catalog with { ExpiresAt = now }));
Denied("catalog_unverified", Plan(c: catalog with { SignatureVerified = false }));
Denied("release_unknown_or_ambiguous", Plan(r: request with { ReleaseId = "latest" }));
Denied("release_unknown_or_ambiguous", Plan(c: catalog with { Releases = [release, release] }));
foreach (var trust in new[] { RuntimeTrust.Unconfirmed, RuntimeTrust.Revoked, RuntimeTrust.Conflict })
    Denied("release_untrusted", Plan(c: catalog with { Releases = [release with { Trust = trust }] }));
Denied("target_unsupported", Plan(c: catalog with { Releases = [release with { Target = target with { Architecture = "amd64" } }] }));
Denied("configuration_unsupported", Plan(c: catalog with { Releases = [release with { ConfigurationSupported = false }] }));
var prerelease = catalog with { Releases = [release with { Channel = RuntimeChannel.Prerelease }] };
Denied("channel_not_allowed", Plan(c: prerelease));
Check(Plan(r: request with { AllowPrerelease = true }, c: prerelease).Allowed, "Explicit prerelease choice");
Denied("artifact_invalid", Plan(c: catalog with { Releases = [release with { Artifact = artifact with { Sha256 = "bad" } }] }));
Denied("target_unchanged", Plan(i: installation with { ActiveReleaseId = release.ReleaseId }));
Denied("repair_target_mismatch", Plan(r: request with { Change = RuntimeChange.Repair }));
Check(Plan(r: request with { Change = RuntimeChange.Repair }, i: installation with { ActiveReleaseId = release.ReleaseId }).Allowed,
    "Same-version repair has distinct semantics");
Check(Plan(r: request with { Change = RuntimeChange.Install }, i: installation with { Ownership = RuntimeOwnership.Absent }).Allowed,
    "Absent installation may install");
var uninstall = request with { Change = RuntimeChange.Uninstall, ReleaseId = null };
Check(uninstall.KeepData, "Uninstall retains data by default");
Check(Plan(r: uninstall, s: source with { Trust = RuntimeTrust.Revoked }, c: catalog with { ExpiresAt = now }).Allowed,
    "Revoked expired source does not prevent owned removal");
Denied("release_unexpected", Plan(r: request with { Change = RuntimeChange.Uninstall }));
var frozen = Plan().Plan!;
catalog = catalog with { Revision = 8, Releases = [] };
Check(frozen.Release!.Artifact == artifact && frozen.Release.ReleaseId == "opaque-2", "Plan freezes exact release independently of catalog refresh");
Check(JsonSerializer.Serialize(JsonSerializer.Deserialize<RuntimeChangePlan>(JsonSerializer.Serialize(frozen))) == JsonSerializer.Serialize(frozen),
    "Current contract JSON roundtrip preserves nested file declarations");
Console.WriteLine($"Managed runtime admission checks passed: {count}");
Console.WriteLine($"Signed catalog and trust-store checks passed: {CatalogChecks.Run()}");
Console.WriteLine($"Helper/Server integration checks passed: {await RuntimeIntegrationChecks.RunAsync()}");
Console.WriteLine($"Runtime archive checks passed: {await ArchiveChecks.RunAsync()}");
Console.WriteLine($"Runtime artifact preparation checks passed: {await PreparationChecks.RunAsync()}");
Console.WriteLine($"Runtime archive transaction checks passed: {await SwitchChecks.RunAsync()}");
Console.WriteLine($"Runtime version/native build checks passed: {NativeVersionChecks.Run()}");
Console.WriteLine($"Mihomo data revision checks passed: {await MihomoDataChecks.RunAsync()}");
Console.WriteLine($"Mihomo YAML/configuration policy checks passed: {MihomoConfigurationPolicyChecks.Run()}");
Console.WriteLine($"Native child process checks passed: {await NativeProcessChecks.RunAsync()}");
Console.WriteLine($"Windows native host/policy checks passed: {await WindowsHostChecks.RunAsync()}");
Console.WriteLine($"Linux listener checks passed: {LinuxListenerChecks.Run()}");
Console.WriteLine($"Runtime current authorization checks passed: {await AuthorizationChecks.RunAsync()}");
Console.WriteLine($"Typed Mihomo authorization checks passed: {await MihomoAuthorizationChecks.RunAsync()}");
Console.WriteLine($"Mihomo installation authority checks passed: {await MihomoInstallationAuthorizationChecks.RunAsync()}");
Console.WriteLine($"Mihomo installation publication checks passed: {await MihomoInstallationPublicationChecks.RunAsync()}");
Console.WriteLine($"Mihomo removal publication checks passed: {await MihomoRemovalPublicationChecks.RunAsync()}");
Console.WriteLine($"Mihomo publication authority checks passed: {await MihomoPublicationChecks.RunAsync()}");
Console.WriteLine($"Mihomo Server publication transaction checks passed: {await MihomoPublicationTransactionChecks.RunAsync()}");
Console.WriteLine($"Mihomo profile publication checks passed: {await MihomoProfilePublicationChecks.RunAsync()}");
Console.WriteLine($"Mihomo default publication registration checks passed: {await MihomoRegistrationChecks.RunAsync()}");
Console.WriteLine($"Mihomo runtime HTTP checks passed: {await MihomoRuntimeHttpChecks.RunAsync()}");
Console.WriteLine($"Mihomo GEO resource checks passed: {await MihomoResourceChecks.RunAsync()}");
Console.WriteLine($"Mihomo lifecycle boundary checks passed: {await MihomoLifecycleBoundaryChecks.RunAsync()}");
Console.WriteLine($"Runtime lifecycle boundary checks passed: {await LifecycleChecks.RunAsync()}");
Console.WriteLine($"Runtime installation coordinator checks passed: {await InstallationChecks.RunAsync()}");
Console.WriteLine($"Managed runtime service checks passed: {await ManagedServiceChecks.RunAsync()}");
Console.WriteLine($"Runtime archive removal checks passed: {await RemovalChecks.RunAsync()}");
Console.WriteLine($"Runtime recovery entry checks passed: {await RecoveryChecks.RunAsync()}");
Console.WriteLine($"Runtime configuration transaction checks passed: {await ConfigurationTransactionChecks.RunAsync()}");
Console.WriteLine($"Runtime commit fresh observation checks passed: {await CommitObservationChecks.RunAsync()}");
Console.WriteLine($"Runtime cancellation checks passed: {await CancellationChecks.RunAsync()}");
Console.WriteLine($"Runtime dependency checks passed: {DependencyChecks.Run()}");
Console.WriteLine($"Runtime download proxy/retry checks passed: {await DownloadProxyChecks.RunAsync()}");
