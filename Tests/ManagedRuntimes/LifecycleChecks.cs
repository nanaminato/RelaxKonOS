using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Protocol.Observability;

internal static class LifecycleChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Protect(string path) => Directory.CreateDirectory(path);
            var states = new RuntimeArchiveStateStore(root + "/runtime/state", Protect);
            var sources = new RuntimeSourceStore(root + "/sources", Protect);
            var lifecycle = new SingBoxRuntimeLifecycle(root + "/runtime", states, sources, new AbsentHost(), Protect);
            var created = 0;
            SingBoxRuntimeLifecycle Create() { created++; return lifecycle; }
            var query = new PrivilegedOperationRequest(PrivilegedOperationKind.SingBoxRuntimeQuery, OperationId: Guid.NewGuid());
            var observation = await SingBoxRuntimeOperations.ExecuteAsync(query, Create);
            Check(observation.Success, "Helper lifecycle query observes installation independently of a catalog");
            var snapshot = JsonSerializer.Deserialize<RuntimeInstallationSnapshot>(Convert.FromBase64String(observation.OutputBase64!))!;
            Check(snapshot.Installation.Ownership == RuntimeOwnership.Absent && snapshot.ActiveRevisionId is null,
                "A fresh empty fixed scope has an explicit absent observation");
            Check((await lifecycle.QueryAsync(default)).Installation.InstallationId == snapshot.Installation.InstallationId,
                "Installation and host identities persist across reads");
            var originalState = states.Read()!;
            // Simulate a disk snapshot copied from another machine; the normal store API forbids changing host identity.
            await File.WriteAllBytesAsync(root + "/runtime/state/archive-state.json", RuntimeJson.Serialize(originalState with { Installation = originalState.Installation with { HostId = "other-host" } }));
            try { await lifecycle.QueryAsync(default); throw new Exception("Another host's installation state was adopted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.installation_mismatch", "Existing installation remains bound to the actual host identity"); }
            await File.WriteAllBytesAsync(root + "/runtime/state/archive-state.json", RuntimeJson.Serialize(originalState));
            var malformed = new[] { query with { Path = "forbidden" }, query with { RuntimeLifecycle = new() },
                query with { OperationId = null }, query with { Operation = PrivilegedOperationKind.FileWrite },
                query with { Operation = PrivilegedOperationKind.SingBoxRuntimePlan, RuntimeLifecycle = new(SourceId: "../other") },
                query with { Operation = PrivilegedOperationKind.SingBoxRuntimeExecute, RuntimeLifecycle = new() },
                query with { Operation = PrivilegedOperationKind.SingBoxRuntimeRecover, RuntimeLifecycle = new(Authorization: new(Guid.NewGuid(), Guid.NewGuid(), new string('0', 64))) } };
            foreach (var invalid in malformed)
                Check(!(await SingBoxRuntimeOperations.ExecuteAsync(invalid, Create)).Success, "Mixed or incomplete lifecycle input is rejected");
            Check(created == 1, "Invalid requests never construct a privileged lifecycle");
            var mixedId = Guid.NewGuid();
            var mixed = await PrivilegedOperationExecutor.ExecuteAsync(new(PrivilegedOperationKind.FileRead,
                OperationId: mixedId, Correlation: CorrelationContext.Create(mixedId, "privileged.operation"), RuntimeLifecycle: new()), new([], [], [], []));
            Check(!mixed.Success && mixed.ProblemCode == PrivilegedProblemCode.InvalidRequest,
                "Ordinary Helper operations cannot accept a hidden lifecycle payload");

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var source = new RuntimeSource("fixture", "sing-box", "test", RuntimeSourceKind.HttpsManifest,
                "https://example.com/catalog/", "manifest.json", "key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                true, 0, [RuntimeChannel.Stable], 65536, 3600, ["https://example.com/artifacts/"], [], []);
            var now = DateTimeOffset.UtcNow;
            sources.Confirm(source, 0, source.ConfirmationFingerprint, "test", now);
            if (OperatingSystem.IsLinux())
            {
                Check((File.GetUnixFileMode(root + "/runtime/state/instance.lock") & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                    "State lock creation has private permissions independent of caller umask");
                Check((File.GetUnixFileMode(root + "/sources/registry.lock") & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                    "Source ledger lock creation has private permissions independent of caller umask");
            }
            var artifact = new RuntimeArtifact("fixture-binary", new string('a', 64), 1024, "zip", "sing-box", "key",
                new(1024, 8, [new("sing-box", 4, new string('b', 64), true)]));
            var release = new ManifestRelease("fixture-release", "sing-box", "v1.14.3", RuntimeChannel.Stable,
                snapshot.Installation.Target, artifact, "https://example.com/artifacts/binary.zip", now);
            var manifest = new RuntimeManifest(1, source.SourceId, 1, now, now.AddMinutes(30), [release], []);
            var bytes = RuntimeJson.Serialize(manifest);
            var signed = RuntimeJson.Serialize(new SignedRuntimeCatalog(1, source.KeyId, Convert.ToBase64String(bytes),
                Convert.ToBase64String(key.SignData(RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(bytes).ToArray(),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
            sources.Accept(source.SourceId, 1, signed, now, _ => new("1.14.3", SingBoxRuntimeSupport.Capability, true));
            var request = new RuntimePlanRequest(snapshot.Installation.InstallationId, snapshot.Installation.HostId,
                RuntimeChange.Install, snapshot.Installation.StateRevision, 1, 1, release.ReleaseId, "lifecycle-test");
            var allowed = await lifecycle.PlanAsync(source.SourceId, request, default);
            Check(allowed.Allowed && allowed.Plan!.Release!.Artifact.Sha256 == artifact.Sha256,
                "Plan is resolved from the Helper-protected signed catalog");
            var stale = await lifecycle.PlanAsync(source.SourceId, request with { ExpectedStateRevision = 99 }, default);
            Check(stale.Problems.Contains("runtime.state_revision_conflict"), "Stale state is rejected before an operation");
            using (var held = states.Acquire())
            {
                try { await lifecycle.PlanAsync(source.SourceId, request, default); throw new Exception("Instance lock bypassed"); }
                catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.instance_busy", "Planning uses the same instance lock as mutation"); }
            }
            // Caller-controlled plans must not narrow the signed extraction/file-verification layout.
            var frozen = allowed.Plan!;
            var forged = frozen with { Release = frozen.Release! with { Artifact = artifact with
                { Layout = artifact.Layout with { Files = [artifact.Layout.Files[0] with { Length = 5 }] } } } };
            var operationId = Guid.NewGuid();
            var channel = new RuntimeAuthorizationChannel(Guid.NewGuid(), operationId, new string('0', 64));
            var result = await lifecycle.ExecuteAsync(operationId, forged, channel, default);
            Check(result.Outcome == RuntimeSwitchOutcome.Failed && result.ProblemCode == "runtime.release_untrusted",
                "Forged artifact layout fails independent Helper authorization before download or execution");
            var evidenceId = Guid.NewGuid();
            var forgedEvidence = frozen with { Request = request with { ExpectedStateRevision = states.Read()!.Installation.StateRevision,
                IdempotencyKey = "evidence-tamper" }, Release = frozen.Release! with { Evidence = RuntimeEvidence.Interoperability } };
            var evidence = await lifecycle.ExecuteAsync(evidenceId, forgedEvidence, channel with { OperationId = evidenceId }, default);
            Check(evidence.Outcome == RuntimeSwitchOutcome.Failed && evidence.ProblemCode == "runtime.release_untrusted",
                "Caller cannot promote unverified catalog metadata to interoperability evidence");
            var disabled = source with { Enabled = false };
            sources.Confirm(disabled, 1, disabled.ConfirmationFingerprint, "test", now);
            var retry = await lifecycle.ExecuteAsync(operationId, forged, channel, default);
            Check(retry == result, "A frozen retry returns its original receipt even after state and trust revisions change");
            Check((await lifecycle.QueryAsync(default, operationId)).Receipt == result,
                "Startup recovery can query the exact durable operation receipt rather than infer success from liveness");
            var stateText = File.ReadAllText(root + "/runtime/state/archive-state.json");
            Check(!stateText.Contains(channel.Secret, StringComparison.Ordinal) && !stateText.Contains(channel.ChannelId.ToString(), StringComparison.OrdinalIgnoreCase),
                "Channel secret and endpoint are absent from the durable transaction journal");

            // Isolate the activation boundary: registration can take longer than an authorization lease.
            var activationRoot = root + "/activation";
            var activationId = Guid.NewGuid();
            var operationDirectory = activationRoot + "/operations/" + activationId.ToString("N");
            var revisionDirectory = activationRoot + "/versions/" + activationId.ToString("N");
            Directory.CreateDirectory(operationDirectory); Directory.CreateDirectory(revisionDirectory);
            var content = SingBoxConfiguration.Render(new(new(0, []), [], []));
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            await File.WriteAllTextAsync(operationDirectory + "/candidate.json", content);
            await File.WriteAllTextAsync(revisionDirectory + "/sing-box", "test");
            var authority = new ActivationAuthorization();
            var activationHost = new ActivationHost(() => authority.Unavailable = true);
            var activationAdapter = new SingBoxArchiveAdapter(activationRoot,
                new(activationRoot + "/state", Protect), sources, new NoTransport(), activationHost, authority, Protect);
            var revision = new RuntimeStoredRevision(activationId, frozen.Release!, "1.14.3", identity, 1, 1);
            var pending = new RuntimePendingSwitch(activationId, new string('a', 64), new string('b', 64), frozen,
                RelaxKonOS.Protocol.Installations.InstallationStage.Activating, revision,
                new(true, false, identity, activationId, activationId), new(revision, true, false), true, null, null);
            try { await activationAdapter.ActivateAsync(pending, default); throw new Exception("Revoked authority started candidate"); }
            catch (RuntimePolicyException ex)
            {
                Check(ex.ProblemCode == "runtime.authorization_unavailable" && activationHost.Starts == 0 && activationHost.Stops == 1,
                    "Revocation during registration blocks candidate start, not merely the later health check");
            }
            authority.Unavailable = false;
            await File.WriteAllTextAsync(operationDirectory + "/previous.json", content);
            await File.WriteAllTextAsync(operationDirectory + "/service.json", JsonSerializer.Serialize(new SingBoxServiceObservation(true, true, true, false, revisionDirectory + "/sing-box", identity)));
            try { await activationAdapter.RestoreAsync(pending, default); throw new Exception("Revoked authority restarted previous program"); }
            catch (RuntimePolicyException ex)
            {
                Check(ex.ProblemCode == "runtime.authorization_unavailable" && activationHost.Starts == 0,
                    "Restoration rechecks authority after registration before restarting retained credentials");
            }
            var stopState = originalState with { Active = revision, Revisions = [revision], Pending = pending,
                ConfigurationIdentity = identity };
            var stopHost = new StopHost(new(true, true, true, true, revisionDirectory + "/sing-box", identity));
            var stopped = await SingBoxManagedStop.ExecuteAsync(activationRoot, stopState, stopHost, default);
            Check(!stopped.Running && stopped.RecoveryRequired && !stopped.Applied && stopped.BootEnabled == true
                && stopHost.Stops == 1, "Emergency stop works during recovery without reading authorization or changing autostart");
            Check(stopState.Pending == pending && stopState.Active == revision && stopState.Receipts.Length == 0,
                "Stopping cannot erase recovery checkpoints or manufacture a switch receipt");
            foreach (var stopObservation in new[] {
                new SingBoxServiceObservation(true, false, true, false, revisionDirectory + "/sing-box", identity),
                new SingBoxServiceObservation(true, true, true, false, activationRoot + "/other/sing-box", identity) })
            {
                var external = new StopHost(stopObservation);
                try { await SingBoxManagedStop.ExecuteAsync(activationRoot, stopState, external, default); throw new Exception("External process stopped"); }
                catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.external_installation" && external.Stops == 0,
                    "Emergency stop cannot take ownership of an external service or unknown program revision"); }
            }
            var stuck = new StopHost(new(true, true, true, false, revisionDirectory + "/sing-box", identity)) { IgnoreStop = true };
            try { await SingBoxManagedStop.ExecuteAsync(activationRoot, stopState, stuck, default); throw new Exception("Unverified stop accepted"); }
            catch (RuntimePolicyException ex) { Check(ex.ProblemCode == "runtime.stop_not_verified", "Stop command success does not prove process termination"); }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }

    private sealed class NoTransport : IRuntimeArchiveTransport
    {
        public Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct) => throw new Exception("No download expected");
    }
    private sealed class StopHost(SingBoxServiceObservation observation) : ISingBoxArchiveHost
    {
        internal int Stops;
        internal bool IgnoreStop;
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(observation);
        public Task StopAsync(CancellationToken ct) { Stops++; if (!IgnoreStop) observation = observation with { Running = false, AppliedConfigurationIdentity = null }; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken ct) => throw new Exception("Stop cannot start a service");
        public Task ConfigureAsync(string binary, string configuration, string identity, bool bootEnabled, CancellationToken ct) => throw new Exception("Stop cannot change registration");
        public Task SetBootAsync(bool enabled, CancellationToken ct) => throw new Exception("Stop cannot change startup");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("Stop cannot remove registration");
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) => throw new Exception("Stop cannot use listener health as ownership");
    }
    private sealed class ActivationAuthorization : ISingBoxArchiveAuthorization
    {
        internal bool Unavailable;
        public Task<SingBoxCurrentAuthorization> ReadAsync(CancellationToken ct) => Unavailable
            ? throw new RuntimePolicyException("runtime.authorization_unavailable") : Task.FromResult(new SingBoxCurrentAuthorization(null, DateTimeOffset.UtcNow.AddSeconds(30)));
    }
    private sealed class ActivationHost(Action afterConfigure) : ISingBoxArchiveHost
    {
        internal int Starts;
        internal int Stops;
        public Task StopAsync(CancellationToken ct) { Stops++; return Task.CompletedTask; }
        public Task StartAsync(CancellationToken ct) { Starts++; return Task.CompletedTask; }
        public Task ConfigureAsync(string binary, string configuration, string identity, bool bootEnabled, CancellationToken ct) { afterConfigure(); return Task.CompletedTask; }
        public Task SetBootAsync(bool enabled, CancellationToken ct) => throw new Exception("No startup change expected");
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => throw new Exception("No post-start observation expected");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("No registration removal expected");
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) => throw new Exception("No health check expected");
    }

    private sealed class AbsentHost : ISingBoxArchiveHost
    {
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(new SingBoxServiceObservation(false, true, false, false, null, null));
        public Task StopAsync(CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task StartAsync(CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task ConfigureAsync(string binary, string configuration, string identity, bool bootEnabled, CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task SetBootAsync(bool enabled, CancellationToken ct) => throw new Exception("No service mutation expected");
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) => throw new Exception("No service mutation expected");
    }
}
