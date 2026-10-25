using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

internal static class CommitObservationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "commit-observe-" + Guid.NewGuid().ToString("N"));
        var revisionId = Guid.NewGuid();
        var revision = Path.Combine(root, "versions", revisionId.ToString("N"));
        Directory.CreateDirectory(revision);
        try
        {
            var program = Encoding.UTF8.GetBytes("trusted"); var configuration = Encoding.UTF8.GetBytes("{}");
            var binary = Path.Combine(revision, "sing-box"); var config = Path.Combine(root, "config.json");
            await File.WriteAllBytesAsync(binary, program); await File.WriteAllBytesAsync(config, configuration);
            var identity = Convert.ToHexString(SHA256.HashData(configuration));
            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            var release = new RuntimeRelease("release", "sing-box", "v1.14.3", "1.14.3", RuntimeChannel.Stable, "source", target,
                new("artifact", new string('a', 64), 1024, "tar.gz", "sing-box", "key",
                    new(1024, 4, [new("sing-box", program.Length, Convert.ToHexString(SHA256.HashData(program)), true)])),
                "sing-box-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration, null);
            var active = new RuntimeStoredRevision(revisionId, release, "1.14.3", identity, 1, 1);
            var installation = new RuntimeInstallation(Guid.NewGuid(), "host", "sing-box", 1, RuntimeOwnership.Managed, target, "release", false);
            var state = new RuntimeArchiveState(installation, active, identity, [active], null, []);
            var host = new Host { Observation = new(true, true, false, false, binary, null) };
            var measures = 0;
            Task<string> Measure(string path, RuntimeRelease offer, CancellationToken ct)
            { measures++; Check(path == binary && offer == release, "native observation uses only the declared protected revision"); return Task.FromResult("1.14.3"); }
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) is null && measures == 1,
                "stopped but owned installed program and unchanged configuration match the committed facts");
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, (_, _, _) => Task.FromResult("1.14.4")) == "runtime.native_version_mismatch",
                "a different measured version cannot be certified from saved metadata");
            await File.WriteAllTextAsync(binary, "changed");
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) == "runtime.installed_file_drift" && measures == 1,
                "same-length program corruption is rejected before executing the binary");
            await File.WriteAllBytesAsync(binary, program);
            await File.WriteAllTextAsync(config, "[]");
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) == "runtime.configuration_drift" && measures == 1,
                "changed configuration bytes are not hidden by the saved commit identity");
            await File.WriteAllBytesAsync(config, configuration);
            var original = host.Observation;
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default,
                    (_, _, _) => Task.FromResult("1.14.3"), new(false, false, identity, null, null)) is null,
                "compensation observes the exact restored running and boot baseline");
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure,
                    new(true, false, identity, null, null)) == "runtime.state_unknown" && measures == 1,
                "a stopped service cannot prove restoration of a previously running service");
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure,
                    new(false, true, identity, null, null)) == "runtime.state_unknown" && measures == 1,
                "a changed boot flag cannot prove completed compensation");
            host.Observation = original with { Owned = false };
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) is not null && measures == 1,
                "a foreign registration is not accepted or automatically taken over");
            host.Observation = original with { Running = true, AppliedConfigurationIdentity = new string('e', 64) };
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) == "runtime.configuration_drift",
                "running process must still identify the current configuration");
            host.Observation = original with { Exists = false };
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) is not null,
                "a removed service cannot be certified as the managed commit");
            state = state with { Active = null, Revisions = [], Installation = installation with { Ownership = RuntimeOwnership.Absent, ActiveReleaseId = null } };
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) == "runtime.installed_file_drift",
                "uninstall proof does not hide remaining program revisions");
            Directory.Delete(Path.Combine(root, "versions"), true);
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) is null,
                "retained-data uninstall verifies actual retained configuration bytes and absent registration");
            File.Delete(config);
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) is not null,
                "missing retained configuration cannot satisfy keep-data proof");
            state = state with { ConfigurationIdentity = null };
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) is null,
                "data-deleting uninstall verifies actual configuration absence");
            host.Observation = original with { Owned = false };
            Check(await SingBoxRuntimeCommitObservation.ReadAsync(root, state, host, default, Measure) == "runtime.external_installation",
                "an external replacement service prevents certifying a removed installation");
            Check(host.Mutations == 0 && measures == 1, "fresh commit checks never register, start, stop, remove or measure an unverified program");

            // Exercise the production query path: a conclusive non-success receipt must also read disk.
            var resultRoot = Path.Combine(root, "result");
            void Protect(string path) => Directory.CreateDirectory(path);
            var states = new RuntimeArchiveStateStore(Path.Combine(resultRoot, "state"), Protect);
            var resultHost = new Host { Observation = new(false, false, false, false, null, null) };
            var lifecycle = new SingBoxRuntimeLifecycle(resultRoot, states,
                new RuntimeSourceStore(Path.Combine(root, "sources"), Protect), resultHost, Protect);
            await lifecycle.QueryAsync(default);
            foreach (var outcome in new[] { RuntimeSwitchOutcome.Failed, RuntimeSwitchOutcome.Cancelled, RuntimeSwitchOutcome.Succeeded })
            {
                var operationId = Guid.NewGuid();
                using (var session = states.Acquire())
                {
                    var saved = session.State!;
                    session.Save(saved with { Installation = saved.Installation with { StateRevision = saved.Installation.StateRevision + 1 },
                        Receipts = [new(operationId, new string('a', 64), new string('b', 64), outcome, null, null, DateTimeOffset.UtcNow, null)] });
                }
                var bytes = await File.ReadAllBytesAsync(Path.Combine(resultRoot, "state", "archive-state.json"));
                var snapshot = await lifecycle.QueryAsync(default, operationId);
                Check(snapshot.Receipt?.Outcome == outcome && snapshot.CommitWitness is null && snapshot.ProblemCode is null,
                    "settled receipt is observed without manufacturing success proof: " + outcome);
                await File.WriteAllTextAsync(Path.Combine(resultRoot, "config.json"), "{}");
                snapshot = await lifecycle.QueryAsync(default, operationId);
                Check(snapshot.ProblemCode == "runtime.configuration_drift" && snapshot.Receipt?.OperationId == operationId,
                    "receipt cannot hide unexpected physical configuration: " + outcome);
                File.Delete(Path.Combine(resultRoot, "config.json"));
                resultHost.Observation = resultHost.Observation with { Exists = true };
                snapshot = await lifecycle.QueryAsync(default, operationId);
                Check(snapshot.ProblemCode == "runtime.external_installation",
                    "receipt cannot certify a replacement registration: " + outcome);
                resultHost.Observation = resultHost.Observation with { Exists = false };
                var observedBytes = await File.ReadAllBytesAsync(Path.Combine(resultRoot, "state", "archive-state.json"));
                Check(bytes.SequenceEqual(observedBytes)
                    && resultHost.Mutations == 0 && !Directory.Exists(Path.Combine(resultRoot, "state", "commits")),
                    "result audit preserves the protected ledger and does not create proof: " + outcome);
            }
        }
        finally { Directory.Delete(root, true); }
        return count;
    }

    private sealed class Host : ISingBoxArchiveHost
    {
        public SingBoxServiceObservation Observation = null!;
        public int Mutations;
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(Observation);
        private Task Mutation() { Mutations++; throw new Exception("Commit observation mutated the host"); }
        public Task StopAsync(CancellationToken ct) => Mutation();
        public Task ConfigureAsync(string binary, string configuration, string identity, bool boot, CancellationToken ct) => Mutation();
        public Task SetBootAsync(bool enabled, CancellationToken ct) => Mutation();
        public Task StartAsync(CancellationToken ct) => Mutation();
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => Mutation();
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) => throw new Exception("Commit observation accessed business configuration");
    }
}
