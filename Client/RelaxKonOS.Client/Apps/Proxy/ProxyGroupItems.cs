using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.Client.Apps.Proxy;

/// <summary>UI-only projection of a controller proxy group. It keeps visual state out of the public API contract.</summary>
public sealed partial class ProxyGroupItem : ObservableObject
{
    public ProxyGroupItem(string name, string type, string? selected, IEnumerable<string> proxies, bool isExpanded)
    {
        Name = name;
        Type = type;
        IsExpanded = isExpanded;
        Nodes = new ObservableCollection<ProxyNodeItem>(proxies.Select(proxy => new ProxyNodeItem(name, proxy, string.Equals(proxy, selected, StringComparison.Ordinal))));
    }

    public string Name { get; }
    public string Type { get; }
    public ObservableCollection<ProxyNodeItem> Nodes { get; }
    public int NodeCount => Nodes.Count;

    [ObservableProperty] private bool _isExpanded;

    public string? Selected => Nodes.FirstOrDefault(node => node.IsSelected)?.Name;

    public void SetSelected(string name)
    {
        foreach (var node in Nodes) node.IsSelected = string.Equals(node.Name, name, StringComparison.Ordinal);
        OnPropertyChanged(nameof(Selected));
    }

    public void SortNodes(ProxyNodeSortMode sortMode)
    {
        var ordered = sortMode switch
        {
            ProxyNodeSortMode.Name => Nodes.OrderBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase),
            ProxyNodeSortMode.Delay => Nodes.OrderBy(node => node.DelayMilliseconds is null).ThenBy(node => node.DelayMilliseconds).ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => Nodes.OrderBy(node => node.DefaultIndex),
        };
        var nodes = ordered.ToArray();
        Nodes.Clear();
        foreach (var node in nodes) Nodes.Add(node);
    }
}

public sealed partial class ProxyNodeItem : ObservableObject
{
    private static int nextDefaultIndex;

    public ProxyNodeItem(string groupName, string name, bool isSelected)
    {
        GroupName = groupName;
        Name = name;
        IsSelected = isSelected;
        DefaultIndex = Interlocked.Increment(ref nextDefaultIndex);
    }

    public string GroupName { get; }
    public string Name { get; }
    public int DefaultIndex { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int? _delayMilliseconds;
    [ObservableProperty] private bool _isTimeout;
    [ObservableProperty] private bool _isTesting;

    public string DelayText => IsTesting ? "…" : IsTimeout ? "timeout" : DelayMilliseconds is { } delay ? delay + " ms" : "—";
    public bool HasFastDelay => DelayMilliseconds is > 0 and < 200 && !IsTesting;
    public bool HasSlowDelay => DelayMilliseconds is >= 200 && !IsTesting;
    public bool HasTimeout => IsTimeout && !IsTesting;
    public bool HasNoDelay => !HasFastDelay && !HasSlowDelay && !HasTimeout;

    public void SetDelay(int? milliseconds, bool timedOut)
    {
        DelayMilliseconds = milliseconds;
        IsTimeout = timedOut;
        RaiseDelayProperties();
    }

    partial void OnDelayMillisecondsChanged(int? value) => RaiseDelayProperties();
    partial void OnIsTimeoutChanged(bool value) => RaiseDelayProperties();
    partial void OnIsTestingChanged(bool value) => RaiseDelayProperties();

    private void RaiseDelayProperties()
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(HasFastDelay));
        OnPropertyChanged(nameof(HasSlowDelay));
        OnPropertyChanged(nameof(HasTimeout));
        OnPropertyChanged(nameof(HasNoDelay));
    }
}

public enum ProxyNodeSortMode { Default, Name, Delay }
