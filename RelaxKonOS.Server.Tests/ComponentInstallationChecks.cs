using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Runtimes;

internal static class ComponentInstallationChecks
{
    public static async Task RunAsync()
    {
        Check(HostPackageDefinitions.Find("arbitrary-package") is null, "Package definitions form a closed product mapping");
        var operation = Guid.NewGuid();
        foreach (var id in new[] { "squid", "samba", "docker" })
        {
            var definition = HostPackageDefinitions.Find(id)!;
            var target = Target(definition.PackageName);
            var resolution = new ComponentPackageResolution(definition.PackageName, null, target);
            var transport = new Transport(_ => new(true, ComponentPackage: resolution));
            var adapter = new HostPackageComponentAdapter(id, transport);
            var absent = adapter.Map(resolution);
            Check(absent is { Ownership: RuntimeOwnership.Absent, Mode: ComponentSourceMode.SystemPackage }, id + " package state preserves absence");
            var managed = adapter.Map(resolution with { InstalledVersion = target.Version, Managed = true, CommittedOperationId = operation });
            Check(managed.Ownership == RuntimeOwnership.Managed && managed.Revision != absent.Revision, id + " commit changes ownership and state revision");
            var pending = adapter.Map(resolution with { InstalledVersion = target.Version, RecoveryRequired = true, PendingOperationId = operation });
            Check(pending.RecoveryRequired && pending.Ownership == RuntimeOwnership.External, id + " uncommitted install never acquires ownership");
            var request = new ComponentPlanRequest(id, Environment.MachineName, RuntimeChange.Install, absent.Revision,
                ComponentSourceMode.SystemPackage, null, 0, 0, null, target.Version, true, "original-key");
            var frozen = new ComponentExecuteRequest(id, request, ComponentFrozenTarget.FromPackage(target));
            ComponentPackageInstallations.Validate(id, InstallationOperationKind.Install, frozen, "original-key");
            Check(true, id + " frozen package is admitted without shell parameters");
            var rejected = false;
            try { ComponentPackageInstallations.Validate(id, InstallationOperationKind.Install,
                frozen with { ComponentId = "nginx" }, "original-key"); }
            catch (InstallationException) { rejected = true; }
            Check(rejected, id + " cannot substitute another component identity");
            rejected = false;
            try { ComponentPackageInstallations.Validate(id, InstallationOperationKind.Install, frozen, "different-key"); }
            catch (InstallationException ex) { rejected = ex.ProblemCode == InstallationProblemCodes.IdempotencyConflict; }
            Check(rejected, id + " frozen key is bound to ledger acceptance");
            resolution = resolution with { InstalledVersion = target.Version, Managed = true, CommittedOperationId = operation };
            Check((await ComponentPackageInstallations.RecoverAsync(id, operation, transport, default, InstallationOperationKind.Install)).State
                == InstallationOperationState.Succeeded, id + " original protected commit can reconcile its install");
            resolution = resolution with { CommittedOperationId = Guid.NewGuid() };
            Check((await ComponentPackageInstallations.RecoverAsync(id, operation, transport, default, InstallationOperationKind.Install)).State
                == InstallationOperationState.Interrupted, id + " a later healthy install cannot replace original operation evidence");
            resolution = resolution with { Managed = false, RecoveryRequired = true, CommittedOperationId = null };
            Check((await ComponentPackageInstallations.RecoverAsync(id, operation, transport, default, InstallationOperationKind.Install)).State
                == InstallationOperationState.Interrupted, id + " drift and pending state remain interrupted");
            resolution = resolution with { RecoveryReceipt = new(operation, Guid.NewGuid(), ComponentRecoveryAction.Close) };
            Check((await ComponentPackageInstallations.RecoverAsync(id, operation, transport, default, InstallationOperationKind.Repair)).State
                == InstallationOperationState.Succeeded, id + " operator closure reconciles its own recovery action");
            Check((await ComponentPackageInstallations.RecoverAsync(id, operation, transport, default, InstallationOperationKind.Install)).State
                == InstallationOperationState.Interrupted, id + " closing an intent never converts original installation into success");
        }
        await VerifyPinnedFrpAsync();
        await VerifyWindowsPinsAsync();
        await VerifyPinnedTransportAsync();
    }
    private static ComponentPackageTarget Target(string package) => new(package, "1.2.3-1", "amd64",
        "https://repo.example|stable|main|amd64", "https://repo.example", "stable", "main",
        "https://repo.example/" + package + ".deb", new('a', 64), 1024, []);
    private static async Task VerifyPinnedFrpAsync()
    {
        var runtime = DispatchProxy.Create<IRuntimeManager, RuntimeProbe>();
        var probe = (RuntimeProbe)(object)runtime;
        var options = Options.Create(new FrpRuntimeOptions());
        var platform = OperatingSystem.IsWindows() ? "win-" : "linux-";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var release = options.Value.Releases.Single(pin => pin.Rid == platform + arch);
        var configured = new FrpRuntimeOptions();
        configured.BindConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Releases:0:Version"] = release.Version,
            ["Releases:0:Rid"] = release.Rid,
            ["Releases:0:Url"] = release.Url,
            ["Releases:0:Sha256"] = release.Sha256,
            ["Releases:0:ArchiveFormat"] = release.ArchiveFormat
        }).Build());
        Check(configured.Releases.Count == 1 && configured.Releases.Single().Rid == release.Rid,
            "Configured FRP releases replace defaults without duplicate or unconfigured targets");
        IReadOnlyList<ComponentPinnedArchive> pins = [PinnedArchives.Frp(new(release.Version, release.Rid, release.Url, release.Sha256, release.ArchiveFormat))];
        var helper = new Transport(request => {
            Check(request.Operation == PrivilegedOperationKind.FrpArchiveCatalog, "FRP planning consults the independent Helper pin directory");
            return new(true, PinnedArchives: pins);
        });
        foreach (var id in new[] { "frpc", "frps" })
        {
            var adapter = new FrpComponentAdapter(id, runtime, options, helper);
            var view = await adapter.StateAsync(default);
            Check(view.State?.Ownership == RuntimeOwnership.Absent && view.PinnedArchives?.Count == 1,
                id + " exposes only host and Helper-approved bundle releases");
            var pin = view.PinnedArchives!.Single();
            var request = new ComponentPlanRequest(id, view.State!.HostId, RuntimeChange.Install, view.State.Revision,
                ComponentSourceMode.ManagedArchive, "host-pins", view.PinRevision, 0, pin.ReleaseId, pin.Version, true, "bundle-key");
            var plan = await adapter.PlanAsync(request, default);
            Check(plan.Allowed && plan.Target?.Pin?.AffectedComponents.SequenceEqual(["frpc", "frps"]) == true,
                id + " frozen install explicitly affects both executables");
            var conflict = await adapter.PlanAsync(request with { ExpectedSourceRevision = view.PinRevision + 1 }, default);
            Check(!conflict.Allowed && conflict.Problems.Contains(ComponentProblemCodes.StateRevisionConflict), id + " stale pin directories refuse installation");
            probe.Partial = true;
            Check((await adapter.StateAsync(default)).State is { RecoveryRequired: true, Ownership: RuntimeOwnership.Unknown },
                id + " a partial bundle is unknown and cannot be overwritten");
            probe.Partial = false;
        }
        pins = [pins.Single() with { Sha256 = new('b', 64) }];
        var blocked = await new FrpComponentAdapter("frpc", runtime, options, helper).StateAsync(default);
        Check(blocked.PinnedArchives?.Count == 0, "Server-only pin changes cannot widen Helper archive trust");
    }
    private static async Task VerifyWindowsPinsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "nginx-pin-query-" + Guid.NewGuid().ToString("N")));
        var policy = RelaxKonOS.PrivilegedHelper.WindowsManagedRuntimePolicy.Create(Path.Combine(root, "nginx-parent", "nginx"),
            Path.Combine(root, "private"), [], [], null, [new("1.28.0", new('a', 64))]);
        policy.Validate();
        var result = await RelaxKonOS.PrivilegedHelper.WindowsManagedRuntimeHost.PinnedNginxAsync(new(PrivilegedOperationKind.NginxArchiveQuery), policy);
        Check(result.ComponentArchive is { Installed: false, Managed: false, Releases.Count: 1 }, "Windows protected slot query exports exact registered pins without claiming an installation");
        Check(result.ComponentArchive!.Releases.Single().SourceUri == "https://nginx.org/download/nginx-1.28.0.zip",
            "Windows nginx pins cannot choose another publisher URL");
        var rejected = await RelaxKonOS.PrivilegedHelper.WindowsManagedRuntimeHost.FileAsync(new(PrivilegedOperationKind.NginxWriteManagedFile,
            Path: Path.Combine(policy.NginxRoot, ".helper-component-install.json"), ContentBase64: Convert.ToBase64String("{}"u8)), policy);
        Check(!rejected.Success, "Server file writes cannot forge protected nginx commit receipts");
        var changed = result.ComponentArchive.Releases.Single() with { Sha256 = new('b', 64) };
        var install = await RelaxKonOS.PrivilegedHelper.WindowsManagedRuntimeHost.PinnedNginxAsync(new(PrivilegedOperationKind.NginxArchiveInstall,
            ArchiveExpectation: changed, OperationId: Guid.NewGuid()), policy);
        Check(!install.Success && !Directory.Exists(policy.NginxRoot), "A changed nginx archive digest is refused before download or filesystem mutation");
    }
    private static async Task VerifyPinnedTransportAsync()
    {
        var frp = PinnedArchives.Frp(PinnedArchives.LinuxFrp(false));
        var frpScope = new RelaxKonOS.ManagedRuntimes.RuntimePinnedArchiveTransfer.Scope(frp);
        frpScope.Validate();
        foreach (var host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com" })
            Check(frpScope.AllowsQuery(new Uri("https://" + host + "/asset?signature=opaque")),
                "FRP permits signed queries on its approved GitHub artifact CDN");
        foreach (var url in new[] { "https://github.com/asset?token=opaque", "https://release-assets.githubusercontent.com.evil.example/asset?token=x",
            "http://release-assets.githubusercontent.com/asset?token=x", "https://release-assets.githubusercontent.com:444/asset?token=x",
            "https://user@release-assets.githubusercontent.com/asset?token=x", "https://release-assets.githubusercontent.com/asset?token=x#fragment" })
            Check(!frpScope.AllowsQuery(new Uri(url)), "FRP query scope refuses unapproved origin, credentials or fragment");
        var pin = new ComponentPinnedArchive("release", "1.28.0", "win-x64", "https://nginx.org/download/nginx-1.28.0.zip",
            new('a', 64), "zip", 1024, ["nginx"]);
        Check(!new RelaxKonOS.ManagedRuntimes.RuntimePinnedArchiveTransfer.Scope(pin).AllowsQuery(
            new Uri("https://release-assets.githubusercontent.com/asset?signature=x")), "Nginx does not inherit GitHub artifact query permissions");
        foreach (var invalid in new[] {
            pin with { SourceUri = "https://unapproved.example/nginx.zip" },
            pin with { SourceUri = "http://nginx.org/download/nginx-1.28.0.zip" },
            pin with { SourceUri = "https://nginx.org/download/nginx-1.28.0.zip?token=secret" },
            pin with { MaximumBytes = long.MaxValue }, pin with { Sha256 = "missing" } })
        {
            using var output = new MemoryStream();
            var refused = false;
            try { await new RelaxKonOS.ManagedRuntimes.RuntimePinnedArchiveTransfer().CopyAsync(invalid, output, null, default); }
            catch (RelaxKonOS.ManagedRuntimes.RuntimePolicyException) { refused = true; }
            Check(refused && output.Length == 0, "Pinned archive transport refuses unapproved source or limits before connecting");
        }
    }

    public class RuntimeProbe : DispatchProxy
    {
        public bool Partial;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch {
            "GetManagedFrpcStatusAsync" => Task.FromResult(new TunnelRuntimeDto("frp", TunnelRuntimeMode.Managed,
                Partial ? TunnelRuntimeState.Available : TunnelRuntimeState.NotInstalled, Partial ? "v0.71.0" : null,
                null, IntegrityVerified: Partial)),
            "GetManagedFrpsStatusAsync" => Task.FromResult(new TunnelRuntimeDto("frp", TunnelRuntimeMode.Managed, TunnelRuntimeState.NotInstalled, null, null)),
            _ => throw new InvalidOperationException("Planning must never execute a runtime mutation") };
    }
    private sealed class Transport(Func<PrivilegedOperationRequest, PrivilegedOperationResult> response) : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(response(request));
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
