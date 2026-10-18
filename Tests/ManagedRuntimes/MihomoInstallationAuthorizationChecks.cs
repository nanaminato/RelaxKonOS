using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoInstallationAuthorizationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
        async Task Denied(string code, Func<Task> action)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected installation authority refusal: " + code);
        }
        using (var f = new Fixture())
        {
            Check((await f.Reader().ReadAsync(default)).Published!.Yaml == f.Material.Yaml,
                "Only the bound durable install receives its candidate without a prior publication");
            var grant = f.Grant;
            f.Grant = grant with { OperationId = Guid.NewGuid() };
            await Denied("authorization_unavailable", () => f.Reader().ReadAsync(default));
            f.Grant = grant with { PlanIdentity = new string('0', 64) };
            await Denied("authorization_unavailable", () => f.Reader().ReadAsync(default));
            f.Grant = grant;
            await Denied("authorization_unavailable", () => f.Reader(f.Plan with
                { Request = f.Plan.Request with { IdempotencyKey = "different-frozen-plan" } }).ReadAsync(default));
            f.Activate();
            Check((await f.Reader().ReadAsync(default)).Published!.Yaml == f.Material.Yaml,
                "The same install remains bound after its own atomic program pointer switch");
        }
        using (var f = new Fixture(retained: true))
        {
            Check((await f.Reader().ReadAsync(default)).Published!.Yaml == f.Material.Yaml,
                "Retained-data reinstall authorizes the unchanged confirmed configuration");
            f.Grant = f.Grant with { Candidate = f.Material with { Yaml = f.Material.Yaml + "# unrelated change\n" } };
            await Denied("authorization_changed", () => f.Reader().ReadAsync(default));
        }
        using (var f = new Fixture())
        {
            await using var callback = new RuntimeAuthorizationServer<MihomoInstallationAuthorization>(Guid.NewGuid(), _ => Task.FromResult(f.Grant));
            var received = await RuntimeAuthorizationClient.ReadAsync<MihomoInstallationAuthorization>(callback.Channel, callback.Channel.OperationId, default);
            Check(received.OperationId == f.Operation && received.PlanIdentity == f.Grant.PlanIdentity,
                "Fresh recovery channels can carry the original installation identity");
            Check(!f.Grant.ToString().Contains(f.Material.ControllerSecret, StringComparison.Ordinal), "Install grant diagnostics redact candidate credentials");
            await Denied("authorization_unavailable", () => RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(callback.Channel, callback.Channel.OperationId, default));
            await Denied("authorization_unavailable", () => RuntimeAuthorizationClient.ReadAsync<MihomoConfigurationAuthorization>(callback.Channel, callback.Channel.OperationId, default));
        }
        using (var f = new Fixture())
        {
            await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(Guid.NewGuid(), _ =>
                Task.FromResult(new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddSeconds(30), f.Material)));
            await Denied("authorization_unavailable", () => RuntimeAuthorizationClient.ReadAsync<MihomoInstallationAuthorization>(callback.Channel, callback.Channel.OperationId, default));
        }
        using (var f = new Fixture())
        {
            var publication = new MihomoPublicationStore(f, new EphemeralDataProtectionProvider());
            var provider = new MihomoRuntimeAuthorizationProvider(publication, f, f);
            await publication.BeginInstallationAsync(f.Operation, 0, f.Material, f.Plan, default);
            var received = await provider.ReadInstallationAsync(f.Operation, default);
            Check(received.Candidate?.Yaml == f.Material.Yaml && received.PlanIdentity == f.Grant.PlanIdentity
                && (await publication.ReadAsync(default)).Published is null,
                "Server candidate authority freezes the installation without inventing a confirmed publication");
            await Denied("publication_unconfirmed", () => provider.ReadAsync(default));
            await Denied("publication_unconfirmed", () => provider.ReadInstallationAsync(Guid.NewGuid(), default));
            await Denied("idempotency_conflict", () => publication.BeginInstallationAsync(f.Operation, 0, f.Material,
                f.Plan with { Request = f.Plan.Request with { IdempotencyKey = "different-install" } }, default));
            f.CurrentSecret = "revoked-controller-secret";
            Check((await provider.ReadInstallationAsync(f.Operation, default)).Candidate is null,
                "Fresh Server secret revocation removes candidate authority before installation");
        }
        return count;
    }

    private sealed class Fixture : IDisposable, IProxyPlatformPaths, IProxyControllerSecretStore
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mihomo-install-authority-" + Guid.NewGuid().ToString("N"));
        internal Guid Operation { get; } = Guid.NewGuid();
        internal RuntimeArchiveStateStore States { get; }
        internal RuntimeChangePlan Plan { get; }
        internal MihomoInstallationAuthorization Grant { get; set; }
        internal string? CurrentSecret = "candidate-installation-secret";
        internal MihomoPublishedRuntime Material { get; } = new("mixed-port: 17890\n", [], 9090, "candidate-installation-secret", [new("127.0.0.1", 17890)]);
        internal Fixture(bool retained = false)
        {
            States = new(Path.Combine(root, "state"), path => Directory.CreateDirectory(path));
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var identity = MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(Material.Yaml));
            var initial = States.Initialize(new(Guid.NewGuid(), "installation-host", "mihomo", 0, RuntimeOwnership.Absent, target, null, false), retained ? identity : null);
            var artifact = new RuntimeArtifact("fixture", new string('a', 64), 4096, "gz", "mihomo", "signed-manifest:fixture",
                new(4096, 1, [new("mihomo", 16, new string('b', 64), true)]));
            var release = new RuntimeRelease("release-1", "mihomo", "v1.19.30", "1.19.30", RuntimeChannel.Stable, "fixture-source",
                target, artifact, "mihomo-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow);
            Plan = new(new(initial.Installation.InstallationId, initial.Installation.HostId, RuntimeChange.Install, 0, 1, 1,
                release.ReleaseId, "installation-authority"), "mihomo", release, null);
            using var session = States.Acquire();
            session.Save(initial with { Installation = initial.Installation with { StateRevision = 1 },
                Pending = new(Operation, new string('c', 64), new string('d', 64), Plan, InstallationStage.Preflight,
                    null, null, null, false, null, null) });
            Grant = new(Operation, DateTimeOffset.UtcNow.AddSeconds(30), MihomoRuntimeMaterial.InstallationPlanIdentity(Plan), Material);
        }
        internal MihomoInstallationAuthorizationReader Reader(RuntimeChangePlan? plan = null) => new(States, plan ?? Plan, _ => Task.FromResult(Grant));
        internal void Activate()
        {
            using var session = States.Acquire();
            var state = session.State!;
            var revision = new RuntimeStoredRevision(Operation, Plan.Release!, "1.19.30", MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(Material.Yaml)), 1, 1);
            session.Save(state with { Installation = state.Installation with { StateRevision = 2, Ownership = RuntimeOwnership.Managed,
                ActiveReleaseId = Plan.Release!.ReleaseId }, Active = revision, Revisions = [revision], ConfigurationIdentity = revision.ConfigurationIdentity,
                Pending = state.Pending! with { SwitchStarted = true, Prepared = new(revision, false, false),
                    Snapshot = new(false, false, null, null, Operation), Stage = InstallationStage.Activating } });
        }
        public Task<string?> ReadExistingAsync(CancellationToken ct) => Task.FromResult(CurrentSecret);
        public Task<string> GetOrCreateAsync(CancellationToken ct) => throw new Exception("Installation grant generated a credential");
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "server", "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "server", "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "server", "data");
        public string GetStateDirectory() => Path.Combine(root, "server", "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "server", "logs");
        public void Dispose() => Directory.Delete(root, true);
    }
}
