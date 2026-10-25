using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

const string helperCode = "proxy.privileged_operation_unavailable";
if (args.FirstOrDefault() is "--component-center-host" or "--component-center-observe-host" or "--component-center-cancel-host")
    AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
else
    AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
SynchronizationContext.SetSynchronizationContext(null);
// These tests only read localization; no appearance changes or desktop are needed.
// The SSH desktop session is only consulted for desktop-language overrides, so it stays unconnected here.
using var services = new ServiceCollection()
    .AddSingleton(new LocalizationService(new ShellSettings(null!), new SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!))))
    .BuildServiceProvider();
typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
if (args.FirstOrDefault() == "--component-center-host")
{
    Environment.ExitCode = await ComponentCenterHostAcceptance.RunAsync(args);
    return;
}
if (args.FirstOrDefault() == "--component-center-observe-host")
{
    Environment.ExitCode = await ComponentCenterHostObservation.RunAsync(args);
    return;
}
if (args.FirstOrDefault() == "--component-center-cancel-host")
{
    Environment.ExitCode = await ComponentCenterHostCancellation.RunAsync(args);
    return;
}
var session = DispatchProxy.Create<IAuthSession, SessionStub>();
var settings = DispatchProxy.Create<IAppSettingsClient, SettingsStub>();
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}
var componentLink = ComponentCenterNavigation.Component("https://host.example/", "relaxkonos.proxy-server", "sing-box");
Check(ComponentCenterNavigation.TryParse(componentLink, out var linkTarget)
    && linkTarget is { HostId: "https://host.example/", AppId: "relaxkonos.proxy-server", ComponentId: "sing-box" },
    "Component links preserve the stable target host and a catalog-controlled return application");
foreach (var suffix in new[] { "&execute=true", "&appId=relaxkonos.proxy-server", "&workloadId=unknown", "#execute" })
    Check(!ComponentCenterNavigation.TryParse(new Uri(componentLink.AbsoluteUri + suffix), out _),
        "Component links reject unknown actions, duplicate fields, unknown workloads and fragments: " + suffix);
Check(!ComponentCenterNavigation.TryParse(new Uri("relaxkonos://components/sing-box?hostId=host&appId=relaxkonos.settings"), out _)
    && !ComponentCenterNavigation.TryParse(new Uri("relaxkonos://components/unknown?hostId=host&appId=relaxkonos.proxy-server"), out _),
    "Component links cannot nominate arbitrary applications or component IDs");
ComponentSelectionChecks.Run();
ComponentDraftPersistenceChecks.Run();
await RuntimeReattachmentChecks.RunAsync();
await ApplicationAvailabilityChecks.RunAsync();
await TaskPreviewClientChecks.RunAsync();
await TaskExecutionClientChecks.RunAsync();
await RuntimeCommitWireChecks.RunAsync();
await ComponentTaskExecutionChecks.RunAsync();
await ComponentCenterAccessChecks.RunAsync();
ComponentCenterHostAcceptanceChecks.Run();
// A frozen plan is the only installation body the ledger accepts for Mihomo.
static ManagedRuntimeInstallationRequest RuntimePlanBody() => new(true, new RuntimeChangePlan(
    new RuntimePlanRequest(Guid.NewGuid(), "host-test", RuntimeChange.Install, 1, 12, 7, "rel-mihomo", Guid.NewGuid().ToString("N")),
    "mihomo", null, null));
static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
{
    Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default)
};

using (var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.NotFound, new { problemCode = helperCode }))))
{
    var client = new InstallationClient(http, session);
    Check(await client.GetActiveAsync(InstallationServiceId.Mihomo, default) is null, "Missing active task is an empty result");
    using var vm = new InstallationTaskViewModel(client, settings, InstallationServiceId.Mihomo, "test", () => Task.CompletedTask, () => Task.FromResult<string?>(null));
    var dialogs = 0;
    vm.ShowPrivilegedHelperUnavailableAsync = code => { Check(code == helperCode, "Helper error is preserved"); dialogs++; return Task.CompletedTask; };
    Check(await vm.CreateFileReferenceAsync("/mihomo.gz") is null && vm.HasMessage && dialogs == 1,
        "File-reference POST failure remains visible and opens guidance");
    await vm.SubmitAsync(InstallationOperationKind.Install, RuntimePlanBody());
    Check(vm.HasMessage && dialogs == 2, "Install POST failure is not silently treated as no result");
}

var operation = new InstallationOperationDto(Guid.NewGuid(), InstallationServiceId.Mihomo, InstallationOperationKind.Install,
    InstallationOperationState.Running, InstallationStage.Preparing, null, null, DateTimeOffset.UtcNow, null, null, false);
using (var http = new HttpClient(new Handler(request => Json(HttpStatusCode.OK,
    request.Method == HttpMethod.Post ? operation : operation with
    { State = InstallationOperationState.Failed, Stage = InstallationStage.Failed, ProblemCode = helperCode }))))
{
    var completed = new TaskCompletionSource();
    var dialogs = 0;
    using var vm = new InstallationTaskViewModel(new InstallationClient(http, session), settings, InstallationServiceId.Mihomo,
        "test", () => { completed.SetResult(); return Task.CompletedTask; }, () => Task.FromResult<string?>(null));
    vm.ShowPrivilegedHelperUnavailableAsync = code => { Check(code == helperCode, "Polled helper error is preserved"); dialogs++; return Task.CompletedTask; };
    await vm.SubmitAsync(InstallationOperationKind.Install, RuntimePlanBody());
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(dialogs == 1 && vm.HasMessage && vm.Operation?.State == InstallationOperationState.Failed,
        "Asynchronous archive installation failure opens guidance once and retains feedback");
}

