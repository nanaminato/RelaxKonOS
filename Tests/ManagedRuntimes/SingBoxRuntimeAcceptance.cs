using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.ProxyServer;

/// <summary>
/// Real acceptance for an archive-managed sing-box on a Linux systemd host: two upstream releases, the real
/// archive/switch transaction, the real fixed unit, and a real VLESS/REALITY client through the tunnel.
/// The operator supplies both upstream archives; this check never downloads and never widens the declared
/// support range. Everything it proves is process-, unit- and socket-level evidence, not a fixture.
/// </summary>
internal static class SingBoxRuntimeAcceptance
{
    // Reviewed upstream identities. The digest is what the release page published for that asset.
    private const string OlderVersion = "1.13.21";
    private const string OlderSha256 = "24f9ef8e7234e13e71e74c3598a4164c5fe07b7b67ccc6e96cf68b54789f72cd";
    private const string NewerVersion = "1.14.3";
    private const string NewerSha256 = "e2bdf179c15a3652955dc44867e33ca0965c9c9b91b34267dcc5a5d639a5feee";
    private const string ProbeUrl = "http://example.com/";
    private const string ProbeMarker = "Example Domain";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);

    [SupportedOSPlatform("linux")]
    internal static async Task<int> RunAsync(string olderArchive, string currentArchive)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new Exception("Real sing-box runtime acceptance requires a Linux x64 host");
        if (!Environment.IsPrivilegedProcess)
            throw new Exception("Real sing-box runtime acceptance registers a fixed systemd unit and must run as root");
        if (!Directory.Exists("/run/systemd/system"))
            throw new Exception("Real sing-box runtime acceptance requires a running systemd host");
        var host = new LinuxSingBoxArchiveHost();
        if ((await host.ObserveAsync(default)).Exists || Directory.Exists(LinuxSingBoxArchiveHost.Root))
            throw new Exception("Acceptance requires an absent sing-box service and an absent runtime root");
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }

        var older = Path.GetFullPath(olderArchive);
        var current = Path.GetFullPath(currentArchive);
        Check((await DigestAsync(older)).Equals(OlderSha256, StringComparison.OrdinalIgnoreCase)
            && (await DigestAsync(current)).Equals(NewerSha256, StringComparison.OrdinalIgnoreCase),
            "Both fixtures match the reviewed upstream digests for their tags");
        Check(string.Equals(SingBoxManifest.Current?.Sha256, NewerSha256, StringComparison.OrdinalIgnoreCase),
            "The newer fixture is byte-identical to the source-controlled pin this build ships");

        // The declared range is the only install permission, and it is computed by this build, not the catalog.
        Check(SingBoxRuntimeSupport.CapabilityFor(OlderVersion) == SingBoxRuntimeSupport.Capability
            && SingBoxRuntimeSupport.CapabilityFor(NewerVersion) == SingBoxRuntimeSupport.Capability,
            "Both tested releases fall inside the declared native-format range");
        Check(SingBoxRuntimeSupport.CapabilityFor("1.12.9") is null
            && SingBoxRuntimeSupport.CapabilityFor("1.15.0") is null
            && SingBoxRuntimeSupport.CapabilityFor("1.15.0-alpha.12") is null
            && SingBoxRuntimeSupport.CapabilityFor("1.14.3") == SingBoxRuntimeSupport.Capability,
            "Releases below, above, or ahead of the range stay unsupported instead of being guessed");

        var root = LinuxSingBoxArchiveHost.Root;
        var work = Path.Combine(Path.GetTempPath(), "relaxkonos-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var clients = new List<Process>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var target = new RuntimeTarget("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd");
        try
        {
            var olderLayout = await LayoutAsync(older, work, OlderVersion);
            var newerLayout = await LayoutAsync(current, work, NewerVersion);
            var olderRelease = Release("release-" + OlderVersion, "v" + OlderVersion, OlderSha256, olderLayout);
            var newerRelease = Release("release-" + NewerVersion, "v" + NewerVersion, NewerSha256, newerLayout);
            var (sources, catalog) = Accept(key, [olderRelease, newerRelease], work);
            // Plans must name the catalog's own frozen release; a locally re-built copy is not what was signed.
            olderRelease = catalog.Releases.Single(r => r.SortVersion == OlderVersion);
            newerRelease = catalog.Releases.Single(r => r.SortVersion == NewerVersion);
            Check(catalog.Releases.Count == 2 && catalog.Releases.All(r =>
                    r.ConfigurationSupported && r.ConfigurationCapability == SingBoxRuntimeSupport.Capability
                    && r.SortVersion is OlderVersion or NewerVersion),
                "The Helper's own capability computation marks in-range signed releases supported");

            var states = new RuntimeArchiveStateStore(Path.Combine(root, "state"), Protect);
            states.Initialize(new(Guid.NewGuid(), "acceptance-host", "sing-box", 0, RuntimeOwnership.Absent, target, null, false), null);
            var authorization = new Authorization();
            var adapter = new SingBoxArchiveAdapter(root, states, sources, new ArchiveCopy(older, current), host, authorization, Protect);
            var lifecycle = new RuntimeArchiveSwitch(states, adapter);

            // A catalog entry cannot buy support by claiming a capability or a version the build has not measured.
            var forged = olderRelease with { ReleaseId = "release-forged", OriginalVersion = "v1.12.0" };
            var forgedPlan = Plan(states, forged, RuntimeChange.Install, "forged-support");
            var refused = await lifecycle.ExecuteAsync(Guid.NewGuid(), forgedPlan, default);
            Check(refused.Outcome == RuntimeSwitchOutcome.Failed && refused.ProblemCode == "runtime.configuration_unsupported"
                && !Directory.Exists(Path.Combine(root, "versions")),
                "A signed catalog cannot widen the native-format range to an unmeasured release");

            var installed = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(states, olderRelease, RuntimeChange.Install, "install-older"), default);
            Check(installed.Outcome == RuntimeSwitchOutcome.Succeeded, "Real " + OlderVersion + " installs through the archive transaction: " + installed.ProblemCode);
            var active = states.Read()!.Active!;
            await using (var stream = File.OpenRead(Path.Combine(root, "versions", active.RevisionId.ToString("N"), olderLayout.EntryPoint)))
                Check(Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(
                        olderLayout.Files.Single(f => f.Executable).Sha256, StringComparison.OrdinalIgnoreCase),
                    "The committed revision is the exact upstream binary, not a re-packed copy");
            Check((await host.ObserveAsync(default)) is { Exists: true, Owned: true, Running: false },
                "Registration is created without starting the service");

            // Publish a real REALITY inbound and let the transaction activate it against the real unit.
            var serverPort = FreePort();
            var shortId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            await RealityKeysAsync(Path.Combine(root, "versions", active.RevisionId.ToString("N"), olderLayout.EntryPoint), authorization, serverPort, shortId);
            using (var publication = states.Acquire())
            {
                var rendered = Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(authorization.Published!));
                await File.WriteAllBytesAsync(Path.Combine(root, "config.json"), rendered);
                publication.Save(publication.State! with
                {
                    ConfigurationIdentity = Convert.ToHexString(SHA256.HashData(rendered)),
                    Installation = publication.State!.Installation with { StateRevision = publication.State.Installation.StateRevision + 1 }
                });
            }
            var publishedIdentity = states.Read()!.ConfigurationIdentity;
            var repaired = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(states, olderRelease, RuntimeChange.Repair, "publish-reality"), default);
            Check(repaired.Outcome == RuntimeSwitchOutcome.Succeeded
                && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "config.json")))) == publishedIdentity,
                "The published REALITY configuration is the one the unit was registered with: " + repaired.ProblemCode);
            await host.StartAsync(default);
            var running = await host.ObserveAsync(default);
            Check(running is { Running: true } && running.AppliedConfigurationIdentity == publishedIdentity,
                "The running unit proves which configuration it actually loaded");
            Check(await HealthyAsync(host, authorization.Published!.Configuration),
                "The real " + OlderVersion + " process listens on the published inbound");
            Check(await ProbeAsync(clients, InstalledBinary(states, OlderVersion, olderLayout), authorization),
                "VLESS/REALITY from a " + OlderVersion + " client reaches a public target through the managed service");

            var updated = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(states, newerRelease, RuntimeChange.Update, "update-newer", previous: olderRelease), default);
            var afterUpdate = states.Read()!;
            Check(updated.Outcome == RuntimeSwitchOutcome.Succeeded && afterUpdate.Active!.MeasuredVersion == NewerVersion
                && afterUpdate.ConfigurationIdentity == publishedIdentity,
                "Cross-version update commits the measured " + NewerVersion + " with the same published configuration: " + updated.ProblemCode);
            Check(afterUpdate.Revisions.Any(r => r.MeasuredVersion == OlderVersion)
                && afterUpdate.Revisions.Count(r => r.MeasuredVersion == NewerVersion) == 1,
                "The previous version stays available as a rollback revision");
            Check(await HealthyAsync(host, authorization.Published!.Configuration),
                "The " + NewerVersion + " service actually listens on the inherited configuration");
            Check(await ProbeAsync(clients, InstalledBinary(states, NewerVersion, newerLayout), authorization),
                "VLESS/REALITY from a " + NewerVersion + " client reaches a public target through the " + NewerVersion + " service");

            // Interoperability is not "same build on both sides": the older client must still complete a
            // REALITY handshake with the newer service, and the tunnel must be the only path to the target.
            Check(await ProbeAsync(clients, InstalledBinary(states, OlderVersion, olderLayout), authorization),
                "A " + OlderVersion + " client interoperates with the " + NewerVersion + " service");
            await host.StopAsync(default);
            Check(!await ProbeAsync(clients, InstalledBinary(states, NewerVersion, newerLayout), authorization),
                "With the managed service stopped the same client has no other route to the target");
            await host.StartAsync(default);
            Check(await HealthyAsync(host, authorization.Published!.Configuration),
                "The restarted service is listening again before the retained revision is exercised");

            var rolledBack = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(states, olderRelease, RuntimeChange.Rollback, "rollback-older", previous: newerRelease), default);
            Check(rolledBack.Outcome == RuntimeSwitchOutcome.Succeeded && states.Read()!.Active!.MeasuredVersion == OlderVersion,
                "Rollback restores the retained " + OlderVersion + " revision: " + rolledBack.ProblemCode);
            Check(await HealthyAsync(host, authorization.Published!.Configuration)
                && await ProbeAsync(clients, InstalledBinary(states, OlderVersion, olderLayout), authorization),
                "The rolled-back service serves the same configuration through a real tunnel");
            var restored = await lifecycle.ExecuteAsync(Guid.NewGuid(), Plan(states, newerRelease, RuntimeChange.Update, "update-again", previous: olderRelease), default);
            Check(restored.Outcome == RuntimeSwitchOutcome.Succeeded && states.Read()!.Active!.MeasuredVersion == NewerVersion,
                "Updating again returns the instance to the pinned " + NewerVersion);

            var removal = new RuntimeArchiveRemoval(states, new SingBoxRemovalAdapter(root, host, Protect));
            var removed = await removal.ExecuteAsync(Guid.NewGuid(), RemovalPlan(states), default);
            var afterRemoval = states.Read()!;
            Check(removed.Outcome == RuntimeSwitchOutcome.Succeeded && afterRemoval.Installation.Ownership == RuntimeOwnership.Absent
                && afterRemoval.Active is null && afterRemoval.Revisions.Length == 0
                && !(await host.ObserveAsync(default)).Exists && !Directory.Exists(Path.Combine(root, "versions"))
                && File.Exists(Path.Combine(root, "config.json")),
                "Uninstalling a real managed instance keeps the published configuration and removes program and unit: " + removed.ProblemCode);
        }
        finally
        {
            foreach (var client in clients)
                try { if (!client.HasExited) client.Kill(true); } catch (InvalidOperationException) { }
            foreach (var client in clients) client.Dispose();
            try { await host.StopAsync(CancellationToken.None); } catch (Exception ex) when (ex is IOException or RuntimePolicyException or InvalidOperationException) { }
            try { await host.RemoveOwnedRegistrationAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or RuntimePolicyException or InvalidOperationException) { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        return count;
    }

    private static RuntimeRelease Release(string releaseId, string tag, string sha256, ArchiveLayout layout) =>
        new(releaseId, "sing-box", tag, tag[1..], RuntimeChannel.Stable, "native-acceptance",
            new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"),
            new("sing-box-" + tag[1..] + "-linux-amd64", sha256, layout.Bytes, "tar.gz", layout.EntryPoint,
                "native-acceptance-key", new(layout.ExpandedBytes, layout.MaximumEntries, layout.Files)),
            SingBoxRuntimeSupport.Capability, true, RuntimeTrust.Confirmed, RuntimeEvidence.Interoperability, DateTimeOffset.UtcNow);

    private static (RuntimeSourceStore Sources, RuntimeCatalog Catalog) Accept(ECDsa key, IReadOnlyList<RuntimeRelease> releases, string work)
    {
        var source = new RuntimeSource("native-acceptance", "sing-box", "SagerNet/sing-box", RuntimeSourceKind.HttpsManifest,
            "https://example.com/catalog/", "manifest.json", "native-acceptance-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            true, 0, [RuntimeChannel.Stable], 1 << 20, 86400, ["https://example.com/artifacts/"], [], []);
        var manifestReleases = releases.Select(release => new ManifestRelease(release.ReleaseId, release.RuntimeId, release.OriginalVersion,
            release.Channel, release.Target, release.Artifact, "https://example.com/artifacts/" + release.Artifact.ArtifactId, null)).ToArray();
        var now = DateTimeOffset.UtcNow;
        var manifest = new RuntimeManifest(1, source.SourceId, 1, now, now.AddHours(1), manifestReleases, []);
        var payload = RuntimeJson.Serialize(manifest);
        var envelope = RuntimeJson.Serialize(new SignedRuntimeCatalog(1, source.KeyId, Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(RuntimeCatalogVerifier.SignatureContext.ToArray().Concat(payload).ToArray(),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        var sources = new RuntimeSourceStore(Path.Combine(work, "trust"), path => Directory.CreateDirectory(path));
        sources.Confirm(source, 0, source.ConfirmationFingerprint, "native-acceptance", now);
        // The production capability computation decides support; the fixture only supplies the signed bytes.
        var accepted = sources.Accept(source.SourceId, 1, envelope, now, RuntimeSourceOperations.ConfigurationSupport);
        return (sources, accepted.Catalog);
    }

    private static RuntimeChangePlan Plan(RuntimeArchiveStateStore states, RuntimeRelease release, RuntimeChange change,
        string key, RuntimeRelease? previous = null)
    {
        var state = states.Read()!;
        return new(new(state.Installation.InstallationId, state.Installation.HostId, change, state.Installation.StateRevision,
            1, 1, release.ReleaseId, key), "sing-box", release, (previous?.ReleaseId) ?? state.Active?.Release.ReleaseId);
    }

    private static RuntimeChangePlan RemovalPlan(RuntimeArchiveStateStore states)
    {
        var state = states.Read()!;
        return new(new(state.Installation.InstallationId, state.Installation.HostId, RuntimeChange.Uninstall,
            state.Installation.StateRevision, 1, 1, null, "uninstall-accepted", false, true), "sing-box", null,
            state.Active?.Release.ReleaseId);
    }

    private static async Task RealityKeysAsync(string binary, Authorization authorization, int port, string shortId)
    {
        var output = await SingBoxOperations.RunAsync(binary, ["generate", "reality-keypair"]);
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var privateKey = lines.Single(l => l.StartsWith("PrivateKey: ", StringComparison.Ordinal))[12..];
        var publicKey = lines.Single(l => l.StartsWith("PublicKey: ", StringComparison.Ordinal))[11..];
        var inboundId = Guid.NewGuid();
        var inbound = new ProxyServerInbound(inboundId, "Native REALITY acceptance", ProxyServerProtocol.VlessReality,
            "127.0.0.1", port, "www.example.com", 443, "www.example.com", HandshakeHost: "www.example.com", HandshakePort: 443);
        authorization.Published = new(new(1, [inbound]),
            [new(Guid.NewGuid(), [inboundId], Guid.NewGuid().ToString("D"))],
            [new(inboundId, privateKey, publicKey, shortId)]);
    }

    /// <summary>The retained revision of one measured version; the active one can be a different version.</summary>
    [SupportedOSPlatform("linux")]
    private static string InstalledBinary(RuntimeArchiveStateStore states, string version, ArchiveLayout layout)
    {
        var revision = states.Read()!.Revisions.FirstOrDefault(r => r.MeasuredVersion == version)
            ?? throw new Exception("No retained revision for " + version);
        var binary = Path.Combine(LinuxSingBoxArchiveHost.Root, "versions", revision.RevisionId.ToString("N"),
            layout.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(binary) ? binary : throw new Exception("Retained revision binary is missing for " + version);
    }

    [SupportedOSPlatform("linux")]
    private static async Task<bool> HealthyAsync(LinuxSingBoxArchiveHost host, ProxyServerConfiguration published)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await host.ListenersHealthyAsync(published, default)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>
    /// One real client process with only a VLESS outbound. A success therefore proves the bytes crossed the
    /// managed service; there is no direct outbound to fall back on.
    /// </summary>
    private static async Task<bool> ProbeAsync(List<Process> clients, string clientBinary, Authorization authorization)
    {
        var draft = authorization.Published ?? throw new Exception("No published configuration");
        var inbound = draft.Configuration.Inbounds.Single();
        var key = draft.Keys.Single();
        var secret = draft.Credentials.Single();
        var socks = FreePort();
        var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-acceptance-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configuration = Path.Combine(directory, "client.json");
        await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
        {
            log = new { level = "error" },
            inbounds = new[] { new { type = "socks", tag = "socks", listen = "127.0.0.1", listen_port = socks } },
            outbounds = new[]
            {
                new
                {
                    type = "vless", tag = "proxy", server = "127.0.0.1", server_port = inbound.ListenPort,
                    uuid = secret.Value, flow = "xtls-rprx-vision",
                    tls = new
                    {
                        enabled = true, server_name = inbound.ServerName,
                        utls = new { enabled = true, fingerprint = "chrome" },
                        reality = new { enabled = true, public_key = key.PublicKey, short_id = key.ShortId }
                    }
                }
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        var client = Process.Start(new ProcessStartInfo(clientBinary, ["run", "-c", configuration])
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        }) ?? throw new Exception("Client process did not start");
        clients.Add(client);
        try
        {
            // Wait for the client's SOCKS inbound; a client that exited early is a fixture failure, not a verdict.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var listening = false;
            while (!listening && DateTime.UtcNow < deadline)
            {
                if (client.HasExited) throw new Exception("Client exited before listening: " + (await client.StandardError.ReadToEndAsync()).Trim());
                listening = await ListeningAsync(socks, default);
                if (!listening) await Task.Delay(100);
            }
            if (!listening) throw new Exception("Client never opened its SOCKS inbound");
            using var timeout = new CancellationTokenSource(ProbeTimeout);
            using var http = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy("socks5://127.0.0.1:" + socks), UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(8)
            }) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                using var response = await http.GetAsync(ProbeUrl, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                return response.StatusCode == HttpStatusCode.OK && body.Contains(ProbeMarker, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return false;
            }
        }
        finally
        {
            try { if (!client.HasExited) client.Kill(true); } catch (InvalidOperationException) { }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task<bool> ListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port, ct);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException) { return false; }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record ArchiveLayout(string EntryPoint, long Bytes, long ExpandedBytes, int MaximumEntries,
        IReadOnlyList<RuntimeArchiveFile> Files);

    /// <summary>Reads the real archive once to declare its exact file list; the extractor re-verifies every byte.</summary>
    private static async Task<ArchiveLayout> LayoutAsync(string archive, string work, string version)
    {
        var directory = Path.Combine(work, "layout-" + version);
        Directory.CreateDirectory(directory);
        await Task.Run(async () =>
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, directory, overwriteFiles: true);
        });
        var files = new List<RuntimeArchiveFile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) throw new Exception("Upstream archive contains a link");
            var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            await using var stream = File.OpenRead(path);
            files.Add(new(relative, info.Length, Convert.ToHexString(await SHA256.HashDataAsync(stream)),
                Path.GetFileName(relative) == "sing-box"));
        }
        var entry = files.Single(file => file.Executable).Path;
        return new(entry, new FileInfo(archive).Length, files.Sum(file => file.Length), files.Count + 16, files);
    }

    private static async Task<string> DigestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    [SupportedOSPlatform("linux")]
    private static void Protect(string path)
    {
        RuntimeSourceStore.RequireNoLinks(path);
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Serves each fixture as its own published asset, so a wrong-asset download is a digest failure.</summary>
    private sealed class ArchiveCopy(string older, string newer) : IRuntimeArchiveTransport
    {
        public async Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct)
        {
            var path = release.OriginalVersion == "v" + OlderVersion ? older : newer;
            await using var input = File.OpenRead(path);
            await input.CopyToAsync(destination, ct);
        }
    }

    private sealed class Authorization : ISingBoxArchiveAuthorization
    {
        internal ProxyServerMaterial? Published;
        public Task<SingBoxCurrentAuthorization> ReadAsync(CancellationToken ct) =>
            Task.FromResult(new SingBoxCurrentAuthorization(Published, DateTimeOffset.UtcNow.AddMinutes(1)));
    }
}
