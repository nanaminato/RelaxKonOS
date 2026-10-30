using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.WebServer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;

internal static class ComponentLifecycleChecks
{
    /// <summary>Captured from Debian 13 with a security archive that also publishes the version.</summary>
    private const string Policy = """
        nginx:
          Installed: (none)
          Candidate: 1.26.3-3+deb13u1
          Version table:
             1.26.3-3+deb13u1 500
                500 http://deb.debian.org/debian trixie/main amd64 Packages
                500 http://security.debian.org/debian-security trixie-security/main amd64 Packages
             1.26.3-3 500
                500 http://deb.debian.org/debian trixie/main amd64 Packages
        """;

    private const string PrintUris = """
        'http://deb.debian.org/debian/pool/main/n/nginx/nginx_1.26.3-3+deb13u1_amd64.deb' nginx_1.26.3-3+deb13u1_amd64.deb 540432 SHA256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF
        'http://deb.debian.org/debian/pool/main/n/nginx/nginx-common_1.26.3-3+deb13u1_all.deb' nginx-common_1.26.3-3+deb13u1_all.deb 102400 SHA256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210
        0 upgraded, 2 newly installed, 0 to remove and 0 not upgraded.
        """;

    public static async Task RunAsync()
    {
        VerifyRoutes();
        VerifyRegistry();
        VerifyRevision();
        var policy = VerifyPolicyParsing();
        var target = VerifyUriPlan(policy);
        VerifyTargetGuards(policy, target);
        VerifyStateMapping();
        VerifyPlanDecision();
        VerifyExecutionGuards();
        VerifyDeclaredModes();
        VerifyWindowsObservation();
        VerifyClientContract();
        await VerifyArchiveAdapterAsync();
        await VerifyPackageLedgerAsync();
    }

