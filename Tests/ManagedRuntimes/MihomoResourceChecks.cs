using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

/// <summary>
/// The GEO resource set is closed and compiled into the Helper. These checks prove that a replaced
/// package asset, an absent installation, or an unfinished transaction can never place a partially
/// trusted database into a managed data directory.
/// </summary>
internal static class MihomoResourceChecks
{
    internal static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        foreach (var file in MihomoGeoDataFiles.Bundled)
        {
            var path = Path.Combine(MihomoRuntimeLifecycle.BundledResourceDirectory, file.Name);
            Check(File.Exists(path) && MihomoGeoDataFiles.DigestMatches(file.Sha256,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))),
                "The published component payload contains the declared resource: " + file.Name);
        }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-resources-" + Guid.NewGuid().ToString("N"));
        void Protect(string path) => Directory.CreateDirectory(path);
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        var geosite = Encoding.UTF8.GetBytes("fixture-geosite-bytes");
        var declared = new[] { new MihomoGeoDataFile("geosite.dat", Convert.ToHexString(SHA256.HashData(geosite)).ToLowerInvariant()) };
        var host = new Host();
        var states = new RuntimeArchiveStateStore(Path.Combine(root, "state"), Protect);
        var lifecycle = new MihomoRuntimeLifecycle(root, states, new(Path.Combine(root, "sources"), Protect), host, Protect,
            resourceDirectory: assets, resourceFiles: declared);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(assets, "geosite.dat"), geosite);

            Check(await FailureAsync(() => lifecycle.PrepareResourcesAsync(default)) == "runtime.installation_absent",
                "Resources are never placed before a managed installation exists");

            // A managed absence is the state the first query records; promote it to a real revision.
            await lifecycle.QueryAsync(default);
            var revisionId = Guid.NewGuid();
            var data = Path.Combine(root, "data", revisionId.ToString("N"));
            Directory.CreateDirectory(data);
            var yaml = "rules:\n  - MATCH,DIRECT\n";
            var binaryBytes = Encoding.UTF8.GetBytes("fixture-binary");
            var binary = Path.Combine(root, "versions", revisionId.ToString("N"), "mihomo");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
            await File.WriteAllBytesAsync(binary, binaryBytes);
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(root, "active.yaml"), yaml);
            var identity = MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(yaml));
            using (var session = states.Acquire())
            {
                var state = session.State!;
                var release = new RuntimeRelease("fixture-release", "mihomo", "v1.19.30", "1.19.30", RuntimeChannel.Stable, "fixture-source",
                    state.Installation.Target, new("fixture", new string('a', 64), 4096, "gz", "mihomo", "signed-manifest:fixture",
                        new(4096, 1, [new("mihomo", binaryBytes.Length, Convert.ToHexString(SHA256.HashData(binaryBytes)), true)])),
                    "mihomo-v1", true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration, DateTimeOffset.UtcNow);
                var revision = new RuntimeStoredRevision(revisionId, release, "1.19.30", identity, 1, 1);
                session.Save(state with { Active = revision, Revisions = [revision], ConfigurationIdentity = identity,
                    Installation = state.Installation with { StateRevision = state.Installation.StateRevision + 1,
                        Ownership = RuntimeOwnership.Managed, ActiveReleaseId = release.ReleaseId } });
            }
            host.Observation = new(true, true, false, false, binary, null, data);

            var missing = await lifecycle.QueryResourcesAsync(default);
            Check(missing is { ProblemCode: null, Files.Count: 1 } && missing.Files[0].State == RuntimeResourceState.Missing
                && !Directory.Exists(Path.Combine(root, "seed-data")), "Resource reads measure missing files without provisioning them");
            var report = await lifecycle.PrepareResourcesAsync(default);
            var seed = Path.Combine(root, "seed-data");
            Check(report.RevisionId == revisionId && report.Files.Count == 1
                && report.Files[0].Name == "geosite.dat" && report.Files[0].Length == geosite.Length
                && report.Files[0].Sha256 == declared[0].Sha256,
                "The Host reports exactly the closed resource it verified");
            Check(File.Exists(Path.Combine(seed, "geosite.dat")) && File.Exists(Path.Combine(data, "geosite.dat"))
                && (await File.ReadAllBytesAsync(Path.Combine(data, "geosite.dat"))).SequenceEqual(geosite),
                "Resources land in both the revision baseline and the active revision");
            Check(File.GetUnixFileMode(Path.Combine(data, "geosite.dat")).HasFlag(UnixFileMode.OtherWrite) == false,
                "Resource files are never group or world writable");

            var observed = await lifecycle.QueryResourcesAsync(default);
            Check(observed.RevisionId == revisionId && observed.Files.Single() is { State: RuntimeResourceState.Verified }
                && observed.Files.Single().Sha256 == declared[0].Sha256, "Resource reads bind verified content to the active revision");
            // A later publication must keep working: an identical file is not rewritten, a foreign one is replaced whole.
            var placedAt = File.GetLastWriteTimeUtc(Path.Combine(data, "geosite.dat"));
            await lifecycle.PrepareResourcesAsync(default);
            Check(File.GetLastWriteTimeUtc(Path.Combine(data, "geosite.dat")) == placedAt,
                "Repeating the operation does not rewrite an already verified resource");
            await File.WriteAllTextAsync(Path.Combine(data, "geosite.dat"), "stale-copy");
            var corrupt = await lifecycle.QueryResourcesAsync(default);
            Check(corrupt.Files.Single() is { State: RuntimeResourceState.Invalid, Sha256: null, Length: null }
                && await File.ReadAllTextAsync(Path.Combine(data, "geosite.dat")) == "stale-copy",
                "Corrupt observations do not assert identity or repair files during a read");
            await lifecycle.PrepareResourcesAsync(default);
            Check((await File.ReadAllBytesAsync(Path.Combine(data, "geosite.dat"))).SequenceEqual(geosite),
                "A stale staged copy is replaced by the verified package asset");

            // A replaced package asset is refused, and nothing is written for that run.
            File.Delete(Path.Combine(seed, "geosite.dat")); File.Delete(Path.Combine(data, "geosite.dat"));
            await File.WriteAllTextAsync(Path.Combine(assets, "geosite.dat"), "tampered");
            Check(await FailureAsync(() => lifecycle.PrepareResourcesAsync(default)) == "runtime.resource_digest_mismatch"
                && !File.Exists(Path.Combine(seed, "geosite.dat")) && !File.Exists(Path.Combine(data, "geosite.dat")),
                "A package asset whose digest does not match the compiled table is refused without a partial write");
            File.Delete(Path.Combine(assets, "geosite.dat"));
            Check(await FailureAsync(() => lifecycle.PrepareResourcesAsync(default)) == "runtime.resource_source_missing",
                "A missing resource source is reported instead of an empty successful placement");
            var link = Path.Combine(root, "linked-geosite.dat");
            File.CreateSymbolicLink(link, "/etc/hostname");
            File.Move(link, Path.Combine(assets, "geosite.dat"), true);
            Check(await FailureAsync(() => lifecycle.PrepareResourcesAsync(default)) == "runtime.linked_path_forbidden",
                "A linked package asset is refused before it is read");
            File.Delete(Path.Combine(assets, "geosite.dat"));
            await File.WriteAllBytesAsync(Path.Combine(assets, "geosite.dat"), geosite);

            File.Delete(Path.Combine(data, "geosite.dat"));
            File.CreateSymbolicLink(Path.Combine(data, "geosite.dat"), "/etc/hostname");
            var linked = await lifecycle.QueryResourcesAsync(default);
            Check(linked.Files.Single() is { State: RuntimeResourceState.Unknown, ProblemCode: "runtime.linked_path_forbidden" },
                "A linked resource is an unknown protected observation, never a missing or verified database");
            File.Delete(Path.Combine(data, "geosite.dat"));
            await lifecycle.PrepareResourcesAsync(default);
            var id = Guid.NewGuid();
            var operation = await MihomoRuntimeOperations.ExecuteAsync(
                new PrivilegedOperationRequest(PrivilegedOperationKind.MihomoRuntimeResources, OperationId: id), () => lifecycle);
            Check(operation.Success && JsonSerializer.Deserialize<RuntimeResourceReport>(
                    Convert.FromBase64String(operation.OutputBase64!)) is { Files.Count: 1 },
                "The privileged entry point reports the same closed set");
            var refused = await MihomoRuntimeOperations.ExecuteAsync(
                new(PrivilegedOperationKind.MihomoRuntimeResources, OperationId: id, RuntimeLifecycle: new()), () => lifecycle);
            Check(refused.ProblemCode == PrivilegedProblemCode.InvalidRequest,
                "The resource operation accepts no plan, path, or selection fields");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return count;
    }

    private static async Task<string?> FailureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (RuntimePolicyException ex) { return ex.ProblemCode; }
    }

    private sealed class Host : IMihomoArchiveHost
    {
        internal MihomoServiceObservation Observation = new(false, true, false, false, null, null, null);
        public Task<MihomoServiceObservation> ObserveAsync(CancellationToken ct) => Task.FromResult(Observation);
        public Task<RuntimeServiceObservation> ObserveRegistrationAsync(CancellationToken ct) => throw new Exception("Unexpected registration observation");
        public Task StopAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task ConfigureAsync(string binary, string data, string configuration, string identity, bool boot, CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task SetBootAsync(bool enabled, CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task StartAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task RemoveOwnedRegistrationAsync(CancellationToken ct) => throw new Exception("Unexpected service mutation");
        public Task<bool> ListenersHealthyAsync(MihomoPublishedRuntime published, CancellationToken ct) => throw new Exception("Unexpected health mutation");
    }
}