using (var handler = new BlockingTransferHandler())
using (var http = new HttpClient(handler))
using (var vm = new InstallationTaskViewModel(new InstallationClient(http, session), settings,
    InstallationServiceId.Nginx, "test", () => Task.CompletedTask, () => Task.FromResult<string?>(null)))
{
    using var package = new MemoryStream([1, 2, 3]);
    var transfer = vm.UploadPackageAsync("nginx.zip", package);
    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(vm.IsActive && vm.IsIndeterminate && vm.HasMessage && vm.CancelCommand.CanExecute(null),
        "Package transfer exposes progress and cancellation before a server task exists");
    Check(await vm.UploadPackageAsync("duplicate.zip", package) is null,
        "Concurrent package transfer is rejected");
    await vm.CancelCommand.ExecuteAsync(null);
    Check(await transfer.WaitAsync(TimeSpan.FromSeconds(5)) is null && handler.Cancelled,
        "Cancel interrupts the transfer HTTP request and returns no install reference");
    Check(!vm.IsActive && !vm.CancelCommand.CanExecute(null) && vm.HasMessage,
        "Cancelled transfer clears active state and retains feedback");
}

// Managed runtime page: display admission, closed source routes, and plan-first execution.
var runtimeTarget = new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd");
var runtimeSource = new RuntimeSourceView(
    new RuntimeSourceIdentity("singbox", "RelaxKon", true, RuntimeTrust.Confirmed, 7), "sing-box",
    RuntimeSourceKind.HttpsManifest, "https://runtime.example.test/", "FINGERPRINT", 3);
static RuntimeArtifact RuntimeArtifactFixture() => new("artifact-" + Guid.NewGuid().ToString("N"), new string('a', 64), 4096,
    "zip", "sing-box", "release-key", new RuntimeArchiveLayout(8192, 4,
        [new RuntimeArchiveFile("sing-box", 4096, new string('b', 64), true)]));
