using System.Reflection;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ComponentRefreshChecks
{
    public static async Task RunAsync()
    {
        foreach (var platform in new[] { "linux", "windows" })
        {
            using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>()) { CanRead = true };
            var lifecycle = new Probe(platform);
            using var model = new ComponentCenterViewModel(runtime, new(), "host", Guid.NewGuid(), lifecycle: lifecycle, hostPlatform: platform);
            var selection = model.SelectedNode;
            await model.RefreshComponentStatesAsync();
            Check(lifecycle.Reads.SequenceEqual(ComponentCatalog.Components.Where(component => component.Platforms.Contains(platform)).Select(component => component.ComponentId)), "Refresh reads every supported component on " + platform);
            Check(ReferenceEquals(selection, model.SelectedNode) && lifecycle.Plans == 0, "Refresh preserves selection and does not prepare installation plans");
            var docker = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).FirstOrDefault(node => node.ComponentId == "docker");
            Check(platform != "linux" || docker?.Version == "1.2.3", "A failed component query does not prevent later components refreshing");
            if (platform == "linux")
            {
                Check(model.IsSystemPackage && !model.HasRuntimeDetails && model.ShowPackageFreeze, "Linux sing-box uses the package panel instead of the signed archive panel");
                model.SelectedNode = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "mihomo");
                Check(model.IsSystemPackage && !model.HasRuntimeDetails, "Linux mihomo uses the configured host package source");
                model.SelectedNode = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "squid");
                Check(model.ShowRegisterPackage && !model.HasExternalPackageChange && !model.CanRegisterPackage
                    && model.RegisterPackageText.Resolve() == LocalizedText.Get("component_center.package.register"),
                    "A verified external package offers explicit registration but no change acknowledgement");
                model.SelectedNode = model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Single(node => node.ComponentId == "samba");
                Check(model.HasExternalPackageChange && model.ShowRegisterPackage
                    && model.ExternalPackageChangeText.Resolve() == LocalizedStatus.Format("component_center.package.external_change", "1.2.3", "1.2.4").Resolve()
                    && model.RegisterPackageText.Resolve() == LocalizedText.Get("component_center.package.acknowledge"),
                    "A managed package verified after an external update reports the change and offers acknowledgement");
            }
            lifecycle.Denied = true;
            await model.RefreshComponentStatesAsync();
            Check(model.VisibleRoots.SelectMany(root => root.DescendantsAndSelf()).Where(node => node.ComponentId != null).All(node => node.Version != "1.2.3"), "Failed refresh removes stale versions");
        }
    }
    private sealed class Probe(string platform) : IComponentLifecycleClient
    {
        public readonly List<string> Reads = [];
        public int Plans;
        public bool Denied;
        public Task<ComponentStateView> StateAsync(string id, CancellationToken ct)
        {
            Reads.Add(id);
            if (Denied || id == "nginx") throw new HttpRequestException("offline");
            var target = new RuntimeTarget(platform, "x64", null, null, RuntimeInstallMode.SystemPackage, "systemd");
            var verified = new ComponentInstalledPackage(id, "1.2.3", "amd64", new('a', 64));
            var managed = new ComponentStateView(id, new(id, "host", 1, RuntimeOwnership.Managed, ComponentSourceMode.SystemPackage,
                target, "1.2.3", false), [], [], InstalledPackage: verified);
            return Task.FromResult(id switch
            {
                "squid" => managed with { State = managed.State! with { Ownership = RuntimeOwnership.External } },
                "samba" => managed with
                {
                    State = managed.State! with { ActiveVersion = "1.2.4" },
                    InstalledPackage = verified with { Version = "1.2.4" },
                    ExternalPackageChange = true, RecordedPackageVersion = "1.2.3"
                },
                _ => managed
            });
        }
        public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
        { Plans++; throw new InvalidOperationException("Refresh must not plan"); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