    private static async Task VerifyArchiveAdapterAsync()
    {
        foreach (var id in new[] { ComponentIds.SingBox, ComponentIds.Mihomo })
        {
            var installation = new RuntimeInstallation(Guid.NewGuid(), "host", id, 12, RuntimeOwnership.Managed,
                new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"), "release", true);
            var snapshot = new RuntimeInstallationSnapshot(installation, "1.0.0", Guid.NewGuid(), "config", null, null, null);
            RuntimePlanRequest? forwarded = null;
            string? source = null;
            var calls = 0;
            var adapter = new ArchiveProbe(id, _ => Task.FromResult(snapshot), (s, request, _) =>
            {
                calls++; source = s; forwarded = request;
                return Task.FromResult(new RuntimePlanResult(null, ["runtime.publication_unconfirmed"]));
            });
            var state = await adapter.StateAsync(default);
            Check(state.State is { RecoveryRequired: true, Revision: 12, ActiveVersion: "1.0.0" }
                && state.State.ComponentId == id, id + " preserves domain recovery, revision and measured version.");
            var request = new ComponentPlanRequest(id, "host", RuntimeChange.Update, 12,
                ComponentSourceMode.ManagedArchive, "source", 5, 6, "release-next", null, false, "original-key");
            var result = await adapter.PlanAsync(request, default);
            Check(!result.Allowed && result.Problems.SequenceEqual(["runtime.publication_unconfirmed"])
                && forwarded is { ExpectedCatalogRevision: 5, ExpectedTrustRevision: 6, KeepData: false, IdempotencyKey: "original-key" }
                && forwarded.InstallationId == installation.InstallationId && source == "source",
                id + " delegates immutable request identities and preserves publication refusal.");
            var conflict = await adapter.PlanAsync(request with { ExpectedRevision = 11 }, default);
            Check(conflict.Problems.Contains(ComponentProblemCodes.StateRevisionConflict) && calls == 1,
                id + " rejects stale state before domain planning.");
            var mode = await adapter.PlanAsync(request with { Mode = ComponentSourceMode.SystemPackage }, default);
            Check(mode.Problems.Contains(ComponentProblemCodes.SourceModeUnsupported) && calls == 1,
                id + " never routes a package request into archive planning.");
            var execution = await adapter.ExecuteAsync(new(id, request, null), default);
            Check(execution.Outcome == ComponentExecutionOutcome.Refused && calls == 1,
                id + " cannot bypass domain publication using component execute.");
            var wrong = new ArchiveProbe(id, _ => Task.FromResult(snapshot with
                { Installation = installation with { RuntimeId = "another-runtime" } }), (_, _, _) => throw new Exception());
            Check((await wrong.StateAsync(default)).State is null,
                id + " rejects observations belonging to another runtime.");
        }
    }

    private sealed class ArchiveProbe(string id, Func<CancellationToken, Task<RuntimeInstallationSnapshot>> query,
        Func<string?, RuntimePlanRequest, CancellationToken, Task<RuntimePlanResult>> plan)
        : ArchiveComponentAdapter(id, query, plan);

    private static async Task VerifyPackageLedgerAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "package-ledger-" + Guid.NewGuid().ToString("N")));
        var environment = new LedgerEnvironment { ContentRootPath = root };
        var store = new RelaxKonOS.Server.Installations.InstallationOperationStore(environment);
        var fingerprint = RelaxKonOS.Server.Installations.InstallationOperationStore.Reference("frozen-package-request");
        var entry = store.Create(RelaxKonOS.Protocol.Installations.InstallationServiceId.Nginx,
            RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install, "actor", "original-key", fingerprint,
            ["service:Nginx", "linux:apt-dpkg"], out var created);
        var reopened = new RelaxKonOS.Server.Installations.InstallationOperationStore(environment);
        var replay = reopened.Create(entry.Operation.Service, entry.Operation.Kind, "actor", "original-key", fingerprint,
            ["service:Nginx", "linux:apt-dpkg"], out var recreated);
        Check(created && !recreated && replay.Operation.OperationId == entry.Operation.OperationId,
            "A reopened durable ledger replays the original package operation identity.");
        var conflict = false;
        try { reopened.Create(entry.Operation.Service, entry.Operation.Kind, "actor", "original-key",
            RelaxKonOS.Server.Installations.InstallationOperationStore.Reference("changed-target"), ["service:Nginx"], out _); }
        catch (RelaxKonOS.Server.Installations.InstallationException) { conflict = true; }
        Check(conflict, "The same package operation key cannot accept a different frozen request.");
        conflict = false;
        try { reopened.Create(RelaxKonOS.Protocol.Installations.InstallationServiceId.Docker,
            entry.Operation.Kind, "actor", "other-key", fingerprint, ["linux:apt-dpkg"], out _); }
        catch (RelaxKonOS.Server.Installations.InstallationException) { conflict = true; }
        Check(conflict, "APT resources remain locked against another component after ledger reload.");
        reopened.Update(entry.Operation.OperationId, op => op with {
            State = RelaxKonOS.Protocol.Installations.InstallationOperationState.Interrupted,
            Stage = RelaxKonOS.Protocol.Installations.InstallationStage.Interrupted, CompletedAt = DateTimeOffset.UtcNow,
            ProblemCode = "installation.interrupted", Cancellable = false }, "recovered");
        Check(new RelaxKonOS.Server.Installations.InstallationOperationStore(environment).Read().Single().Operation.State
            == RelaxKonOS.Protocol.Installations.InstallationOperationState.Interrupted,
            "Interrupted package status persists across a second ledger reload.");
        var service = new RelaxKonOS.Server.Installations.NginxInstallationService(null!, null!,
            new ComponentLifecycleRegistry([new ProbeAdapter(ComponentIds.Nginx)]), null!);
        var state = NginxPackagePlan.BuildState(new("nginx", null, Candidate()), false, "host", "amd64");
        var request = new ComponentPlanRequest("nginx", "host", RuntimeChange.Install, state.Revision,
            ComponentSourceMode.SystemPackage, null, 0, 0, null, null, true, "frozen-key");
        var options = new RelaxKonOS.Protocol.Installations.NginxInstallationRequest(true,
            FrozenPackage: new("nginx", request, ComponentFrozenTarget.FromPackage(Candidate())));
        static System.Text.Json.JsonElement Json(object value) => System.Text.Json.JsonSerializer.SerializeToElement(value);
        Check(service.Validate(RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install, Json(options), "frozen-key") is not null,
            "Frozen nginx installation is admitted through the installation service.");
        foreach (var changed in new[] {
            options with { Confirmed = false }, options with { Version = "another-version" }, options with { FileReferenceId = "archive" },
            options with { FrozenPackage = options.FrozenPackage! with { Request = request with { SourceId = "unrelated-source" } } } })
        {
            var rejected = false;
            try { service.Validate(RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install, Json(changed), "frozen-key"); }
            catch (RelaxKonOS.Server.Installations.InstallationException) { rejected = true; }
            Check(rejected, "Frozen package admission rejects unconfirmed or mixed archive/source requests.");
        }
        Check(!AptPackageResolution.MatchesExpectation(Candidate(), Candidate() with { AlsoInstalls = [new("unexpected-package.deb", "https://repo.example/unexpected.deb", 100, new('b', 64))] }),
            "A dependency-set change invalidates the frozen package transaction.");
        Check(!AptPackageResolution.MatchesExpectation(Candidate(), Candidate() with {
            AlsoInstalls = [Candidate().AlsoInstalls[0] with { Sha256 = new('a', 64) }] }),
            "A dependency republished with different bytes invalidates the frozen transaction.");
        Check(!AptPackageResolution.TryParseUriPlan(PrintUris.Replace("102400 SHA256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210", "102400"),
            "nginx", "1.26.3-3+deb13u1", out _, out _), "A dependency without a digest cannot enter an install plan.");
        Check(!AptPackageResolution.MatchesExpectation(Candidate(), Candidate() with { PoolUri = "https://other.example/nginx.deb" }),
            "A changed package artifact location invalidates the frozen transaction.");
        var keyRejected = false;
        try { service.Validate(RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install, Json(options), "new-key"); }
        catch (RelaxKonOS.Server.Installations.InstallationException ex)
        { keyRejected = ex.ProblemCode == RelaxKonOS.Protocol.Installations.InstallationProblemCodes.IdempotencyConflict; }
        Check(keyRejected, "Frozen package idempotency key must equal the durable ledger submission key.");
        var recovered = await service.RecoverAsync(new(Guid.NewGuid(), RelaxKonOS.Protocol.Installations.InstallationServiceId.Nginx,
            RelaxKonOS.Protocol.Installations.InstallationOperationKind.Install,
            RelaxKonOS.Protocol.Installations.InstallationOperationState.Running,
            RelaxKonOS.Protocol.Installations.InstallationStage.Installing, null, null, DateTimeOffset.UtcNow, null, null, false), default);
        Check(recovered.State == RelaxKonOS.Protocol.Installations.InstallationOperationState.Interrupted,
            "Nginx recovery cannot invent original operation success from current host health or rerun APT.");
        Check(NginxPackagePlan.PrecheckFrozen(new("nginx", request with { HostId = "other-host" }, options.FrozenPackage!.Expected),
            state, new("nginx", null, Candidate())) is { Problems: [ComponentProblemCodes.RequestInvalid] },
            "Frozen installation never moves to another host.");
    }

    private sealed class LedgerEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "PackageChecks";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static void VerifyRoutes()
    {
        Check(ComponentRoutes.PlanPattern.Contains("{componentId}", StringComparison.Ordinal)
            && ComponentRoutes.StatePattern.Contains("{componentId}", StringComparison.Ordinal),
            "One route pair must carry every component; a per-product path would need a new endpoint per product.");
        Check(ComponentRoutes.Plan("nginx") == ComponentRoutes.Root + "/nginx/plan"
            && ComponentRoutes.State("sing-box") == ComponentRoutes.Root + "/sing-box/state",
            "Component route builders must stay in step with the mapped patterns.");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton<RelaxKonOS.Server.ManagedRuntimes.IRuntimeDownloadProxyProvider,
            TestRuntimeDownloadProxyProvider>();
        builder.Services.AddSingleton<ComponentLifecycleRegistry>(new ComponentLifecycleRegistry(
            [new ProbeAdapter(ComponentIds.SingBox), new ProbeAdapter(ComponentIds.Nginx), new ProbeAdapter(ComponentIds.Mihomo)]));
        using var app = builder.Build();
        app.MapComponentEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        Check(endpoints.Length == 3, "Component writes execute only through the durable installation ledger; component routes only discover releases, read and plan.");
        foreach (var endpoint in endpoints)
        {
            var isRead = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET");
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(item => item.Policy).ToArray();
            Check(policies.Contains(isRead ? RuntimeCatalogEndpoints.ReadPolicy : RuntimeCatalogEndpoints.ManagePolicy),
                "Component endpoints must reuse the runtime read/manage authorization boundary.");
        }
    }

    private static void VerifyRegistry()
    {
        var registry = new ComponentLifecycleRegistry([new ProbeAdapter(ComponentIds.SingBox), new ProbeAdapter(ComponentIds.Nginx), new ProbeAdapter(ComponentIds.Mihomo)]);
        Check(registry.Registered.SequenceEqual([ComponentIds.Mihomo, ComponentIds.Nginx, ComponentIds.SingBox]),
            "The registry must report exactly the adapters it was given.");
        Check(Throws<ComponentLifecycleException>(() => registry.Require("squid"), ComponentProblemCodes.UnknownComponent),
            "An unregistered component is unknown, not silently unsupported.");
        Check(Throws<ComponentLifecycleException>(() => registry.Require(null), ComponentProblemCodes.UnknownComponent),
            "A missing component id must not fall back to any adapter.");
        Check(Throws<InvalidOperationException>(() => new ComponentLifecycleRegistry(
                [new ProbeAdapter(ComponentIds.Nginx), new ProbeAdapter(ComponentIds.Nginx)]), null),
            "Two adapters for one component would make the answer depend on registration order.");
        Check(Throws<InvalidOperationException>(() => new ComponentLifecycleRegistry([new ProbeAdapter("unregistered-product")]), null),
            "An adapter outside the known component ids must fail at composition, not at request time.");
    }

    private static void VerifyRevision()
    {
        var first = ComponentRevision.Of("1.26.3", "1.26.3", "abc", "managed");
        Check(first == ComponentRevision.Of("1.26.3", "1.26.3", "abc", "managed") && first > 0,
            "A package revision must be stable for the same observed facts.");
        Check(first != ComponentRevision.Of("1.26.3", "1.26.4", "abc", "managed")
            && first != ComponentRevision.Of("1.26.3", "1.26.3", "abc", "unmanaged")
            && first != ComponentRevision.Of(null, "1.26.3", "abc", "managed"),
            "A candidate, ownership or installed-version change must move the revision, or a stale plan would still look current.");
    }

    private static AptPolicy VerifyPolicyParsing()
    {
        Check(AptPackageResolution.TryParsePolicy(Policy, "nginx", out var policy) && policy is not null,
            "The apt policy output of a normal Debian host must be understood.");
        Check(policy!.InstalledVersion is null && policy.CandidateVersion == "1.26.3-3+deb13u1",
            "Installed and candidate versions must come from apt's own answer.");
        Check(policy.Repositories.Count == 3 && policy.Repositories.All(item => item.Architecture == "amd64"
                && item.Suite is "trixie" or "trixie-security" && item.Area == "main"),
            "Every offered version must keep the repository that published it.");
        Check(AptPackageResolution.TryParsePolicy("nginx:\n  Installed: 1.26.3-3\n  Candidate: 1.26.3-3\n  Version table:\n *** 1.26.3-3 100\n        100 /var/lib/dpkg/status\n", "nginx", out var local)
            && local is { InstalledVersion: "1.26.3-3", CandidateVersion: "1.26.3-3", Repositories.Count: 0 },
            "A locally installed package has no publishing repository and must not invent one.");
        Check(!AptPackageResolution.TryParsePolicy(Policy, "squid", out _),
            "Another product's policy output must not be accepted as Nginx's.");
        return policy;
    }

    private static ComponentPackageTarget VerifyUriPlan(AptPolicy policy)
    {
        Check(AptPackageResolution.TryParseUriPlan(PrintUris, "nginx", "1.26.3-3+deb13u1", out var main, out var others),
            "The transaction apt would run must be readable, including its dependencies.");
        Check(main is { Bytes: 540432, Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }
            && main.Uri.EndsWith("nginx_1.26.3-3+deb13u1_amd64.deb", StringComparison.Ordinal),
            "The frozen artifact must be the package itself, with apt's lowercase digest.");
        Check(others.Count == 1 && others[0].FileName.StartsWith("nginx-common_", StringComparison.Ordinal),
            "Dependencies in the same transaction must be reported instead of silently installed.");
        Check(!AptPackageResolution.TryParseUriPlan(PrintUris, "nginx", "1.26.4-1", out _, out _),
            "A plan for a different version must not be accepted as this package's.");
        Check(!AptPackageResolution.TryParseUriPlan("'file:///tmp/nginx_1.26.3-3+deb13u1_amd64.deb' nginx_1.26.3-3+deb13u1_amd64.deb 1 SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "nginx", "1.26.3-3+deb13u1", out _, out _), "A non-HTTP artifact location is not a repository artifact.");

        Check(AptPackageResolution.TryBuildTarget(policy, main, others, out var target, out var problem) && target is not null && problem is null,
            "A resolved candidate with a publishing repository must freeze into one identity.");
        Check(target!.RepositoryId == "http://deb.debian.org/debian|trixie|main|amd64",
            "The repository identity must name the host, suite, area and architecture it came from.");
        Check(target.PackageName == "nginx" && target.Version == "1.26.3-3+deb13u1" && target.Architecture == "amd64"
            && target.Suite == "trixie" && target.AlsoInstalls.Count == 1,
            "The frozen target must carry the exact package, version, architecture and dependency set.");
        return target;
    }

    private static void VerifyTargetGuards(AptPolicy policy, ComponentPackageTarget target)
    {
        Check(!AptPackageResolution.TryBuildTarget(policy with { CandidateVersion = "1.26.4-1" },
                new AptUriLine("nginx_1.26.3-3+deb13u1_amd64.deb", target.PoolUri, target.Bytes, target.Sha256), [], out _, out var mismatch)
            && mismatch == ComponentProblemCodes.PackageVersionMismatch,
            "A transaction for another version must not be frozen under this candidate.");
        Check(!AptPackageResolution.TryBuildTarget(policy with { Repositories = [] }, 
                new AptUriLine("nginx_1.26.3-3+deb13u1_amd64.deb", target.PoolUri, target.Bytes, target.Sha256), [], out _, out var noRepository)
            && noRepository == ComponentProblemCodes.PackageIndexUnavailable,
            "A digest without a publishing repository is not a frozen source.");
        Check(!AptPackageResolution.TryBuildTarget(policy,
                new AptUriLine("nginx_1.26.3-3+deb13u1_amd64.deb", target.PoolUri, 0, target.Sha256), [], out _, out _),
            "A zero-length artifact must be refused before anything can install it.");
        Check(!AptPackageResolution.TryBuildTarget(policy,
                new AptUriLine("nginx_1.26.3-3+deb13u1_amd64.deb", target.PoolUri, target.Bytes, "not-a-digest"), [], out _, out _),
            "A malformed digest must never be recorded as verification evidence.");
    }

    private static void VerifyStateMapping()
    {
        var candidate = Candidate();
        var identity = new ComponentInstalledPackage("nginx", candidate.Version, "amd64", new('a', 64));
        var absent = NginxPackagePlan.BuildState(new("nginx", null, candidate), managed: false, "host", "amd64");
        Check(absent.Ownership == RuntimeOwnership.Absent && absent.ActiveVersion is null
            && absent.Mode == ComponentSourceMode.SystemPackage && absent.Target.InstallMode == RuntimeInstallMode.SystemPackage,
            "Nothing installed and no marker is Absent, and the state must name the package install mode.");
        Check(NginxPackagePlan.BuildState(new("nginx", "1.26.3-3+deb13u1", candidate), managed: true, "host", "amd64").Ownership == RuntimeOwnership.Managed,
            "A host package with the RelaxKonOS marker is Managed even though APT owns the executable.");
        Check(NginxPackagePlan.BuildState(new("nginx", "1.26.3-3+deb13u1", candidate), managed: false, "host", "amd64").Ownership == RuntimeOwnership.External,
            "A host package without the marker belongs to the host, not to the center.");
        Check(NginxPackagePlan.BuildState(new("nginx", null, null), managed: false, "host", null) is { ActiveVersion: null, Target.Architecture: "unknown" },
            "An unreadable architecture must be named unknown instead of guessed.");
    }

    private static void VerifyPlanDecision()
    {
        var candidate = Candidate();
        var identity = new ComponentInstalledPackage("nginx", candidate.Version, "amd64", new('a', 64));
        var absent = NginxPackagePlan.BuildState(new("nginx", null, candidate), managed: false, "host", "amd64");
        var installed = NginxPackagePlan.BuildState(new("nginx", "1.26.3-3+deb13u1", candidate), managed: true, "host", "amd64");
        ComponentPlanRequest Request(RuntimeChange change, long revision, string? version = null) => new("nginx", "host", change,
            revision, ComponentSourceMode.SystemPackage, null, 0, 0, null, version, KeepData: true, "key-1");

        var install = NginxPackagePlan.Evaluate(Request(RuntimeChange.Install, absent.Revision), absent, new("nginx", null, candidate), true);
        Check(install.Target is { Mode: ComponentSourceMode.SystemPackage, Package.Version: "1.26.3-3+deb13u1" }
            && install.Problems.Count == 0 && install.Allowed,
            "A first install of the resolved candidate is a plan this build can carry out");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Install, absent.Revision), absent, new("nginx", null, candidate), false)
            .Problems.SequenceEqual([ComponentProblemCodes.ExecutionUnavailable]),
            "Without an execution adapter the same plan must stay refused");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Install, absent.Revision + 1), absent, new("nginx", null, candidate), true)
            .Problems.Contains(ComponentProblemCodes.StateRevisionConflict),
            "A plan prepared against another revision must conflict rather than freeze the current host.");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Install, installed.Revision), installed, new("nginx", "1.26.3-3+deb13u1", candidate), true)
            .Problems.SequenceEqual([ComponentProblemCodes.PackageAlreadyInstalled]),
            "An install over a managed package is refused instead of reinstalling it under a new name.");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Uninstall, absent.Revision), absent, new("nginx", null, candidate), true)
            .Problems.SequenceEqual([ComponentProblemCodes.PackageAbsent, ComponentProblemCodes.PackageIdentityUnverified]),
            "Removal requires an installed, owned package with its original frozen target.");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Update, installed.Revision), installed,
                new("nginx", "1.26.3-3+deb13u1", candidate, CandidateVersion: candidate.Version, InstalledPackage: identity), true)
            .Problems.SequenceEqual([ComponentProblemCodes.PackageNoUpdate]),
            "An unchanged candidate cannot be presented as an update.");
        var newer = candidate with { Version = "1.26.4-1" };
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Update, installed.Revision), installed,
                new("nginx", candidate.Version, newer, Managed: true, CandidateVersion: newer.Version, UpdateAvailable: true, InstalledPackage: identity), true).Allowed,
            "An owned package with a newer resolved candidate can freeze an update.");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Uninstall, installed.Revision), installed,
                new("nginx", candidate.Version, candidate, Managed: true, InstalledTarget: candidate, InstalledPackage: identity), true).Allowed,
            "An owned installed package with its original target can freeze removal.");
        foreach (var change in new[] { RuntimeChange.Repair, RuntimeChange.Rollback })
            Check(NginxPackagePlan.Evaluate(Request(change, installed.Revision), installed,
                    new("nginx", "1.26.3-3+deb13u1", candidate), true).Problems.Contains(ComponentProblemCodes.ExecutionUnavailable),
                change + " stays refused because package repair and rollback are unavailable");
        var unresolved = NginxPackagePlan.Evaluate(Request(RuntimeChange.Install, absent.Revision), absent, new("nginx", null, null), true);
        Check(unresolved.Target is null && unresolved.Problems.SequenceEqual([ComponentProblemCodes.PackageUnresolved]),
            "With no candidate there is nothing to freeze, and the refusal must name that.");
        Check(NginxPackagePlan.Evaluate(Request(RuntimeChange.Install, absent.Revision, "1.26.4-1"), absent, new("nginx", null, candidate), true)
            .Problems.Contains(ComponentProblemCodes.PackageVersionMismatch),
            "A caller-named version that apt did not resolve must not be frozen in its place.");
    }

    private static void VerifyExecutionGuards()
    {
        var candidate = Candidate();
        var state = NginxPackagePlan.BuildState(new("nginx", null, candidate), managed: false, "host", "amd64");
        var resolution = new ComponentPackageResolution("nginx", null, candidate);
        ComponentPlanRequest Request(RuntimeChange change) => new("nginx", "host", change, state.Revision,
            ComponentSourceMode.SystemPackage, null, 0, 0, null, null, KeepData: true, "key-exec");
        static ComponentExecuteRequest Execute(ComponentPlanRequest request, ComponentFrozenTarget? expected) =>
            new("nginx", request, expected);
        static ComponentFrozenTarget Frozen(ComponentPackageTarget target) => ComponentFrozenTarget.FromPackage(target);
        static ComponentFrozenTarget FrozenRemoval(ComponentInstalledPackage identity) =>
            new(ComponentSourceMode.SystemPackage, null, null, InstalledPackage: identity);

        Check(NginxPackagePlan.PrecheckExecute(Execute(Request(RuntimeChange.Install), null), true) is
            { Outcome: ComponentExecutionOutcome.Refused } noExpectation
            && noExpectation.Problems.SequenceEqual([ComponentProblemCodes.ExecutionNotRequested]),
            "Execute refuses without the frozen identity the caller was shown");
        Check(NginxPackagePlan.PrecheckExecute(Execute(Request(RuntimeChange.Install), Frozen(candidate)), false) is
            { Problems: [ComponentProblemCodes.PlatformUnsupported] },
            "Execute refuses on a host whose platform has no package mode");
        var installed = new ComponentInstalledPackage("nginx", "1.26.3-3+deb13u1", "amd64", new('d', 64));
        Check(NginxPackagePlan.PrecheckExecute(Execute(Request(RuntimeChange.Uninstall), FrozenRemoval(installed)), true) is null
            && NginxPackagePlan.PrecheckExecute(Execute(Request(RuntimeChange.Uninstall), Frozen(candidate)), true) is
                { Problems: [ComponentProblemCodes.ExecutionNotRequested] },
            "Removal carries only the verified installed identity; a candidate-only freeze is refused");
        Check(NginxPackagePlan.PrecheckExecute(Execute(Request(RuntimeChange.Install) with
            { Mode = ComponentSourceMode.ManagedArchive }, Frozen(candidate)), true) is
            { Problems: [ComponentProblemCodes.SourceModeUnsupported] },
            "Execute refuses when the request names another install mode");
        Check(NginxPackagePlan.PrecheckExecute(Execute(Request(RuntimeChange.Install), Frozen(candidate)), true) is null,
            "A first install with the frozen identity passes the guards that need no host access");
        Check(NginxPackagePlan.PrecheckFrozen(Execute(Request(RuntimeChange.Install), Frozen(candidate)), state, resolution) is null,
            "An unchanged host accepts the identity it just showed");
        Check(NginxPackagePlan.PrecheckFrozen(Execute(Request(RuntimeChange.Install), Frozen(candidate with { Sha256 = new('a', 64) })),
                state, resolution) is { Problems: [ComponentProblemCodes.PackageExpectationChanged] },
            "A republished package with another digest refuses instead of installing it");
        Check(NginxPackagePlan.PrecheckFrozen(Execute(Request(RuntimeChange.Install), Frozen(candidate)),
                state with { Revision = state.Revision + 1 }, resolution) is { Problems: [ComponentProblemCodes.StateRevisionConflict] },
            "A host that changed since the plan refuses the install");
        Check(NginxPackagePlan.PrecheckFrozen(Execute(Request(RuntimeChange.Install), Frozen(candidate)), state,
                resolution with { Candidate = candidate with { Version = "1.26.4-1" } }) is
            { Problems: [ComponentProblemCodes.PackageExpectationChanged] },
            "A package that now resolves to another version refuses the install");
    }

    /// <summary>
    /// The platform decides the install mode, and declaring a mode is not the same as implementing it.
    /// This is checked without constructing the adapter: the manager behind it needs a whole host.
    /// </summary>
    internal static void VerifyWindowsObservation()
    {
        var defaults = RelaxKonOS.PrivilegedHelper.WindowsManagedRuntimePolicy.Create(null, null, null, [], null);
        defaults.Validate();
        Check(defaults.NginxReleases is [{ Version: "1.30.6", Sha256.Length: 64 }],
            "Default Windows Helper policy supplies the publisher-verified nginx release.");
        var disabled = RelaxKonOS.PrivilegedHelper.WindowsManagedRuntimePolicy.Create(null, null, null, [], null, []);
        Check(disabled.NginxReleases.Count == 0,
            "An explicitly empty administrator catalog stays empty instead of silently restoring defaults.");
        var absent = new ComponentArchiveHostSnapshot(false, false, null, [], null, false);
        Check(NginxComponentAdapter.MapArchive(absent, "host", "x64") is { Ownership: RuntimeOwnership.Absent, Target.Os: "windows" },
            "Windows absence comes from the protected managed slot, not an empty discovery list.");
        var external = NginxComponentAdapter.MapArchive(absent with { Installed = true }, "host", "x64");
        Check(external.Ownership == RuntimeOwnership.External, "Windows occupied slot without an original receipt is external.");
        var managed = absent with { Installed = true, Managed = true, Version = "1.28.0", CommittedOperationId = Guid.NewGuid() };
        var state = NginxComponentAdapter.MapArchive(managed, "host", "x64");
        Check(state is { Ownership: RuntimeOwnership.Managed, ActiveVersion: "1.28.0", Target.ServiceHost: "scm" },
            "Windows managed state keeps the protected archive version and service host.");
        Check(NginxComponentAdapter.MapArchive(managed with { Version = "1.28.1" }, "host", "x64").Revision != state.Revision,
            "Windows archive version change invalidates frozen state.");
        Check(NginxComponentAdapter.MapArchive(managed with { RecoveryRequired = true }, "host", "x64").RecoveryRequired,
            "Windows archive recovery is preserved.");
    }

    private static void VerifyDeclaredModes()
    {
        var modes = NginxComponentAdapter.DeclaredModes;
        Check(modes.Count == 1, "Nginx declares exactly one install mode per platform.");
        if (OperatingSystem.IsLinux())
        {
            Check(modes[0] is { Mode: ComponentSourceMode.SystemPackage, Platform: "linux", Available: true, ProblemCode: null },
                "On Linux the mode is the distribution package, and this build can resolve it.");
            return;
        }
        Check(modes[0] is { Mode: ComponentSourceMode.ManagedArchive, Available: true, ProblemCode: null },
            "On Windows the archive adapter is connected; installed targets still require protected release pins.");
    }

    /// <summary>The client renders these modes; changing the shape without the client is a silent mismatch.</summary>
    private static void VerifyClientContract()
    {
        var view = new ComponentStateView(ComponentIds.Nginx, null,
            [new ComponentModeSupport(ComponentSourceMode.SystemPackage, "linux", true, null)], []);
        var json = System.Text.Json.JsonSerializer.Serialize(view, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
        Check(json.Contains("\"systemPackage\"", StringComparison.Ordinal) && json.Contains("\"modes\"", StringComparison.Ordinal),
            "Component enums travel as names so a client never has to guess ordinals.");
        var request = new ComponentPlanRequest(ComponentIds.Nginx, "host", RuntimeChange.Install, 1, ComponentSourceMode.SystemPackage,
            null, 0, 0, null, "1.26.3-3+deb13u1", KeepData: true, "key-1");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ComponentPlanRequest>(
            System.Text.Json.JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default),
            RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
        Check(roundTrip == request, "A plan request must survive the client/server round trip unchanged.");
    }

    private static ComponentPackageTarget Candidate() => new("nginx", "1.26.3-3+deb13u1", "amd64",
        "http://deb.debian.org/debian|trixie|main|amd64", "http://deb.debian.org/debian", "trixie", "main",
        "http://deb.debian.org/debian/pool/main/n/nginx/nginx_1.26.3-3+deb13u1_amd64.deb",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 540432, [new("nginx-common_1.26.3-3+deb13u1_all.deb", "http://deb.debian.org/debian/pool/main/n/nginx/nginx-common_1.26.3-3+deb13u1_all.deb", 102400, "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210")]);

    private static bool Throws<T>(Action action, string? code) where T : Exception
    {
        try { action(); return false; }
        catch (T exception) { return code is null || exception is ComponentLifecycleException lifecycle && lifecycle.ProblemCode == code; }
        catch { return false; }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private sealed class ProbeAdapter(string componentId) : IComponentLifecycleAdapter
    {
        public string ComponentId => componentId;
        public IReadOnlyList<ComponentModeSupport> Modes => [new(ComponentSourceMode.ManagedArchive, "linux", true, null)];
        public Task<ComponentStateView> StateAsync(CancellationToken ct) => Task.FromResult(new ComponentStateView(componentId, null, Modes, []));
        public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct) =>
            Task.FromResult(new ComponentPlanResult(null, null, null, [ComponentProblemCodes.RequestInvalid], []));
        public Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct) =>
            Task.FromResult(new ComponentExecutionResult(componentId, ComponentExecutionOutcome.Refused,
                [ComponentProblemCodes.ExecutionUnavailable], [], null, null));
    }

}
