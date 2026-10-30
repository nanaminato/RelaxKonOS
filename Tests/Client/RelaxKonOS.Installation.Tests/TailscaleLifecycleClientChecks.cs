using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class TailscaleLifecycleClientChecks
{
    internal static async Task RunAsync(IAuthSession session, IAppSettingsClient settings)
    {
        var definition = ComponentCatalog.Find("tailscale")!;
        Check(definition.Integration == ComponentIntegration.Installation
            && definition.Sources.Any(source => source.Kind == ComponentSourceKind.NativeInstaller && source.Platforms.Contains("windows"))
            && definition.Sources.Any(source => source.Kind == ComponentSourceKind.SystemPackage && source.Platforms.Contains("linux")),
            "The desktop catalog declares actual Windows MSI and Linux package installation paths");
        using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>())
        { CanRead = true, CanManage = true };
        var lifecycle = new NativeLifecycle();
        using var model = new ComponentCenterViewModel(runtime, new(), "host", Guid.NewGuid(), lifecycle: lifecycle, hostPlatform: "windows");
        var sent = new List<TailscaleInstallationRequest>();
        using var http = new HttpClient(new Handler(message =>
        {
            var body = message.Content!.ReadFromJsonAsync<TailscaleInstallationRequest>().GetAwaiter().GetResult()!;
            sent.Add(body);
            Check(message.Headers.GetValues("Idempotency-Key").Single() == body.FrozenPackage!.Request.IdempotencyKey,
                "Native Tailscale submission preserves the exact frozen key");
            Check(body.FrozenPackage.Expected is { Mode: ComponentSourceMode.NativeInstaller, Installer.Format: "msi", Archive: null, Package: null },
                "Desktop sends an MSI expectation without routing it through archive or APT execution");
            return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { code = "offline" }) };
        }));
        using var task = new InstallationTaskViewModel(new InstallationClient(http, session), settings, InstallationServiceId.Tailscale,
            "relaxkonos.component-center", () => Task.CompletedTask, () => Task.FromResult<string?>(null));
        model.AttachPackageInstallation("tailscale", task);
        model.SelectedNode = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "tailscale");
        await model.RefreshPackageInstallationAsync("tailscale");
        await model.ResolvePackageCommand.ExecuteAsync(null);
        Check(model.IsNativeInstaller && model.ShowPackageFreeze && model.ShowPackageChangeActions && !model.ShowInstallOnlyNote,
            "Desktop MSI details offer installation, update and removal without an archive-only explanation");
        model.ConfirmPackageInstallAsync = _ => Task.FromResult(false);
        await model.InstallPackageCommand.ExecuteAsync(null);
        Check(sent.Count == 0, "Cancelling the native installer confirmation never submits an operation");
        model.ConfirmPackageInstallAsync = _ => Task.FromResult(true);
        await model.InstallPackageCommand.ExecuteAsync(null);
        Check(sent.Count == 1 && sent[0].FrozenPackage!.Request.Change == RuntimeChange.Install,
            "Confirmed desktop native installation reaches the installation service");
        lifecycle.State = lifecycle.State with { Ownership = RuntimeOwnership.Managed, ActiveVersion = "1.104.2" };
        await model.RefreshPackageInstallationAsync("tailscale");
        await model.UpdatePackageCommand.ExecuteAsync(null);
        Check(sent.Count == 2 && sent[1].FrozenPackage!.Request.Change == RuntimeChange.Update,
            "Desktop native update freezes a genuinely newer version");
        lifecycle.State = lifecycle.State with { ActiveVersion = "1.104.4" };
        await model.RefreshPackageInstallationAsync("tailscale");
        await model.UninstallPackageCommand.ExecuteAsync(null);
        Check(sent.Count == 3 && sent[2].FrozenPackage!.Request is { Change: RuntimeChange.Uninstall, KeepData: true },
            "Desktop native uninstall retains login and configuration data");
        lifecycle.State = lifecycle.State with { Ownership = RuntimeOwnership.External };
        await model.RefreshPackageInstallationAsync("tailscale");
        Check(!model.CanChangeInstalledPackage, "External Windows Tailscale is observed without an implicit update or removal grant");
    }
    private sealed class NativeLifecycle : IComponentLifecycleClient
    {
        internal ComponentState State = new("tailscale", "host", 7, RuntimeOwnership.Absent, ComponentSourceMode.NativeInstaller,
            new("windows", "x64", null, null, RuntimeInstallMode.NativeInstaller, "scm"), null, false);
        private readonly ComponentDownloadRelease release = new("1.104.4", "win-x64",
            "https://pkgs.tailscale.com/stable/tailscale-setup-1.104.4-amd64.msi", "tailscale-setup-1.104.4-amd64.msi", "msi", new('a', 64));
        public Task<ComponentStateView> StateAsync(string id, CancellationToken ct) => Task.FromResult(new ComponentStateView(id,
            State with { ComponentId = id }, [new(ComponentSourceMode.NativeInstaller, "windows", true, null, "msi")], []));
        public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct) =>
            Task.FromResult(TailscaleInstallerPlan.Evaluate(request, State, release));
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