static RuntimeRelease RuntimeReleaseFixture(string id, string version, bool supported) => new(id, "sing-box", version, version,
    RuntimeChannel.Stable, "singbox", new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd"),
    RuntimeArtifactFixture(), "sing-box-v1", supported, RuntimeTrust.Confirmed, RuntimeEvidence.NativeConfiguration,
    new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
var runtimeCatalog = new RuntimeCatalog("singbox", 12, 7, DateTimeOffset.UtcNow.AddDays(1), true,
[
    RuntimeReleaseFixture("rel-1160", "1.16.0", true),
    RuntimeReleaseFixture("rel-1143", "1.14.3", true),
    RuntimeReleaseFixture("rel-rc", "1.17.0-rc.1", false)
]);
var runtimeInstallation = new RuntimeInstallation(Guid.NewGuid(), "host-test", "sing-box", 4, RuntimeOwnership.Managed,
    runtimeTarget, "rel-1143", false);
var runtimeInstalled = new[] { new RuntimeInstalledRelease(Guid.NewGuid(), "rel-1143", "1.14.3") };
var runtimeOperationId = Guid.NewGuid();
var runtimeCalls = new List<string>();
var runtimePlanBody = "";
var runtimeRefreshFails = true;
var runtimeNeedsRecovery = false;
var runtimePendingId = Guid.NewGuid();
var runtimeRecoveryBody = "";
// The Host measures itself: capability names and byte counts, never a path or a command.
var runtimeEnvironment = new RuntimeDependencyReport([
    new(RuntimeDependencyKind.Platform, "linux/amd64", RuntimeDependencyState.Satisfied, Measured: "linux/amd64"),
    new(RuntimeDependencyKind.ServiceHost, "systemd", RuntimeDependencyState.Satisfied, Measured: "systemd"),
    new(RuntimeDependencyKind.DiskSpace, "protected-root", RuntimeDependencyState.Satisfied, 4L << 30),
    new(RuntimeDependencyKind.FileSystem, "protected-root", RuntimeDependencyState.Satisfied),
    new(RuntimeDependencyKind.KernelFeature, "ipv6", RuntimeDependencyState.Satisfied)]);
var runtimeElevations = 0;
var runtimePrompts = 0;
var runtimeObservationFails = false;
var runtimeActiveReleaseMissing = false;
var runtimeElevationRequired = true;
var runtimeRecovery = new RuntimeRecoverySnapshot(true, runtimePendingId, RuntimeChange.Update, InstallationStage.HealthChecking,
    true, "runtime.switch_failed", "runtime.compensation_health_failed", "rel-1160", "rel-1143",
    new RuntimeServiceObservation(true, true, true));
RuntimePlanResult runtimePlan = new(new RuntimeChangePlan(new RuntimePlanRequest(runtimeInstallation.InstallationId,
    runtimeInstallation.HostId, RuntimeChange.Update, runtimeInstallation.StateRevision, runtimeCatalog.Revision, 7,
    "rel-1160", "idem", false), "sing-box", runtimeCatalog.Releases[0], "rel-1143"), []);
var mihomoCalls = new List<string>();
var mihomoElevation = "";
using (var mihomoHttp = new HttpClient(new Handler(request =>
{
    var path = request.RequestUri!.AbsolutePath;
    mihomoCalls.Add(request.Method.Method + " " + path);
    if (path == PrivilegedApiRoutes.Elevation)
    {
        mihomoElevation = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, new HostElevationResult(true));
    }
    if (path == RuntimeRoutes.MihomoInstallation)
        return Json(HttpStatusCode.OK, new RuntimeInstallationSnapshot(runtimeInstallation with { RuntimeId = "mihomo" }, null, null, null, null, null, null));
    if (path == RuntimeRoutes.MihomoRecovery) return Json(HttpStatusCode.OK, runtimeRecovery);
    if (path == RuntimeRoutes.MihomoRecoveryRetry) return Json(HttpStatusCode.OK, new RuntimeSwitchResult(runtimePendingId, RuntimeSwitchOutcome.Succeeded, null));
    if (path == RuntimeRoutes.MihomoRecoveryStop) return Json(HttpStatusCode.OK, runtimeRecovery with { Service = new(false, false, false) });
    if (path == RuntimeRoutes.MihomoRecoveryClose) return Json(HttpStatusCode.OK, new RuntimeSwitchResult(runtimePendingId, RuntimeSwitchOutcome.Succeeded, null));
    if (path == RuntimeRoutes.MihomoPlan) return Json(HttpStatusCode.OK, new RuntimePlanResult(null, ["runtime.release_untrusted"]));
    if (path.StartsWith("/api/v1.0/installations/", StringComparison.Ordinal)) return Json(HttpStatusCode.OK,
        new InstallationOperationDto(runtimeOperationId, InstallationServiceId.Mihomo, InstallationOperationKind.Upgrade,
            InstallationOperationState.Queued, InstallationStage.Queued, null, null, DateTimeOffset.UtcNow, null, null, false));
    return Json(HttpStatusCode.NotFound, new { code = "runtime.request_failed" });
})))
{
    var mihomoClient = new MihomoRuntimeCatalogClient(mihomoHttp, session);
    Check(mihomoClient.RuntimeId == "mihomo" && mihomoClient.SupportsRecoveryStopAndClose,
        "Mihomo has an explicit runtime binding and advertises the recovery verbs its Server routes implement");
    await mihomoClient.InstallationAsync(default);
    await mihomoClient.PlanAsync("source", runtimePlan.Plan!.Request, default);
    await mihomoClient.RecoveryAsync(default);
    await mihomoClient.RetryRecoveryAsync(runtimePendingId, default);
    await mihomoClient.StartAsync(InstallationOperationKind.Upgrade, new(true, runtimePlan.Plan with { RuntimeId = "mihomo", Release = runtimePlan.Plan.Release! with { RuntimeId = "mihomo" } }), "idem", default);
    await mihomoClient.ElevateAsync("fixture", default);
    Check(mihomoCalls.Contains("GET " + RuntimeRoutes.MihomoInstallation)
        && mihomoCalls.Contains("POST " + RuntimeRoutes.MihomoPlan)
        && mihomoCalls.Contains("GET " + RuntimeRoutes.MihomoRecovery)
        && mihomoCalls.Contains("POST " + RuntimeRoutes.MihomoRecoveryRetry)
        && mihomoCalls.Any(c => c.Contains("/installations/Mihomo/Upgrade", StringComparison.OrdinalIgnoreCase))
        && !mihomoCalls.Any(c => c.Contains("sing-box", StringComparison.OrdinalIgnoreCase)),
        "Mihomo observations, plans, ledger and recovery never use sing-box routes");
    var elevationRequest = System.Text.Json.JsonSerializer.Deserialize<HostElevationRequest>(mihomoElevation, RelaxKonOSJsonOptions.Default);
    Check(elevationRequest is { Capability: HostElevationCapability.MihomoInstall, Target: "mihomo" },
        "Mihomo elevation binds its own capability and exact target");
    var stopped = await mihomoClient.StopRecoveryAsync(runtimePendingId, default);
    var closed = await mihomoClient.CloseRecoveryAsync(runtimePendingId, default);
    Check(stopped.Service is { Running: false } && closed is { Outcome: RuntimeSwitchOutcome.Succeeded }
        && mihomoCalls.Contains("POST " + RuntimeRoutes.MihomoRecoveryStop)
        && mihomoCalls.Contains("POST " + RuntimeRoutes.MihomoRecoveryClose)
        && !mihomoCalls.Any(c => c.Contains("/sing-box/recovery/", StringComparison.Ordinal)),
        "Mihomo stop/close use only their own closed recovery routes");
    var beforeManualRecovery = mihomoCalls.Count;
    try { await mihomoClient.StartAsync(InstallationOperationKind.Upgrade, new(true, runtimePlan.Plan), "idem", default); throw new Exception("Cross-runtime plan accepted"); }
    catch (ArgumentException) { }
    Check(mihomoCalls.Count == beforeManualRecovery, "Mihomo rejects a sing-box frozen plan before any HTTP submission");
}
using (var runtimeHttp = new HttpClient(new Handler(request =>
{
    var path = request.RequestUri!.AbsolutePath;
    runtimeCalls.Add(request.Method.Method + " " + path);
    if (path.EndsWith("/managed-runtimes/sources", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, new[] { runtimeSource, runtimeSource with { RuntimeId = "mihomo" } });
    if (path.EndsWith("/catalog", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, new RuntimeCatalogSnapshot(runtimeSource, runtimeCatalog with
            { Releases = runtimeCatalog.Releases.Where(r => !runtimeActiveReleaseMissing || r.ReleaseId != runtimeInstallation.ActiveReleaseId).Append(runtimeCatalog.Releases[0] with { RuntimeId = "mihomo", ReleaseId = "other-runtime-release" }).ToArray() }, true, null));
    if (path.EndsWith("/refresh", StringComparison.Ordinal))
        return runtimeRefreshFails
            ? Json(HttpStatusCode.BadGateway, new { code = "runtime.catalog_missing" })
            : Json(HttpStatusCode.OK, new RuntimeCatalogSnapshot(runtimeSource, runtimeCatalog, true, null));
    if (path.EndsWith("/sing-box/installation", StringComparison.Ordinal) && runtimeObservationFails)
        return Json(HttpStatusCode.BadGateway, new { code = "runtime.observation_failed" });
    if (path.EndsWith("/sing-box/installation", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, new RuntimeInstallationSnapshot(
            runtimeNeedsRecovery ? runtimeInstallation with { RecoveryRequired = true } : runtimeInstallation, "1.14.3",
            Guid.NewGuid(), new string('c', 64), null, null, null, null, runtimeInstalled, runtimeEnvironment));
    if (path.EndsWith("/sing-box/plan", StringComparison.Ordinal))
    {
        runtimePlanBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, runtimePlan);
    }
    // The first privileged recovery action is refused until the administrator grants elevation in place.
    if (path.EndsWith("/sing-box/recovery/stop", StringComparison.Ordinal) && runtimeElevationRequired)
    {
        runtimeElevationRequired = false;
        return Json(HttpStatusCode.Forbidden, new { code = "elevation-required" });
    }
    if (path.EndsWith("/privileged/elevation", StringComparison.Ordinal))
    {
        runtimeElevations++;
        return Json(HttpStatusCode.OK, new HostElevationResult(true));
    }
    if (path.EndsWith("/sing-box/recovery/retry", StringComparison.Ordinal))
    {
        runtimeRecoveryBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, new RuntimeSwitchResult(runtimePendingId, RuntimeSwitchOutcome.RecoveryRequired,
            "runtime.recovery_required"));
    }
    if (path.EndsWith("/sing-box/recovery/stop", StringComparison.Ordinal))
    {
        runtimeRecoveryBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        runtimeRecovery = runtimeRecovery with { Service = new RuntimeServiceObservation(true, false, false) };
        return Json(HttpStatusCode.OK, runtimeRecovery);
    }
    if (path.EndsWith("/sing-box/recovery/close", StringComparison.Ordinal))
    {
        runtimeRecoveryBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Json(HttpStatusCode.OK, new RuntimeSwitchResult(runtimePendingId, RuntimeSwitchOutcome.Failed,
            "runtime.recovery_closed_by_operator"));
    }
    if (path.EndsWith("/sing-box/recovery", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, runtimeRecovery);
    if (path.Contains("/installations/", StringComparison.Ordinal)
        && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put))
        return Json(HttpStatusCode.OK, new InstallationOperationDto(runtimeOperationId, InstallationServiceId.SingBox,
            InstallationOperationKind.Upgrade, InstallationOperationState.Queued, InstallationStage.Queued, null, null,
            DateTimeOffset.UtcNow, null, null, false));
    if (path.Contains("/installations/", StringComparison.Ordinal))
        return Json(HttpStatusCode.OK, new InstallationOperationDto(runtimeOperationId, InstallationServiceId.SingBox,
            InstallationOperationKind.Upgrade, InstallationOperationState.Succeeded, InstallationStage.Completed, 100, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false));
    return Json(HttpStatusCode.NotFound, new { code = "runtime.source_not_found" });
})))
{
    var catalogClient = new SingBoxRuntimeCatalogClient(runtimeHttp, session);
    var rejected = false;
    try { await catalogClient.CatalogAsync("../escape", false, default); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected && runtimeCalls.Count == 0, "Runtime client rejects an invalid source id before any request");

    using var runtimeVm = new ProxyServerRuntimeViewModel(catalogClient) { CanRead = true };
    runtimeVm.ConfirmAsync = (_, _) => Task.FromResult(true);
    await runtimeVm.LoadAsync();
    Check(runtimeVm.Sources.Count == 1 && runtimeVm.Releases.Count == 3,
        "Runtime page lists only sources and releases for its explicitly bound runtime");
    var blocked = runtimeVm.Releases.Single(option => option.Release.ReleaseId == "rel-rc");
    Check(blocked.HasBlocked && blocked.Blocked.Resolve() == LocalizedText.Get("proxy_server.runtime_problem.configuration_unsupported"),
        "A release without declared configuration support shows its block reason");
    Check(runtimeVm.SelectedRelease?.Release.ReleaseId == "rel-1143"
        && runtimeVm.ActionText.Resolve() == LocalizedText.Get("proxy_server.runtime.action.Repair"),
        "The active release is preselected as a same-version repair");
    Check(runtimeVm.HasEnvironment && runtimeVm.EnvironmentPlatformText.Resolve() == "linux/amd64"
        && runtimeVm.EnvironmentServiceHostText.Resolve() == "systemd"
        && runtimeVm.EnvironmentDiskSpaceText.Resolve() == "4 GiB"
        && runtimeVm.EnvironmentFileSystemText.Resolve() == LocalizedText.Get("proxy_server.runtime.environment.state.Satisfied")
        && runtimeVm.EnvironmentKernelText.Resolve() == LocalizedText.Get("proxy_server.runtime.environment.ipv6.Satisfied")
        && !runtimeVm.HasEnvironmentWarning,
        "The page shows what the host measured, in capability names and sizes only");
    Check(!runtimeVm.EnvironmentPlatformText.Resolve().Contains("/var/", StringComparison.Ordinal)
        && !runtimeVm.EnvironmentDiskSpaceText.Resolve().Contains("root", StringComparison.Ordinal),
        "The environment panel never renders a filesystem path");
    Check(!runtimeVm.CanAct, "Without host management permission the runtime page offers no action");
    runtimeVm.CanManage = true;
    runtimeVm.SelectedRelease = runtimeVm.Releases.Single(option => option.Release.ReleaseId == "rel-1160");
    Check(runtimeVm.CanAct && runtimeVm.ActionText.Resolve() == LocalizedText.Get("proxy_server.runtime.action.Update"),
        "A newer supported release is offered as an update");
    ComponentChangeDraft? staged = null;
    runtimeVm.StageChange = draft => { staged = draft; return true; };
    var callsBeforeStage = runtimeCalls.Count;
    await runtimeVm.ActCommand.ExecuteAsync(null);
    Check(staged is { RuntimeId: "sing-box", ReleaseId: "rel-1160", Change: RuntimeChange.Update }
        && staged.InstallationId == runtimeInstallation.InstallationId && runtimeCalls.Count == callsBeforeStage,
        "Staging a component intent neither plans nor executes and binds its exact host and installation");
    runtimeVm.StageChange = null;
    var store = new ComponentDraftStore();
    var actor = Guid.NewGuid();
    using (var center = new ComponentCenterViewModel(runtimeVm, store, "server-a", actor))
    {
        Check(center.Stage(staged!) && center.Drafts.Count == 1,
            "The component center stores explicit local intent");
        Check(!center.Stage(staged! with { ReleaseId = "other-release" }) && center.Drafts.Single().Draft == staged,
            "A conflicting version cannot overwrite an existing draft by last selection");
        var beforeNavigation = runtimeCalls.Count;
        center.Navigate(ComponentCenterPage.Installed);
        Check(center.HasResults && center.HasRuntimeDetails, "Installed view uses host ownership rather than checkbox state");
        center.Navigate(ComponentCenterPage.Updates);
        Check(center.HasResults && runtimeVm.Updates.Count == 1 && runtimeVm.Updates[0].Version == "1.16.0",
            "Updates excludes the active repair target and unsupported releases");
        center.Navigate(ComponentCenterPage.Workloads);
        Check(center.HasWorkloadDetails && center.VisibleRoots.Single().IsChecked == true,
            "Workload references aggregate the same shared draft without duplicating it");
        center.VisibleRoots.Single().IsChecked = false;
        Check(center.Drafts.Count == 0 && runtimeCalls.Count == beforeNavigation,
            "Unchecking a workload removes local intent without uninstalling its shared component");
        center.VisibleRoots.Single().IsChecked = true;
        Check(center.Drafts.Count == 0 && center.VisibleRoots.Single().IsChecked == false,
            "Selecting an installed workload never disguises a repair as installation");
        center.Stage(staged!);
        center.Search = "no-such-component";
        Check(!center.HasResults && !center.HasRuntimeDetails && center.Drafts.Count == 1,
            "Search and page filters preserve drafts while hiding unmatched detail");
        center.Locate(new("server-a", "relaxkonos.proxy-server", "sing-box", null, ComponentCenterPage.Sources));
        Check(center.SelectedPage == ComponentCenterPage.Sources && center.HasRuntimeDetails
            && center.Drafts.Count == 1 && runtimeCalls.Count == beforeNavigation,
            "Page navigation and controlled locating never plan, approve or execute a draft");
    }
    Check(store.Read("server-b", actor).Count == 0 && store.Read("server-a", Guid.NewGuid()).Count == 0,
        "Saved drafts never cross server or actor boundaries");
    var saved = store.Read("server-a", actor).Single();
    using (var reopened = new ComponentCenterViewModel(runtimeVm, store, "server-a", actor))
    {
        Check(reopened.Drafts.Single().Draft == saved && runtimeCalls.Count == callsBeforeStage,
            "Reopening restores intent without planning, approval or execution");
        reopened.RemoveCommand.Execute(reopened.Drafts.Single());
        Check(store.Read("server-a", actor).Count == 0,
            "Removing draft intent only clears local state");
    }
    var startsBeforeStale = runtimeCalls.Count(call => call.StartsWith("POST /api/v1.0/installations/", StringComparison.Ordinal));
    Check(!await runtimeVm.ApplyDraftAsync(saved with { HostId = "different-host" })
        && runtimeCalls.Count(call => call.StartsWith("POST /api/v1.0/installations/", StringComparison.Ordinal)) == startsBeforeStale,
        "A draft from another actual host is rejected before execution");
    runtimeVm.ConfirmAsync = null;
    await runtimeVm.ApplyDraftAsync(saved);
    Check(!runtimeCalls.Any(call => call.StartsWith("POST /api/v1.0/installations/", StringComparison.Ordinal)),
        "A missing frozen-plan approval host never silently starts a component change");
    runtimeVm.ConfirmAsync = (_, _) => Task.FromResult(false);
    await runtimeVm.ActCommand.ExecuteAsync(null);
    Check(!runtimeCalls.Any(call => call.StartsWith("POST /api/v1.0/installations/", StringComparison.Ordinal)),
        "Declining a frozen component plan sends no execution request");
    runtimeVm.ConfirmAsync = (_, message) =>
    {
        Check(message.Resolve().Contains("1.16.0", StringComparison.Ordinal),
            "Frozen component approval names the exact target version");
        return Task.FromResult(true);
    };
    await runtimeVm.ActCommand.ExecuteAsync(null);
    Check(runtimeCalls.Any(call => call.Equals("POST /api/v1.0/installations/SingBox/Upgrade", StringComparison.OrdinalIgnoreCase))
        && runtimePlanBody.Contains("rel-1160", StringComparison.Ordinal)
        && runtimePlanBody.Contains("\"change\":\"update\"", StringComparison.Ordinal),
        "Executing selects the exact release and hands the plan to the installation ledger as an upgrade");
    Check(!runtimePlanBody.Contains("http", StringComparison.OrdinalIgnoreCase) && runtimeVm.OutcomeText.Resolve()
        == LocalizedText.Get("proxy_server.runtime.action_done"),
        "The plan carries no URL or executable path and reports completion after the ledger succeeds");

    var startsBefore = runtimeCalls.Count(call => call.StartsWith("POST /api/v1.0/installations/", StringComparison.Ordinal));
    runtimePlan = new RuntimePlanResult(null, ["runtime.target_unchanged"]);
    runtimeVm.SelectedRelease = runtimeVm.Releases.Single(option => option.Release.ReleaseId == "rel-1143");
    await runtimeVm.ActCommand.ExecuteAsync(null);
    Check(runtimeCalls.Count(call => call.StartsWith("POST /api/v1.0/installations/", StringComparison.Ordinal)) == startsBefore
        && runtimeVm.PlanText.Resolve() == LocalizedText.Get("proxy_server.runtime_problem.target_unchanged")
        && runtimeVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime.plan_blocked"),
        "A rejected plan shows the host problem and never starts an installation");
    // The refused plan carries the measurement, so the page names the fact instead of only a code.
    runtimePlan = new RuntimePlanResult(null, ["runtime.dependencies_unsatisfied"], new RuntimeDependencyReport([
        new(RuntimeDependencyKind.DiskSpace, "protected-root", RuntimeDependencyState.Missing, 64L << 20,
            RequiredBytes: 320L << 20)]));
    await runtimeVm.ActCommand.ExecuteAsync(null);
    Check(runtimeVm.HasDependencyBlock && runtimeVm.DependencyText.Resolve() == string.Format(
            LocalizedText.Get("proxy_server.runtime.dependency.disk_space"), "64 MiB", "320 MiB")
        && runtimeVm.PlanText.Resolve() == LocalizedText.Get("proxy_server.runtime_problem.dependencies_unsatisfied"),
        "A refused plan names the unmet prerequisite and both sides of the size comparison");
    // Removal drops the default retention only after an explicit confirmation and names no release.
    runtimePlan = new RuntimePlanResult(new RuntimeChangePlan(new RuntimePlanRequest(runtimeInstallation.InstallationId,
        runtimeInstallation.HostId, RuntimeChange.Uninstall, runtimeInstallation.StateRevision, 0, 0, null, "idem", false, false),
        "sing-box", null, "rel-1143"), []);
    var confirmations = new List<string>();
    runtimeVm.ConfirmAsync = (title, message) => { confirmations.Add(title.Resolve()); return Task.FromResult(true); };
    await runtimeVm.UninstallDeleteDataCommand.ExecuteAsync(null);
    Check(confirmations.Count == 2 && confirmations[1] == LocalizedText.Get("component_center.preview_title") && runtimePlanBody.Contains("\"change\":\"uninstall\"", StringComparison.Ordinal)
        && runtimePlanBody.Contains("\"keepData\":false", StringComparison.Ordinal)
        && runtimePlanBody.Contains("\"releaseId\":null", StringComparison.Ordinal)
        && runtimeCalls.Any(call => call.Equals("POST /api/v1.0/installations/SingBox/Uninstall", StringComparison.OrdinalIgnoreCase))
        && runtimeVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime.uninstalled"),
        "Uninstall confirms data deletion and the frozen plan, names no release and starts removal");

    var removalsBefore = runtimeCalls.Count(call => call.Contains("/Uninstall", StringComparison.OrdinalIgnoreCase));
    runtimeVm.ConfirmAsync = null;
    await runtimeVm.UninstallKeepDataCommand.ExecuteAsync(null);
    Check(runtimeCalls.Count(call => call.Contains("/Uninstall", StringComparison.OrdinalIgnoreCase)) == removalsBefore,
        "A missing confirmation host never turns into a silent removal");

    // A refresh that fails keeps the cached list and says it failed; it never reads as "already the newest".
    var releasesBefore = runtimeVm.Releases.Count;
    await runtimeVm.RefreshCatalogCommand.ExecuteAsync(null);
    Check(runtimeVm.Releases.Count == releasesBefore
        && runtimeVm.CatalogProblem.Resolve() == LocalizedText.Get("proxy_server.runtime.refresh_failed")
        && runtimeVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime_problem.catalog_missing"),
        "A failed catalog refresh keeps the cached list and never reports the catalog as current");
    using (var updates = new ComponentCenterViewModel(runtimeVm, new ComponentDraftStore(), "updates-host", Guid.NewGuid()))
    {
        updates.Navigate(ComponentCenterPage.Updates);
        Check(!updates.HasResults && !runtimeVm.CanDetermineUpdates
            && updates.EmptyMessage.Resolve() == LocalizedText.Get("component_center.updates_unknown"),
            "Failed refresh withdraws cached update offers and shows unknown rather than no updates");
    }
    runtimeRefreshFails = false;
    await runtimeVm.RefreshCatalogCommand.ExecuteAsync(null);
    Check(runtimeVm.CatalogProblem.Resolve() == LocalizedText.Get("proxy_server.runtime.catalog_fresh_hint"),
        "A successful refresh clears the failure hint");
    Check(runtimeVm.CanDetermineUpdates && runtimeVm.HasUpdates,
        "Successful fresh catalog observation restores confirmed update offers");
    var freshCatalog = runtimeCatalog;
    runtimeCatalog = runtimeCatalog with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
    await runtimeVm.LoadAsync();
    using (var updates = new ComponentCenterViewModel(runtimeVm, new ComponentDraftStore(), "updates-host", Guid.NewGuid()))
    {
        updates.Navigate(ComponentCenterPage.Updates);
        Check(!updates.HasResults && updates.EmptyMessage.Resolve() == LocalizedText.Get("component_center.updates_unknown"),
            "Expired catalog cannot establish that no updates exist");
    }
    runtimeCatalog = freshCatalog;

    runtimeObservationFails = true;
    await runtimeVm.LoadAsync();
    Check(runtimeVm.Installation is null && !runtimeVm.CanAct && !runtimeVm.CanUninstall,
        "A failed installation observation clears authority and cannot offer install or uninstall from stale state");
    using (var updates = new ComponentCenterViewModel(runtimeVm, new ComponentDraftStore(), "updates-host", Guid.NewGuid()))
    {
        updates.Navigate(ComponentCenterPage.Updates);
        Check(!updates.HasResults && updates.EmptyMessage.Resolve() == LocalizedText.Get("component_center.observation_unknown"),
            "Missing installation facts are distinguished from an observed installation with no updates");
    }
    runtimeObservationFails = false;
    await runtimeVm.LoadAsync();
    var managedInstallation = runtimeInstallation;
    foreach (var ownership in new[] { RuntimeOwnership.Absent, RuntimeOwnership.External, RuntimeOwnership.Unknown })
    {
        runtimeInstallation = managedInstallation with { Ownership = ownership };
        await runtimeVm.LoadAsync();
        using var updates = new ComponentCenterViewModel(runtimeVm, new ComponentDraftStore(), "updates-host", Guid.NewGuid());
        updates.Navigate(ComponentCenterPage.Updates);
        Check(!updates.HasResults && updates.EmptyMessage.Resolve() == LocalizedText.Get(ownership == RuntimeOwnership.Unknown
                ? "component_center.observation_unknown" : "component_center.updates_requires_managed"),
            "Update checks distinguish ownership without claiming latest: " + ownership);
    }
    runtimeInstallation = managedInstallation;
    await runtimeVm.LoadAsync();
    runtimeActiveReleaseMissing = true;
    using (var missingSourceVm = new ProxyServerRuntimeViewModel(catalogClient))
    {
        missingSourceVm.CanRead = true;
        missingSourceVm.CanManage = true;
        await missingSourceVm.LoadAsync();
        Check(missingSourceVm.HasSources && missingSourceVm.SelectedSource is null && !missingSourceVm.CanAct
            && missingSourceVm.Releases.Count == 0,
            "An installed component whose active release is absent never defaults to another source or target");
    }
    runtimeActiveReleaseMissing = false;
    // An interrupted transaction is a recovery state: the page states it and stops offering lifecycle actions.
    runtimeNeedsRecovery = true;
    await runtimeVm.LoadAsync();
    Check(runtimeVm.NeedsRecovery && !runtimeVm.CanAct && !runtimeVm.CanUninstall
        && runtimeVm.RecoveryText.Resolve() == LocalizedText.Get("proxy_server.runtime.recovery_required"),
        "A pending recovery is shown as its own state and blocks install, update and removal");
    Check(runtimeVm.RecoveryOperationText.Resolve() == runtimePendingId.ToString("D")
        && runtimeVm.RecoveryChangeText.Resolve() == LocalizedText.Get("proxy_server.runtime.change.Update")
        && runtimeVm.RecoveryProblem.Resolve() == LocalizedText.Get("proxy_server.runtime_problem.compensation_health_failed")
        && runtimeVm.RecoveryServiceText.Resolve() == LocalizedText.Get("proxy_server.runtime.recovery_service.Running"),
        "The recovery panel names the transaction, its change, the compensation failure and the live service");
    Check(runtimeVm.CanRetryRecovery && runtimeVm.CanEmergencyStop && !runtimeVm.CanCloseRecovery,
        "A running service offers retry and the emergency stop, never a close");

    await runtimeVm.RetryRecoveryCommand.ExecuteAsync(null);
    Check(runtimeCalls.Contains("POST /api/v1.0/managed-runtimes/sing-box/recovery/retry")
        && runtimeRecoveryBody.Contains(runtimePendingId.ToString("D"), StringComparison.Ordinal)
        && runtimeVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime_problem.recovery_required"),
        "Retrying names the exact transaction and reports the Host's repeated recovery requirement");

    // The Server answers elevation-required once: the page asks for the password and retries exactly once.
    runtimeVm.PasswordPromptAsync = () => { runtimePrompts++; return Task.FromResult<string?>("secret"); };
    await runtimeVm.EmergencyStopCommand.ExecuteAsync(null);
    Check(runtimeElevations == 1 && runtimePrompts == 1
        && runtimeCalls.FindLastIndex(call => call == "POST /api/v1.0/managed-runtimes/sing-box/recovery/stop") > 0
        && runtimeVm.RecoveryServiceText.Resolve() == LocalizedText.Get("proxy_server.runtime.recovery_service.Stopped")
        && runtimeVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime.recovery_stopped"),
        "The emergency stop asks for elevation once, then reports the observed stopped service");
    Check(runtimeVm.CanCloseRecovery && !runtimeVm.CanEmergencyStop,
        "A stopped service makes closing available and withdraws the stop");

    confirmations.Clear();
    // Closing needs its own confirmation: a missing confirmation host must not close a transaction.
    var closesBefore = runtimeCalls.Count(call => call.EndsWith("/recovery/close", StringComparison.Ordinal));
    runtimeVm.ConfirmAsync = null;
    await runtimeVm.CloseRecoveryCommand.ExecuteAsync(null);
    Check(runtimeCalls.Count(call => call.EndsWith("/recovery/close", StringComparison.Ordinal)) == closesBefore,
        "A missing confirmation host never turns into a silent close");

    runtimeVm.ConfirmAsync = (title, message) => { confirmations.Add(title.Resolve()); return Task.FromResult(true); };
    await runtimeVm.CloseRecoveryCommand.ExecuteAsync(null);
    Check(confirmations.Count == 1
        && runtimeCalls.Count(call => call.EndsWith("/recovery/close", StringComparison.Ordinal)) == closesBefore + 1
        && runtimeRecoveryBody.Contains("\"confirmed\":true", StringComparison.Ordinal)
        && runtimeRecoveryBody.Contains(runtimePendingId.ToString("D"), StringComparison.Ordinal)
        && runtimeVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime.recovery_closed"),
        "Closing confirms first and carries the transaction and the acknowledgement to the Server");

    // Cancellation is a request, not an assumption: the page offers it only while the Host still allows the
    // transaction to be abandoned, keeps polling, and reports the Host's own Cancelled outcome.
    runtimePlan = new RuntimePlanResult(new RuntimeChangePlan(new RuntimePlanRequest(runtimeInstallation.InstallationId,
        runtimeInstallation.HostId, RuntimeChange.Update, runtimeInstallation.StateRevision, runtimeCatalog.Revision, 7,
        "rel-1160", "idem", false), "sing-box", runtimeCatalog.Releases[0], "rel-1143"), []);
    var downloadOperation = Guid.NewGuid();
    var downloadState = InstallationOperationState.Running;
    var downloadCancellable = true;
    var cancelKey = "";
    using (var cancelHttp = new HttpClient(new Handler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/managed-runtimes/sources", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new[] { runtimeSource, runtimeSource with { RuntimeId = "mihomo" } });
        if (path.EndsWith("/catalog", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new RuntimeCatalogSnapshot(runtimeSource, runtimeCatalog with
            { Releases = runtimeCatalog.Releases.Append(runtimeCatalog.Releases[0] with { RuntimeId = "mihomo", ReleaseId = "other-runtime-release" }).ToArray() }, true, null));
        if (path.EndsWith("/sing-box/installation", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new RuntimeInstallationSnapshot(runtimeInstallation, "1.14.3", null, null, null, null, null, null, runtimeInstalled));
        if (path.EndsWith("/sing-box/plan", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, runtimePlan);
        if (path.EndsWith("/sing-box/recovery", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new RuntimeRecoverySnapshot(false, null, null, null, false, null, null, null, null));
        if (path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            cancelKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? string.Join("", keys) : "";
            downloadState = InstallationOperationState.Cancelled;
            downloadCancellable = false;
            return Json(HttpStatusCode.OK, Operation(InstallationOperationState.Running, true, null));
        }
        if (!path.Contains("/installations/", StringComparison.Ordinal))
            return Json(HttpStatusCode.NotFound, new { code = "runtime.source_not_found" });
        if (request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, Operation(InstallationOperationState.Queued, true, null));
        return Json(HttpStatusCode.OK, Operation(downloadState, downloadCancellable,
            downloadState == InstallationOperationState.Cancelled ? "runtime.operation_cancelled" : null));

        InstallationOperationDto Operation(InstallationOperationState state, bool cancellable, string? problem) =>
            new(downloadOperation, InstallationServiceId.SingBox, InstallationOperationKind.Upgrade, state,
                state == InstallationOperationState.Cancelled ? InstallationStage.Cancelled : InstallationStage.Preparing,
                null, problem, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, cancellable);
    })))
    {
        var cancelClient = new SingBoxRuntimeCatalogClient(cancelHttp, session);
        using var cancelVm = new ProxyServerRuntimeViewModel(cancelClient) { CanRead = true };
        cancelVm.ConfirmAsync = (_, _) => Task.FromResult(true);
        await cancelVm.LoadAsync();
        cancelVm.CanManage = true;
        cancelVm.SelectedRelease = cancelVm.Releases.FirstOrDefault(option => option.Available);
        var running = cancelVm.ActCommand.ExecuteAsync(null);
        for (var attempt = 0; attempt < 200 && !cancelVm.CanCancelDownload; attempt++) await Task.Delay(10);
        Check(cancelVm.CanCancelDownload,
            "The page offers cancellation only while the Host reports the running operation as cancellable");
        await cancelVm.CancelDownloadCommand.ExecuteAsync(null);
        Check(cancelKey.Length > 0 && !cancelVm.CanCancelDownload,
            "Cancelling names the exact operation with an idempotency key and withdraws the offer at once");
        await running;
        Check(cancelVm.OutcomeText.Resolve() == LocalizedText.Get("proxy_server.runtime.operation_cancelled"),
            "A cancellation the Host performed reads as cancelled, never as a failed installation");
    }
}

sealed class BlockingTransferHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new();
    public bool Cancelled { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Started.SetResult();
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
        throw new InvalidOperationException();
    }
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
public class SessionStub : DispatchProxy
{
    private static readonly RelaxKonOS.Protocol.Workspace.SessionDto SessionToken = new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, RelaxKonOS.Protocol.Workspace.SessionStatus.Active,
        Guid.NewGuid(), "password", DateTimeOffset.UtcNow);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_State" => AuthSessionState.Authenticated,
        "get_ServiceId" => "http://localhost",
        "get_EffectiveBaseUrl" => "http://localhost/",
        "get_CurrentSession" => SessionToken,
        "GetAccessTokenAsync" => Task.FromResult<string?>("test"),
        _ => throw new NotSupportedException(method?.Name)
    };
}
public class SettingsStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetAsync" or "SaveAsync" => Task.FromResult<AppSettingsDocumentDto?>(null),
        _ => throw new NotSupportedException(method?.Name)
    };
}
