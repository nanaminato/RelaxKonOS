using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Fixed real unit, reviewed upstream binaries, controller and real local HTTP through the mixed listener.</summary>
internal static class MihomoSystemdAcceptance
{
    [SupportedOSPlatform("linux")]
    internal static async Task<int> RunAsync(RuntimeSourceStore sources, RuntimeCatalog catalog, string olderArchive, string newerArchive)
    {
        if (!Environment.IsPrivilegedProcess || !Directory.Exists("/run/systemd/system"))
            throw new Exception("Mihomo systemd acceptance requires root and systemd");
        var host = new LinuxMihomoArchiveHost();
        var root = LinuxMihomoArchiveHost.Root;
        if ((await host.ObserveAsync(default)).Exists || Directory.Exists(root))
            throw new Exception("Mihomo acceptance requires an absent fixed service and runtime root");
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        count += await SandboxAsync();
        const int controllerPort = 19090, mixedPort = 17892;
        const string secret = "systemd-mihomo-acceptance-secret";
        var older = catalog.Releases.Single(r => r.SortVersion == "1.19.10");
        var newer = catalog.Releases.Single(r => r.SortVersion == "1.19.30");
        var yaml = MihomoManagedConfiguration.WithRuntimeSettings(
            MihomoManagedConfiguration.WithServerControllerSettings("rules:\n  - MATCH,DIRECT\n",
                new() { Endpoint = new($"http://127.0.0.1:{controllerPort}/") }, secret),
            new ProxySettingsDto(false, false, false, false, false, "info", mixedPort));
        var published = new MihomoPublishedRuntime(yaml, ["with_gvisor"],
            ControllerPort: controllerPort, ControllerSecret: secret,
            Listeners: [new("127.0.0.1", mixedPort)]);
        var grant = new Authorization(published);
        var states = new RuntimeArchiveStateStore(Path.Combine(root, "state"), RuntimeSourceProtection.Protect);
        var lifecycle = new MihomoRuntimeLifecycle(root, states, sources, host, RuntimeSourceProtection.Protect,
            transport: new Transport(older.ReleaseId, olderArchive, newerArchive));
        var stages = new List<InstallationStage>();
        Action<InstallationStage>? configurationStage = null;
        async Task<T> HelperAsync<T>(PrivilegedOperationRequest request)
        {
            var result = await MihomoRuntimeOperations.ExecuteAsync(request, () => lifecycle, frame =>
            {
                if (frame.Stage is { } stage) { stages.Add(stage); configurationStage?.Invoke(stage); }
                return Task.CompletedTask;
            });
            if (!result.Success || result.OutputBase64 is null) throw new Exception("Mihomo Helper refusal: " + result.Error);
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64))!;
        }
        async Task<RuntimeSwitchResult> ExecuteAsync(RuntimeChangePlan plan)
        {
            var planned = await HelperAsync<RuntimePlanResult>(new(PrivilegedOperationKind.MihomoRuntimePlan,
                OperationId: Guid.NewGuid(), RuntimeLifecycle: new(SourceId: plan.Release?.SourceId ?? "removal", Plan: plan.Request)));
            if (planned.Plan is null) throw new Exception("Mihomo plan refusal: " + string.Join(',', planned.Problems));
            var operationId = Guid.NewGuid();
            await using var callback = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(operationId, grant.ReadAsync);
            return await HelperAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeExecute, OperationId: operationId,
                RuntimeLifecycle: new(Authorization: callback.Channel, FrozenPlan: planned.Plan)));
        }
        RuntimeChangePlan Plan(RuntimeChange action, RuntimeRelease release, string key)
        {
            var current = states.Read()!;
            return new(new(current.Installation.InstallationId, current.Installation.HostId, action,
                current.Installation.StateRevision, catalog.Revision, catalog.TrustRevision, release.ReleaseId, key),
                "mihomo", release, current.Active?.Release.ReleaseId);
        }
        try
        {
            var initial = await HelperAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: Guid.NewGuid()));
            Check(initial.Installation.Ownership == RuntimeOwnership.Absent, "Helper initializes the real host-bound absent installation");
            var install = await ExecuteAsync(Plan(RuntimeChange.Install, older, "systemd-install"));
            Check(install.Outcome == RuntimeSwitchOutcome.Succeeded, "Real systemd install: " + install.ProblemCode);
            Check(grant.Reads > 0 && stages.Contains(InstallationStage.Downloading) && stages.Contains(InstallationStage.HealthChecking),
                "Real Helper transaction reads the operation-scoped typed callback and reports measured stages");
            var snapshot = await HelperAsync<RuntimeInstallationSnapshot>(new(PrivilegedOperationKind.MihomoRuntimeQuery, OperationId: install.OperationId));
            Check(snapshot.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded } && !JsonSerializer.Serialize(snapshot).Contains(secret, StringComparison.Ordinal),
                "Public Helper observation has the operation receipt without protected controller material");
            var observed = await host.ObserveAsync(default);
            Check(observed.Exists && !observed.Running && observed.BootEnabled == false, "Real registration starts neither service nor boot enablement");
            await host.StartAsync(default);
            Check(await HealthyAsync(host, published), "Real old release owns listener and authenticated version controller");
            Check(await ProbeAsync(mixedPort), "Old release forwards real HTTP through its mixed listener");
            await host.SetBootAsync(true, default);
            Check((await host.ObserveAsync(default)) is { Running: true, BootEnabled: true }, "Boot enablement does not stop a running service");

            var configurationBefore = states.Read()!;
            var configurationRevision = configurationBefore.Active!.RevisionId;
            var configurationMarker = Path.Combine(root, "data", configurationRevision.ToString("N"), "configuration-acceptance");
            await File.WriteAllTextAsync(configurationMarker, "confirmed-data");
            var candidate = published with { Yaml = published.Yaml.Replace("mixed-port: " + mixedPort, "mixed-port: " + (mixedPort + 1), StringComparison.Ordinal),
                Listeners = [new("127.0.0.1", mixedPort + 1)] };
            Check(candidate.Yaml != published.Yaml, "Configuration fixture changes a real effective mixed listener");
            var denyCandidate = false;
            var denyPrevious = false;
            async Task<RuntimeSwitchResult> ConfigureAsync(MihomoPublishedRuntime previous, MihomoPublishedRuntime next, string key)
            {
                var current = states.Read()!;
                var operation = Guid.NewGuid();
                var plan = new RuntimeConfigurationPlan(current.Installation.InstallationId, current.Installation.HostId, "mihomo",
                    current.Installation.StateRevision, current.Active!.RevisionId,
                    MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(previous.Yaml)), MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(next.Yaml)), key);
                await using var callback = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(operation, _ =>
                    Task.FromResult(new MihomoConfigurationAuthorization(operation, DateTimeOffset.UtcNow.AddSeconds(30),
                        denyPrevious ? null : previous, denyCandidate ? null : next)));
                return await HelperAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeConfigure,
                    OperationId: operation, RuntimeLifecycle: new(Authorization: callback.Channel, ConfigurationPlan: plan)));
            }
            var configured = await ConfigureAsync(published, candidate, "configuration-change");
            Check(configured.Outcome == RuntimeSwitchOutcome.Succeeded && await HealthyAsync(host, candidate)
                && await ProbeAsync(mixedPort + 1), "Real configuration publication changes listener and forwards HTTP: " + configured.ProblemCode);
            Check(states.Read()!.Active!.RevisionId == configurationRevision && states.Read()!.Revisions.Length == configurationBefore.Revisions.Length
                && (await host.ObserveAsync(default)) is { Running: true, BootEnabled: true },
                "Configuration publication preserves program revision, runtime data location and service flags");
            Check(await File.ReadAllTextAsync(configurationMarker) == "confirmed-data"
                && File.Exists(Path.Combine(root, "operations", configured.OperationId.ToString("N"), "data-backup", "configuration-acceptance")),
                "Quiescent data backup remains identified by the publication receipt");
            Check((await ConfigureAsync(candidate, published, "configuration-back")).Outcome == RuntimeSwitchOutcome.Succeeded
                && await ProbeAsync(mixedPort), "A second explicit publication restores the original listener");

            configurationStage = stage =>
            {
                if (stage != InstallationStage.HealthChecking) return;
                File.WriteAllText(configurationMarker, "candidate-data");
                denyCandidate = true;
            };
            var rejected = await ConfigureAsync(published, candidate, "configuration-rejected");
            configurationStage = null;
            Check(rejected.Outcome == RuntimeSwitchOutcome.Failed && await HealthyAsync(host, published)
                && await ProbeAsync(mixedPort), "Revoked candidate is compensated using separately authorized old material");
            Check(await File.ReadAllTextAsync(configurationMarker) == "confirmed-data"
                && states.Read()!.ConfigurationIdentity == MihomoArchiveAdapter.Hash(Encoding.UTF8.GetBytes(published.Yaml)),
                "Failed configuration restores captured data and verified old identity");
            denyCandidate = false;
            configurationStage = stage =>
            {
                if (stage != InstallationStage.HealthChecking) return;
                File.WriteAllText(configurationMarker, "candidate-data-for-recovery");
                denyCandidate = true; denyPrevious = true;
            };
            var interrupted = await ConfigureAsync(published, candidate, "configuration-recovery");
            configurationStage = null;
            Check(interrupted.Outcome == RuntimeSwitchOutcome.RecoveryRequired && states.Read()!.Configuration is not null
                && !(await host.ObserveAsync(default)).Running, "Revoked old authorization leaves the real service stopped and transaction pending");
            var configurationRecovery = await HelperAsync<RuntimeRecoverySnapshot>(new(PrivilegedOperationKind.MihomoRuntimeRecoveryQuery, OperationId: Guid.NewGuid()));
            Check(configurationRecovery.Change == RuntimeChange.Configure && configurationRecovery.OperationId == interrupted.OperationId
                && configurationRecovery.NeedsOperatorDecision, "Helper recovery query identifies the real pending publication");
            var recoveryOperation = Guid.NewGuid();
            await using (var callback = new RuntimeAuthorizationServer<MihomoConfigurationAuthorization>(recoveryOperation, _ =>
                Task.FromResult(new MihomoConfigurationAuthorization(interrupted.OperationId, DateTimeOffset.UtcNow.AddSeconds(30), published, null))))
            {
                var recovered = await HelperAsync<RuntimeSwitchResult>(new(PrivilegedOperationKind.MihomoRuntimeRecover,
                    OperationId: recoveryOperation, RuntimeLifecycle: new(Authorization: callback.Channel, RecoveryOperationId: interrupted.OperationId)));
                Check(recovered.Outcome == RuntimeSwitchOutcome.Interrupted && states.Read()!.Configuration is null
                    && await HealthyAsync(host, published) && await ProbeAsync(mixedPort),
                    "Fresh recovery channel compensates the named publication without needing the revoked candidate");
            }
            Check(await File.ReadAllTextAsync(configurationMarker) == "confirmed-data", "Delayed recovery restores the original data snapshot");
            denyCandidate = false; denyPrevious = false;
            await host.StopAsync(default);
            Check((await ConfigureAsync(published, candidate, "configuration-stopped")).Outcome == RuntimeSwitchOutcome.Succeeded
                && (await host.ObserveAsync(default)) is { Running: false, BootEnabled: true },
                "Publishing to a stopped service preserves independent boot enablement without starting it");
            Check((await ConfigureAsync(candidate, published, "configuration-stopped-back")).Outcome == RuntimeSwitchOutcome.Succeeded,
                "Stopped configuration returns to the publication used by later version transactions");
            await host.StartAsync(default);
            Check(await HealthyAsync(host, published), "Explicit start uses the newly confirmed stopped configuration");

            var oldRevision = states.Read()!.Active!;
            var oldMarker = Path.Combine(root, "data", oldRevision.RevisionId.ToString("N"), "acceptance-selection");
            await File.WriteAllTextAsync(oldMarker, "old-data");
            var update = await ExecuteAsync(Plan(RuntimeChange.Update, newer, "systemd-update"));
            Check(update.Outcome == RuntimeSwitchOutcome.Succeeded, "Real cross-version systemd update: " + update.ProblemCode);
            var upgraded = states.Read()!.Active!;
            Check(upgraded.MeasuredVersion == "1.19.30" && (await host.ObserveAsync(default)) is { Running: true, BootEnabled: true },
                "Real update changes measured program and preserves independent service flags");
            Check(await ProbeAsync(mixedPort), "Updated release forwards real HTTP after restart");
            var upgradedMarker = Path.Combine(root, "data", upgraded.RevisionId.ToString("N"), "acceptance-selection");
            Check(await File.ReadAllTextAsync(upgradedMarker) == "old-data", "Real service update carries its stopped data into an independent revision");
            await File.WriteAllTextAsync(upgradedMarker, "new-data");
            Check(await File.ReadAllTextAsync(oldMarker) == "old-data", "Running newer service leaves old data intact");

            var repair = await ExecuteAsync(Plan(RuntimeChange.Repair, newer, "systemd-repair"));
            Check(repair.Outcome == RuntimeSwitchOutcome.Succeeded && states.Read()!.Active!.RevisionId != upgraded.RevisionId,
                "Real same-version repair replaces the revision");
            Check(await ProbeAsync(mixedPort), "Repaired service still forwards HTTP");
            var rollback = await ExecuteAsync(Plan(RuntimeChange.Rollback, older, "systemd-rollback"));
            Check(rollback.Outcome == RuntimeSwitchOutcome.Succeeded && states.Read()!.Active!.MeasuredVersion == "1.19.10",
                "Real systemd rollback switches back to the older measured version: " + rollback.ProblemCode);
            Check(await File.ReadAllTextAsync(Path.Combine(root, "data", states.Read()!.Active!.RevisionId.ToString("N"), "acceptance-selection")) == "old-data",
                "Real rollback restores old data instead of passing it a newer database");
            Check(await ProbeAsync(mixedPort), "Rolled-back release forwards real HTTP");
            Check(!await host.ListenersHealthyAsync(published with { ControllerSecret = "wrong-acceptance-secret" }, default),
                "A listening controller with the wrong credential is unhealthy");
            await host.SetBootAsync(false, default);
            Check((await host.ObserveAsync(default)) is { Running: true, BootEnabled: false }, "Disabling boot keeps the real service running");
            await host.StopAsync(default);
            Check(!(await host.ObserveAsync(default)).Running, "Real service stops");
            RuntimeChangePlan RemovePlan(string key, bool keepData)
            {
                var current = states.Read()!;
                return new(new(current.Installation.InstallationId, current.Installation.HostId, RuntimeChange.Uninstall,
                    current.Installation.StateRevision, 0, 0, null, key, KeepData: keepData), "mihomo", null,
                    current.Active!.Release.ReleaseId);
            }
            Check((await ExecuteAsync(RemovePlan("systemd-retain", true))).Outcome == RuntimeSwitchOutcome.Succeeded
                && !(await host.ObserveAsync(default)).Exists, "Retention uninstall removes only the owned real registration");
            Check(File.Exists(Path.Combine(root, "retained-data.json")) && File.Exists(Path.Combine(root, "active.yaml")),
                "Real retention uninstall keeps configuration and data provenance");
            var reinstall = await ExecuteAsync(Plan(RuntimeChange.Install, older, "systemd-reinstall"));
            Check(reinstall.Outcome == RuntimeSwitchOutcome.Succeeded && await File.ReadAllTextAsync(Path.Combine(root, "data",
                states.Read()!.Active!.RevisionId.ToString("N"), "acceptance-selection")) == "old-data",
                "Real service reinstalls the retained old data");
            await host.StartAsync(default);
            Check(await HealthyAsync(host, published) && await ProbeAsync(mixedPort), "Reinstalled service authenticates and forwards traffic");
            Check((await ExecuteAsync(RemovePlan("systemd-delete", false))).Outcome == RuntimeSwitchOutcome.Succeeded
                && !(await host.ObserveAsync(default)).Exists && !Directory.Exists(Path.Combine(root, "data"))
                && !File.Exists(Path.Combine(root, "active.yaml")), "Explicit real uninstall removes owned data and service");
        }
        finally
        {
            // This test claimed only an initially absent fixed root/unit, never an existing installation.
            await host.StopAsync(default);
            await host.RemoveOwnedRegistrationAsync(default);
            if (Path.GetFullPath(root) != LinuxMihomoArchiveHost.Root) throw new Exception("Acceptance cleanup escaped its fixed root");
            Directory.Delete(root, true);
        }
        return count;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> SandboxAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var work = Path.Combine(Path.GetTempPath(), "mihomo-check-sandbox-" + id);
        var outside = "/var/lib/relaxkonos/mihomo-sandbox-probe-" + id;
        Directory.CreateDirectory(work);
        var data = Path.Combine(work, "data"); Directory.CreateDirectory(data);
        var binary = Path.Combine(work, "check-fixture");
        var configuration = Path.Combine(work, "profile.yaml");
        try
        {
            await File.WriteAllTextAsync(outside, "unchanged");
            await File.WriteAllTextAsync(Path.Combine(work, "hidden"), "host-temp-file");
            await File.WriteAllTextAsync(configuration, "# read-only input\n");
            await File.WriteAllTextAsync(binary, $"""
                #!/bin/sh
                set -eu
                test "$1" = -t
                test "$2" = -d
                test "$4" = -f
                test -r "$5"
                test ! -e '{work}/hidden'
                printf isolated > "$3/proof"
                if /usr/bin/touch '{outside}' 2>/dev/null; then exit 30; fi
                if /usr/bin/touch "$5" 2>/dev/null; then exit 31; fi
                if /usr/bin/python3 -c 'import socket; socket.socket(socket.AF_INET, socket.SOCK_STREAM)' 2>/dev/null; then exit 32; fi
                echo 'test is successful'
                """ + "\n");
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await MihomoOperations.TestConfigurationAsync(binary, data, configuration);
            if (await File.ReadAllTextAsync(outside) != "unchanged" || await File.ReadAllTextAsync(Path.Combine(data, "proof")) != "isolated")
                throw new Exception("Native checker sandbox crossed its write scope");
            return 4; // permitted data write, forbidden host/config writes, hidden temp, forbidden IPv4 socket
        }
        finally
        {
            File.Delete(outside);
            if (Path.GetDirectoryName(Path.GetFullPath(work)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Sandbox test cleanup escaped its temporary root");
            Directory.Delete(work, true);
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<bool> HealthyAsync(LinuxMihomoArchiveHost host, MihomoPublishedRuntime published)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await host.ListenersHealthyAsync(published, default)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<bool> ProbeAsync(int mixedPort)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var length = 0;
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                if ((length += line.Length) > 8192) throw new IOException("Acceptance request headers too large");
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 15\r\nConnection: close\r\n\r\nmihomo-evidence"u8.ToArray(), timeout.Token);
        }, timeout.Token);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{mixedPort}"),
            UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            var response = await client.GetStringAsync($"http://127.0.0.1:{port}/", timeout.Token);
            await server;
            return response == "mihomo-evidence";
        }
        finally { timeout.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } }
    }

    private sealed class Authorization(MihomoPublishedRuntime published) : IMihomoArchiveAuthorization
    {
        internal int Reads;
        public Task<MihomoCurrentAuthorization> ReadAsync(CancellationToken ct)
        { Reads++; return Task.FromResult(new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddMinutes(1), published)); }
    }
    private sealed class Transport(string olderId, string older, string newer) : IRuntimeArchiveTransport
    {
        public async Task CopyAsync(RuntimeSource source, ManifestRelease release, Stream destination, CancellationToken ct)
        {
            await using var input = File.OpenRead(release.ReleaseId == olderId ? older : newer);
            await input.CopyToAsync(destination, ct);
        }
    }
}
