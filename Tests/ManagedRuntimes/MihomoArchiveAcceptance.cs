using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>
/// Real Mihomo evidence: the reviewed upstream gzip archives, the shared signed-catalog and extraction
/// pipeline, native '-v'/'t' reports and archive transactions. The default uses a simulated service;
/// the explicit systemd command additionally registers the fixed unit and forwards real HTTP traffic.
/// </summary>
internal static class MihomoArchiveAcceptance
{
    private const string OlderVersion = "1.19.10";
    private const string OlderSha256 = "a40d28be99cd46649ff0fa9f293a9b593fd98c0507b2623927f0ff2bd587d294";
    private const string NewerVersion = "1.19.30";
    private const string NewerSha256 = "cf06ce2c7d1421bdbda14ee4a5b6046672dc35ebf8eecd8e77504ec3c0ed9a84";
    private const string SourceId = "mihomo-acceptance";
    private const string KeyId = "mihomo-acceptance-key";
    private const string ControllerSecret = "acceptance-controller-secret";

    // A profile as a subscription would deliver it, before the Server takes ownership of its own keys.
    private const string Profile = """
        proxies:
          - name: local-socks
            type: socks5
            server: 127.0.0.1
            port: 1080
            udp: true
        proxy-groups:
          - name: PROXY
            type: select
            proxies:
              - local-socks
              - DIRECT
        rules:
          - MATCH,PROXY
        dns:
          enable: true
          nameserver:
            - 223.5.5.5
        """;

