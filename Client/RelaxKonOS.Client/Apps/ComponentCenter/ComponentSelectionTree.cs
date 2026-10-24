using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public enum ComponentCenterPage { All, Installed, Updates, Workloads, Tasks, Sources }
public sealed record ComponentCenterNavigationItem(ComponentCenterPage Page)
{
    public LocalizedStatus Label => LocalizedStatus.Key("component_center.page." + Page);
}

/// <summary>Selection aggregates local draft intents, independently of host installation status.</summary>
public partial class ComponentTreeNode : ObservableObject
{
    private bool _synchronizing;
    public string Id { get; }
    public string? ComponentId { get; }
    public string? WorkloadId { get; }
    public LocalizedStatus Label { get; }
    public bool IsOptional { get; }
    public LocalizedStatus Requirement => LocalizedStatus.Key(IsOptional ? "component_center.optional" : "component_center.required");
    public bool ShowRequirement { get; }
    public ObservableCollection<ComponentTreeNode> Children { get; } = [];
    public ObservableCollection<ComponentTreeNode> VisibleChildren { get; } = [];
    public bool IsGroup => Children.Count != 0;
    public Action<ComponentTreeNode, bool>? SelectionRequested { get; set; }
    [ObservableProperty] private bool? isChecked = false;
    [ObservableProperty] private LocalizedStatus status = LocalizedStatus.Literal("");
    [ObservableProperty] private string version = "";
    [ObservableProperty] private bool canSelect = true;
    public ComponentTreeNode(string id, LocalizedStatus label, string? componentId = null,
        string? workloadId = null, bool optional = false, bool showRequirement = false)
    { Id = id; Label = label; ComponentId = componentId; WorkloadId = workloadId; IsOptional = optional; ShowRequirement = showRequirement; }
    partial void OnIsCheckedChanged(bool? value)
    {
        if (!_synchronizing) SelectionRequested?.Invoke(this, value != false);
    }
    public IEnumerable<ComponentTreeNode> DescendantsAndSelf() => new[] { this }.Concat(Children.SelectMany(child => child.DescendantsAndSelf()));
    public IEnumerable<ComponentTreeNode> VisibleDescendantsAndSelf() => new[] { this }.Concat(VisibleChildren.SelectMany(child => child.VisibleDescendantsAndSelf()));
    public void SynchronizeSelection(IReadOnlySet<string> selected)
    {
        foreach (var child in Children) child.SynchronizeSelection(selected);
        _synchronizing = true;
        IsChecked = ComponentId is { } id ? selected.Contains(id)
            : Children.Count == 0 || Children.All(child => child.IsChecked == false) ? false
            : Children.All(child => child.IsChecked == true) ? true : null;
        _synchronizing = false;
    }
    public bool Filter(string query, Func<ComponentTreeNode, bool> eligible, bool parentMatches = false)
    {
        var match = parentMatches || query.Length == 0 || Label.Resolve().Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Id.Contains(query, StringComparison.OrdinalIgnoreCase) || ComponentId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        var visible = Children.Where(child => child.Filter(query, eligible, match)).ToArray();
        if (!VisibleChildren.SequenceEqual(visible)) { VisibleChildren.Clear(); foreach (var child in visible) VisibleChildren.Add(child); }
        return ComponentId is not null ? match && eligible(this) : visible.Length > 0;
    }
}
