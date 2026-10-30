using System.Reflection;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
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
            return Task.FromResult(new ComponentStateView(id, new(id, "host", 1, RuntimeOwnership.Managed, ComponentSourceMode.SystemPackage,
                new(platform, "x64", null, null, RuntimeInstallMode.SystemPackage, "systemd"), "1.2.3", false), [], []));
        }
        public Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
        { Plans++; throw new InvalidOperationException("Refresh must not plan"); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