    internal static async Task<int> RunAsync(string olderArchive, string currentArchive, bool systemd = false)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new Exception("Real Mihomo archive acceptance requires Linux x64.");
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        var work = Path.Combine(Path.GetTempPath(), "relaxkonos-mihomo-acceptance-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(work, "staging");
        Directory.CreateDirectory(staging);
        try
        {
            // The fixtures must be the reviewed upstream bytes; a locally rebuilt copy is not evidence.
            Check(await MatchesAsync(olderArchive, OlderSha256), OlderVersion + " fixture matches the reviewed upstream digest");
            Check(await MatchesAsync(currentArchive, NewerSha256), NewerVersion + " fixture matches the reviewed upstream digest");

            var older = await LayoutAsync(olderArchive);
            var current = await LayoutAsync(currentArchive);
            Check(older.Bytes > 0 && current.Bytes > 0 && older.Bytes != current.Bytes,
                "Both reviewed binaries report their own real expanded size");

            var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
            RuntimeRelease Release(string tag, string archiveSha256, long archiveBytes, ArchiveLayout layout) =>
                new("mihomo-" + tag[1..], "mihomo", tag, tag[1..], RuntimeChannel.Stable, SourceId, target,
                    new("mihomo-" + tag[1..] + "-linux-amd64", archiveSha256, archiveBytes, "gz", "mihomo", KeyId,
                        new(layout.Bytes, 4, [new("mihomo", layout.Bytes, layout.Sha256, true)])),
                    MihomoRuntimeSupport.Capability, true, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration,
                    DateTimeOffset.UtcNow);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (sources, catalog) = Accept(key,
            [
                Release("v" + OlderVersion, OlderSha256, new FileInfo(olderArchive).Length, older),
                Release("v" + NewerVersion, NewerSha256, new FileInfo(currentArchive).Length, current),
            ], work);
            Check(catalog.Releases.Count == 2
                && catalog.Releases.All(r => r is { ConfigurationSupported: true, Trust: RuntimeTrust.Confirmed }
                    && r.ConfigurationCapability == MihomoRuntimeSupport.Capability),
                "Both reviewed Mihomo releases are discovered inside the measured native-format range");
            // A plan must name the catalog's own frozen release, never the locally re-built fixture.
            var olderRelease = catalog.Releases.Single(r => r.SortVersion == OlderVersion);
            var newerRelease = catalog.Releases.Single(r => r.SortVersion == NewerVersion);

            var configuration = Path.Combine(work, "active.yaml");
            await File.WriteAllTextAsync(configuration,
                MihomoManagedConfiguration.WithRuntimeSettings(
                    MihomoManagedConfiguration.WithServerControllerSettings(Profile, new MihomoControllerOptions(), ControllerSecret),
                    new ProxySettingsDto(false, false, true, false, false, "info", 17890)));
            Check(File.ReadAllText(configuration).Contains("external-controller: 127.0.0.1:9090", StringComparison.Ordinal)
                && File.ReadAllText(configuration).Contains("mixed-port: 17890", StringComparison.Ordinal),
                "The Server-owned controller and runtime keys are generated once for every version");

            foreach (var (release, archive) in new[] { (olderRelease, olderArchive), (newerRelease, currentArchive) })
            {
                var data = Path.Combine(work, "data-" + release.SortVersion);
                Directory.CreateDirectory(data);
                var plan = new RuntimeChangePlan(new(Guid.NewGuid(), "mihomo-acceptance", RuntimeChange.Install,
                    0, catalog.Revision, catalog.TrustRevision, release.ReleaseId, "prepare-" + release.ReleaseId),
                    "mihomo", release, null);
                var transport = new FixtureTransport(archive);
                var prepared = await new MihomoRuntimePreparation(sources, transport)
                    .PrepareAsync(plan, staging, data, configuration, default);
                var extracted = prepared.Artifact.Archive;
                Check(transport.Calls == 1 && prepared.ConfigurationIdentity ==
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(configuration))),
                    release.SortVersion + " prepares the frozen signed artifact and current configuration together");
                var declared = release.Artifact.Layout.Files.Single();
                Check(new FileInfo(extracted.EntryPoint).Length == declared.Length
                    && await MatchesAsync(extracted.EntryPoint, declared.Sha256),
                    release.SortVersion + " extracts the exact reviewed executable from its single gzip member");
                Check(UnixFileMode.UserExecute == (File.GetUnixFileMode(extracted.EntryPoint) & UnixFileMode.UserExecute),
                    release.SortVersion + " extracted executable keeps its execute bit");
                var build = prepared.Build;
                Check(build is { Os: "linux", Architecture: "amd64" } && build.Version == release.SortVersion,
                    release.SortVersion + " reports exactly the version, platform and architecture it was signed as");
                Check(build.BuildTags.Contains("with_gvisor"),
                    release.SortVersion + " reports the build tags the signed layout depends on");
                Check(true, release.SortVersion + " accepts the generated native configuration");
                RuntimeArchiveExtractor.Discard(extracted, staging);

                // A caller cannot replace even non-executable evidence in the frozen catalog record.
                try
                {
                    await new MihomoRuntimePreparation(sources, transport).PrepareAsync(plan with
                    { Release = release with { Evidence = RuntimeEvidence.Interoperability } }, staging, data, configuration, default);
                    throw new Exception("A substituted frozen record was prepared");
                }
                catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.release_untrusted")
                { Check(transport.Calls == 1 && !Directory.EnumerateFileSystemEntries(staging).Any(),
                    "Forged frozen evidence is refused before download"); }

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await new MihomoRuntimePreparation(sources, transport)
                        .PrepareAsync(plan, staging, data, configuration, cancelled.Token);
                    throw new Exception("Cancelled preparation succeeded");
                }
                catch (OperationCanceledException)
                { Check(!Directory.EnumerateFileSystemEntries(staging).Any(), "Cancelled preparation leaves no staging residue"); }

