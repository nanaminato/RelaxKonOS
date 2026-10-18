using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

internal static class SingBoxArchiveChecks
{
    internal static async Task<int> RunAsync(string trustedArchive)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new Exception("This native fixture declares only Windows amd64 support");
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var archivePath = Path.GetFullPath(trustedArchive);
        await using (var stream = File.OpenRead(archivePath))
            Check(Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(SingBoxManifest.Current!.Sha256, StringComparison.OrdinalIgnoreCase),
                "Native archive must match the source-controlled upstream pin before any binary is executed");
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-sing-box-native-" + Guid.NewGuid().ToString("N"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var files = new List<RuntimeArchiveFile>();
            using (var zip = ZipFile.OpenRead(archivePath))
                foreach (var item in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
                {
                    await using var input = item.Open();
                    files.Add(new(item.FullName, item.Length, Convert.ToHexString(await SHA256.HashDataAsync(input)), item.FullName.EndsWith("sing-box.exe", StringComparison.Ordinal)));
                }
            var source = new RuntimeSource("native-fixture", "sing-box", "SagerNet/sing-box", RuntimeSourceKind.HttpsManifest,
                "https://example.com/catalog/", "manifest.json", "test-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                true, 0, [RuntimeChannel.Stable], 65536, 86400, ["https://example.com/artifacts/"], [], []);
            var artifact = new RuntimeArtifact("sing-box-1.14.3-windows-amd64", SingBoxManifest.Current!.Sha256,
                new FileInfo(archivePath).Length, "zip", files.Single(f => f.Executable).Path, source.KeyId, new(128 * 1024 * 1024, 32, files));
            var target = new RuntimeTarget("windows", "amd64", null, null, RuntimeInstallMode.Archive, "scm");
            var manifestRelease = new ManifestRelease("release-1.14.3", "sing-box", "v1.14.3", RuntimeChannel.Stable,
                target, artifact, "https://example.com/artifacts/sing-box.zip", null);
            var now = DateTimeOffset.UtcNow;
            var manifest = new RuntimeManifest(1, source.SourceId, 1, now, now.AddHours(1), [manifestRelease], []);
            var payload = RuntimeJson.Serialize(manifest);
            var envelope = RuntimeJson.Serialize(new SignedRuntimeCatalog(1, source.KeyId, Convert.ToBase64String(payload),
                Convert.ToBase64String(key.SignData(RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(payload).ToArray(),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
            var sources = new RuntimeSourceStore(Path.Combine(root, "trust"), path => Directory.CreateDirectory(path));
            sources.Confirm(source, 0, source.ConfirmationFingerprint, "native-test", now);
            var catalog = sources.Accept(source.SourceId, 1, envelope, now, _ => new("1.14.3", "sing-box-1.14.3", true));
            var runtimeRoot = Path.Combine(root, "runtime");
            var states = new RuntimeArchiveStateStore(Path.Combine(runtimeRoot, "state"), path => Directory.CreateDirectory(path));
            states.Initialize(new(Guid.NewGuid(), "native-fixture-host", "sing-box", 0, RuntimeOwnership.Absent, target, null, false), null);
            var host = new TestHost(); var authorization = new Authorization();
            var adapter = new SingBoxArchiveAdapter(runtimeRoot, states, sources, new ArchiveCopy(archivePath), host, authorization,
                path => Directory.CreateDirectory(path));
            var lifecycle = new RuntimeArchiveSwitch(states, adapter);
            RuntimeChangePlan Plan(RuntimeChange change, string name)
            {
                var state = states.Read()!;
                return new(new(state.Installation.InstallationId, state.Installation.HostId, change, state.Installation.StateRevision,
                    1, 1, manifestRelease.ReleaseId, name), "sing-box", catalog.Catalog.Releases.Single(), state.Active?.Release.ReleaseId);
            }
            var install = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Install, "native-install"), default);
            Check(install.Outcome == RuntimeSwitchOutcome.Succeeded, "Real sing-box version/check accepted through archive transaction: " + install.ProblemCode);
            var original = states.Read()!.Active!;
            Check(original.MeasuredVersion == "1.14.3" && host.Observation.Exists && !host.Observation.Running,
                "Measured native version committed without starting any host service");
            Check(File.Exists(Path.Combine(runtimeRoot, "versions", original.RevisionId.ToString("N"), files.Single(f => f.Path.EndsWith("libcronet.dll", StringComparison.Ordinal)).Path.Replace('/', Path.DirectorySeparatorChar))),
                "All declared native dependencies retained and verified");
            var repaired = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Repair, "native-repair"), default);
            Check(repaired.Outcome == RuntimeSwitchOutcome.Succeeded && states.Read()!.Active!.RevisionId != original.RevisionId,
                "Real same-version repair creates a distinct immutable directory");
            var generated = await SingBoxOperations.RunAsync(host.Observation.Executable!, ["generate", "reality-keypair"]);
            var keyLines = generated.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var inboundId = Guid.NewGuid();
            var inbound = new ProxyServerInbound(inboundId, "Native REALITY fixture", ProxyServerProtocol.VlessReality,
                "127.0.0.1", 24443, "proxy.example.com", 24443, "proxy.example.com", HandshakeHost: "www.example.com", HandshakePort: 443);
            authorization.Published = new(new(1, [inbound]), [new(Guid.NewGuid(), [inboundId], Guid.NewGuid().ToString("D"))],
                [new(inboundId, keyLines.Single(l => l.StartsWith("PrivateKey: ", StringComparison.Ordinal))[12..],
                    keyLines.Single(l => l.StartsWith("PublicKey: ", StringComparison.Ordinal))[11..],
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant())]);
            using (var publication = states.Acquire())
            {
                var rendered = Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(authorization.Published));
                await File.WriteAllBytesAsync(Path.Combine(runtimeRoot, "config.json"), rendered);
                var identity = Convert.ToHexString(SHA256.HashData(rendered));
                publication.Save(publication.State! with { ConfigurationIdentity = identity,
                    Installation = publication.State!.Installation with { StateRevision = publication.State.Installation.StateRevision + 1 } });
                host.Observation = host.Observation with { AppliedConfigurationIdentity = identity };
            }
            var realityRepair = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Repair, "native-reality"), default);
            Check(realityRepair.Outcome == RuntimeSwitchOutcome.Succeeded,
                "Real target binary checks the current published REALITY configuration and its build feature");
            var beforeFailure = states.Read()!.Active!;
            host.FailNextConfigure = true;
            var failed = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Repair, "native-compensate"), default);
            Check(failed.Outcome == RuntimeSwitchOutcome.Failed && states.Read()!.Active!.RevisionId == beforeFailure.RevisionId
                && host.Observation.Executable!.Contains(beforeFailure.RevisionId.ToString("N"), StringComparison.Ordinal),
                "Service-adapter failure restores native-checked previous program/configuration");
            Check(!states.Read()!.Installation.RecoveryRequired && states.Read()!.Pending is null,
                "Verified native compensation clears pending transaction");
            authorization.Unavailable = true;
            var beforeCalls = host.ConfigureCalls;
            var denied = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Repair, "no-authorization"), default);
            Check(denied.Outcome == RuntimeSwitchOutcome.Failed && denied.ProblemCode == "runtime.authorization_unavailable"
                && host.ConfigureCalls == beforeCalls, "Missing current authorization does not switch an existing native runtime");
            authorization.Unavailable = false;
            var beforeRevocation = states.Read()!.Active!;
            host.Observation = host.Observation with { Running = true };
            var stopsBeforeRevocation = host.StopCalls;
            host.AfterConfigure = () => authorization.Unavailable = true;
            var revoke = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(RuntimeChange.Repair, "revoke-during-switch"), default);
            Check(revoke.Outcome == RuntimeSwitchOutcome.RecoveryRequired && states.Read()!.Pending!.RecoveryProblemCode == "runtime.authorization_unavailable"
                && !host.Observation.Running && host.StopCalls >= stopsBeforeRevocation + 2,
                "Unavailable fresh authority stops the simulated running candidate and prevents restoring credentials after the switch");
            host.AfterConfigure = null; authorization.Unavailable = false;
            var recovered = await lifecycle.RecoverAsync(states.Read()!.Pending!.OperationId, default);
            Check(recovered!.Outcome == RuntimeSwitchOutcome.Interrupted && states.Read()!.Active!.RevisionId == beforeRevocation.RevisionId,
                "Recovery rechecks the actual old binary/config only after current authority becomes available");
            Check(!Directory.EnumerateFileSystemEntries(Path.Combine(runtimeRoot, "staging")).Any(), "Operation-addressable staging leaves no successful preparation residue");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Native fixture cleanup escaped temp root");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return count;
    }

    private sealed class ArchiveCopy(string path) : IRuntimeArchiveTransport
    {
        public async Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct)
        { await using var input = File.OpenRead(path); await input.CopyToAsync(destination, ct); }
    }
    private sealed class Authorization : ISingBoxArchiveAuthorization
    {
        internal bool Unavailable;
        internal ProxyServerMaterial? Published;
        public Task<SingBoxCurrentAuthorization> ReadAsync(CancellationToken ct) => Unavailable
            ? throw new RuntimePolicyException("runtime.authorization_unavailable")
            : Task.FromResult(new SingBoxCurrentAuthorization(Published, DateTimeOffset.UtcNow.AddMinutes(1)));
    }
    private sealed class TestHost : ISingBoxArchiveHost
    {
        internal SingBoxServiceObservation Observation = new(false, true, false, false, null, null);
        internal bool FailNextConfigure;
        internal int ConfigureCalls;
        internal int StopCalls;
        internal Action? AfterConfigure;
        public Task<SingBoxServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(Observation);
        public Task StopAsync(CancellationToken ct) { StopCalls++; Observation = Observation with { Running = false }; return Task.CompletedTask; }
        public Task ConfigureAsync(string verifiedBinary, string protectedConfiguration, string configurationIdentity, bool bootEnabled, CancellationToken ct)
        {
            ConfigureCalls++;
            Observation = new(true, true, false, bootEnabled, verifiedBinary, configurationIdentity);
            AfterConfigure?.Invoke();
            if (FailNextConfigure) { FailNextConfigure = false; throw new IOException("Fixture configuration failure"); }
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken ct) { Observation = Observation with { Running = true }; return Task.CompletedTask; }
        public Task SetBootAsync(bool enabled, CancellationToken ct) { Observation = Observation with { BootEnabled = enabled }; return Task.CompletedTask; }
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) { Observation = new(false, true, false, false, null, null); return Task.CompletedTask; }
        // Deliberately a service-boundary mock; no inbound service or network listener is started.
        public Task<bool> ListenersHealthyAsync(ProxyServerConfiguration published, CancellationToken ct) => Task.FromResult(true);
    }
}
