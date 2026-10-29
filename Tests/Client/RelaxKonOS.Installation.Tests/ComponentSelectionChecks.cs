using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.ManagedRuntimes;

internal static class ComponentSelectionChecks
{
    public static void Run()
    {
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: Component selection: " + message); }
        var root = new ComponentTreeNode("network", LocalizedStatus.Literal("Network"));
        var first = new ComponentTreeNode("first", LocalizedStatus.Literal("First"), "first");
        var second = new ComponentTreeNode("second", LocalizedStatus.Literal("Second"), "second");
        root.Children.Add(first); root.Children.Add(second);
        var notifications = 0;
        foreach (var node in root.DescendantsAndSelf()) node.SelectionRequested = (_, _) => notifications++;
        root.SynchronizeSelection(new HashSet<string>());
        Check(root.IsChecked == false, "No local choices leave the group unchecked");
        root.SynchronizeSelection(new HashSet<string> { "first" });
        Check(root.IsChecked is null && first.IsChecked == true && second.IsChecked == false,
            "A partially selected group is indeterminate");
        root.SynchronizeSelection(new HashSet<string> { "first", "second" });
        Check(root.IsChecked == true && notifications == 0, "Full aggregation never generates a user intent or host action");
        first.Status = LocalizedStatus.Literal("Not installed");
        first.Version = "—";
        Check(first.IsChecked == true, "Installation facts do not define checkbox selection");
        first.IsChecked = false;
        Check(notifications == 1, "An explicit checkbox edit produces one local selection request");
        root.SynchronizeSelection(new HashSet<string> { "first", "second" });
        Check(root.Filter("FIRST", _ => true) && root.VisibleChildren.SequenceEqual(new[] { first })
            && second.IsChecked == true, "Search hides unmatched nodes without discarding their selected intents");
        Check(!root.Filter("missing", _ => true) && root.VisibleChildren.Count == 0, "Nonmatching trees are removed from results");
        root.Filter("", _ => true);
        Check(root.VisibleChildren.Count == 2 && second.IsChecked == true, "Clearing search restores the same nodes and selections");
        Check(RuntimeSemanticVersion.TryParse("1.9.9", out var old) && RuntimeSemanticVersion.TryParse("1.10.0", out var newer)
            && newer.CompareTo(old) > 0, "Update ordering uses strict numeric SemVer precedence");
        Check(RuntimeSemanticVersion.TryParse("1.10.0-rc.1", out var preview) && RuntimeSemanticVersion.TryParse("1.10.0", out var stable)
            && stable.CompareTo(preview) > 0 && !RuntimeSemanticVersion.TryParse("latest", out _),
            "Prerelease precedence and unknown version labels cannot be guessed as upgrades");
        foreach (var page in Enum.GetValues<ComponentCenterPage>())
        {
            var uri = ComponentCenterNavigation.Component("host-a", "relaxkonos.proxy-server", "sing-box", page);
            Check(ComponentCenterNavigation.TryParse(uri, out var target) && target!.Page == page,
                "Controlled links bind the exact page " + page);
        }
        foreach (var page in new[] { "Install", "1", "all", "Sources%20", "Tasks&execute=true" })
            Check(!ComponentCenterNavigation.TryParse(new Uri("relaxkonos://components/sing-box?hostId=host-a&appId=relaxkonos.proxy-server&page=" + page), out _),
                "Unknown or executable navigation is refused: " + page);
        Check(ComponentCenterNavigation.TryParse(ComponentCenterNavigation.Workload("host-a", "relaxkonos.proxy-server", "proxy-server"), out var workload)
            && workload is { Page: ComponentCenterPage.Workloads, WorkloadId: "proxy-server" },
            "A workload link only targets its catalog context");
    }
}