                var original = await File.ReadAllTextAsync(configuration);
                var drifting = new FixtureTransport(archive)
                { AfterCopy = () => File.AppendAllText(configuration, "\n# concurrent publication\n") };
                try
                {
                    await new MihomoRuntimePreparation(sources, drifting)
                        .PrepareAsync(plan, staging, data, configuration, default);
                    throw new Exception("A changed configuration was accepted");
                }
                catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.configuration_drift")
                { Check(!Directory.EnumerateFileSystemEntries(staging).Any(), "Configuration drift discards the prepared executable"); }
                finally { await File.WriteAllTextAsync(configuration, original); }
            }

            var tampered = Path.Combine(work, "tampered.gz");
            var bytes = await File.ReadAllBytesAsync(currentArchive);
            bytes[bytes.Length / 2] ^= 0xff;
            await File.WriteAllBytesAsync(tampered, bytes);
            try
            {
                await using var stream = File.OpenRead(tampered);
                await RuntimeArchiveExtractor.ExtractAsync(stream, newerRelease.Artifact, staging, default);
                throw new Exception("A tampered Mihomo archive was extracted");
            }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime.archive_digest_mismatch")
            { Check(!Directory.EnumerateFileSystemEntries(staging).Any(), "A tampered archive leaves no extracted files"); }

            Check(!RuntimeArchiveRules.Valid(newerRelease.Artifact with
            {
                Layout = newerRelease.Artifact.Layout with
                { Files = [newerRelease.Artifact.Layout.Files[0], new("geoip.metadb", 8, new string('a', 64), false)] },
            }), "A gzip release cannot claim a second file the archive never names");

            ManifestRelease Signed(string tag) => new("mihomo-" + tag[1..], "mihomo", tag, RuntimeChannel.Stable, target,
                newerRelease.Artifact, "https://example.com/artifacts/" + tag, null);
            Check(RuntimeFormatSupport.Configuration(Signed("v" + NewerVersion)) is { Supported: true, SortVersion: NewerVersion }
                && RuntimeFormatSupport.Configuration(Signed("v1.18.10")) is { Supported: false, SortVersion: "1.18.10" }
                && RuntimeFormatSupport.Configuration(Signed("v1.20.0")) is { Supported: false, SortVersion: "1.20.0" },
                "Discovery honours the measured range around the versions proven here");
            count += await MihomoArchiveTransactionChecks.RunAsync(Path.Combine(work, "transaction"), sources,
                catalog, olderArchive, currentArchive, await File.ReadAllTextAsync(configuration));
            if (systemd) count += await MihomoSystemdAcceptance.RunAsync(sources, catalog, olderArchive, currentArchive);
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(work)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Acceptance cleanup escaped the temporary directory");
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        return count;
    }

    private sealed class FixtureTransport(string archive) : IRuntimeArchiveTransport
    {
        internal int Calls;
        internal Action? AfterCopy;
        public async Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct)
        {
            Calls++;
            await using var input = File.OpenRead(archive);
            await input.CopyToAsync(destination, ct);
            AfterCopy?.Invoke();
        }
    }

    private readonly record struct ArchiveLayout(long Bytes, string Sha256);

    /// <summary>Reads what the real archive inflates to, without keeping a second copy of the executable.</summary>
    private static async Task<ArchiveLayout> LayoutAsync(string archive)
    {
        await using var input = File.OpenRead(archive);
        // A single gzip member is the whole archive: gzip itself must not decode into something else.
        var header = new byte[2];
        input.ReadExactly(header);
        if (header[0] != 0x1f || header[1] != 0x8b) throw new Exception("Fixture is not a gzip archive: " + archive);
        input.Position = 0;
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await gzip.ReadAsync(buffer)) > 0)
        {
            digest.AppendData(buffer, 0, read);
            total += read;
        }
        return new(total, Convert.ToHexString(digest.GetHashAndReset()));
    }

    private static async Task<bool> MatchesAsync(string path, string expected)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (RuntimeSourceStore Sources, RuntimeCatalog Catalog) Accept(ECDsa key, IReadOnlyList<RuntimeRelease> releases, string work)
    {
        var source = new RuntimeSource(SourceId, "mihomo", "MetaCubeX/mihomo", RuntimeSourceKind.HttpsManifest,
            "https://example.com/catalog/", "manifest.json", KeyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            true, 0, [RuntimeChannel.Stable], 1 << 20, 86400, ["https://example.com/artifacts/"], [], []);
        var manifestReleases = releases.Select(release => new ManifestRelease(release.ReleaseId, release.RuntimeId,
            release.OriginalVersion, release.Channel, release.Target, release.Artifact,
            "https://example.com/artifacts/" + release.Artifact.ArtifactId, null)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var manifest = new RuntimeManifest(1, source.SourceId, 1, now, now.AddHours(1), manifestReleases, []);
        var payload = RuntimeJson.Serialize(manifest);
        var envelope = RuntimeJson.Serialize(new SignedRuntimeCatalog(1, source.KeyId, Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(payload).ToArray(),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        var sources = new RuntimeSourceStore(Path.Combine(work, "trust"), path => Directory.CreateDirectory(path));
        sources.Confirm(source, 0, source.ConfirmationFingerprint, "mihomo-acceptance", now);
        // Production decides support from this build's measured range; the fixture only supplies signed bytes.
        var accepted = sources.Accept(source.SourceId, 1, envelope, now, RuntimeFormatSupport.Configuration);
        return (sources, accepted.Catalog);
    }
}
