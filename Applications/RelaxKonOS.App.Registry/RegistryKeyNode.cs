using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.App.Registry;

public enum RegistryKeyKind { Root, CurrentUser, Users, User, Key, Json, Placeholder }

public sealed partial class RegistryKeyNode : ObservableObject
{
    public RegistryKeyNode(string name, string? path, RegistryScope? scope, RegistryKeyNode? parent, RegistryKeyKind kind = RegistryKeyKind.Key, Guid? targetUserId = null)
    {
        _name = name; Path = path; Scope = scope; Parent = parent; Kind = kind; TargetUserId = targetUserId ?? parent?.TargetUserId;
        if (kind is RegistryKeyKind.Key or RegistryKeyKind.Users)
            Children.Add(new RegistryKeyNode("…", null, null, this, RegistryKeyKind.Placeholder));
    }
    public IReadOnlyList<RegistryEntryDto>? CachedValues { get; set; }
    public RegistryEntryDto? Document { get; set; }
    public IReadOnlyList<string> JsonPath { get; init; } = [];
    public string NavigationName => Kind == RegistryKeyKind.Json
        ? JsonPath.Count == 0 ? Document!.Name : Parent?.JsonValueKind == System.Text.Json.JsonValueKind.Array ? "[" + JsonPath[^1] + "]" : JsonPath[^1]
        : (Path ?? Name)[((Path ?? Name).LastIndexOf('\\') + 1)..];
    public JsonValueKind? JsonValueKind => Document is null ? null : RegistryJsonProjection.Resolve(Document.DesiredValue, JsonPath).ValueKind;
    public string ToolTipText => Document is null ? Name : RegistryJsonProjection.Resolve(Document.DesiredValue, JsonPath).GetRawText();
    public void NotifyJsonChanged()
    {
        OnPropertyChanged(nameof(ToolTipText));
        OnPropertyChanged(nameof(JsonValueKind));
    }
    public Guid? TargetUserId { get; }
    [ObservableProperty] private string _name;
    public string? Path { get; }
    public RegistryScope? Scope { get; }
    public RegistryKeyNode? Parent { get; }
    public RegistryKeyKind Kind { get; }
    public bool IsRegistryKey => Kind == RegistryKeyKind.Key;
    /// <summary>Raw account name for the status bar, without the tree's duplicate/current-user markers.</summary>
    public string? UserName { get; set; }
    public RegistryAddress? Address { get; set; }
    public bool AllowCreate { get; set; }
    public bool AllowDelete { get; set; }
    public bool CanCreateChildKey => AllowCreate;
    public bool CanManageKey => AllowDelete;
    public ObservableCollection<RegistryKeyNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _childrenLoaded;
    [ObservableProperty] private bool _isLoadingChildren;
}
