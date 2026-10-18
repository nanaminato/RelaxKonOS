using System.Text.Json;
using RelaxKonOS.Protocol.ManagedRuntimes;

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
Console.WriteLine($"Native child process checks passed: {await NativeProcessChecks.RunAsync()}");
Console.WriteLine($"Windows native host/policy checks passed: {await WindowsHostChecks.RunAsync()}");
Console.WriteLine($"Linux listener checks passed: {LinuxListenerChecks.Run()}");
Console.WriteLine($"Runtime current authorization checks passed: {await AuthorizationChecks.RunAsync()}");
Console.WriteLine($"Runtime lifecycle boundary checks passed: {await LifecycleChecks.RunAsync()}");
Console.WriteLine($"Runtime installation coordinator checks passed: {await InstallationChecks.RunAsync()}");
Console.WriteLine($"Managed runtime service checks passed: {await ManagedServiceChecks.RunAsync()}");
Console.WriteLine($"Runtime archive removal checks passed: {await RemovalChecks.RunAsync()}");
Console.WriteLine($"Runtime recovery entry checks passed: {await RecoveryChecks.RunAsync()}");
Console.WriteLine($"Runtime cancellation checks passed: {await CancellationChecks.RunAsync()}");
Console.WriteLine($"Runtime dependency checks passed: {DependencyChecks.Run()}");
