using System.Collections.ObjectModel;
using System.Text.Json;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Windows-style, on-demand registry browser. Values are fetched only for the selected key.</summary>
public sealed partial class RegistryViewModel(IRegistryClient client) : LocalizedObservableObject
{
    private long _loadGeneration;
    public ObservableCollection<RegistryKeyNode> Keys { get; } = [];
    public ObservableCollection<RegistryEntryRow> Entries { get; } = [];
    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.RefWithFallback("registry.status.loading", "Loading registry…");
    [ObservableProperty] private string _navigationPathInput = "HKEY_USERS";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private RegistryKeyNode? _selectedKey;
    [ObservableProperty] private RegistryEntryRow? _selectedEntry;
    [ObservableProperty] private string _path = "Workspace";
    [ObservableProperty] private string _name = "(Default)";
    [ObservableProperty] private RegistryScope _scope = RegistryScope.Workspace;
    [ObservableProperty] private RegistryValueType _valueType = RegistryValueType.Json;
    [ObservableProperty] private string _valueText = "{}";

    public Func<RegistryEntryRow, Task>? ShowEditDialogAsync { get; set; }
    public Func<RegistryScope, string, Task>? ShowNewValueDialogAsync { get; set; }
    public Func<RegistryScope, string, Task>? ShowNewKeyDialogAsync { get; set; }
    public bool CanCreateKey => SelectedKey?.CanCreateChildKey == true;
    public bool CanDeleteKey => SelectedKey?.CanManageKey == true;

    [RelayCommand]
    public Task RefreshAsync()
    {
        BuildStaticTree();
        return SelectedKey is { } key ? LoadValuesAsync(key, refreshChildren: true) : Task.CompletedTask;
    }

    partial void OnSelectedKeyChanged(RegistryKeyNode? value)
    {
        NavigationPathInput = DisplayPath(value);
        OnPropertyChanged(nameof(CanCreateKey));
        OnPropertyChanged(nameof(CanDeleteKey));
        if (value?.Kind == RegistryKeyKind.OtherUsers) _ = LoadChildrenAsync(value);
        _ = LoadValuesAsync(value);
    }

    partial void OnSelectedEntryChanged(RegistryEntryRow? value)
    {
        if (value is null) return;
        Scope = value.Source.Scope;
        Path = value.Source.Path;
        Name = value.Source.Name;
        ValueType = value.Source.ValueType;
        ValueText = value.Source.DesiredValue.GetRawText();
    }

    public async Task LoadChildrenAsync(RegistryKeyNode? key)
    {
        if (key is { Kind: RegistryKeyKind.OtherUsers, ChildrenLoaded: false, IsLoadingChildren: false })
        {
            key.IsLoadingChildren = true;
            try
            {
                var users = await client.ListUsersAsync();
                key.Children.Clear();
                foreach (var user in users)
                {
                    var owner = new RegistryKeyNode(user.Username, null, null, key, RegistryKeyKind.OtherUser, user.Id);
                    owner.Children.Add(new RegistryKeyNode("Workspace", "Workspace", RegistryScope.Workspace, owner));
                    key.Children.Add(owner);
                }
                key.ChildrenLoaded = true;
            }
            catch (Exception error) { StatusText = LocalizedText.RefWithFallback("registry.status.unavailable", "Registry unavailable: {0}", error.Message); }
            finally { key.IsLoadingChildren = false; }
            return;
        }
        if (key is not { IsRegistryKey: true, Scope: { } scope, Path: { } path } || key.ChildrenLoaded || key.IsLoadingChildren)
            return;
        key.IsLoadingChildren = true;
        try
        {
            var children = await client.ForUser(key.TargetUserId).ListKeysAsync(scope, path);
            key.Children.Clear();
            foreach (var child in children)
                key.Children.Add(new RegistryKeyNode(LastSegment(child.Path), child.Path, child.Scope, key));
            key.ChildrenLoaded = true;
        }
        catch (Exception ex) { StatusText = LocalizedText.RefWithFallback("registry.status.unavailable", "Registry unavailable: {0}", ex.Message); }
        finally { key.IsLoadingChildren = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            using var document = JsonDocument.Parse(ValueText);
            var baseline = SelectedEntry?.Source;
            var saved = await client.ForUser(SelectedKey?.TargetUserId).SaveAsync(new PutRegistryEntryRequest(Scope, Path, Name, ValueType, document.RootElement.Clone(),
                baseline is not null && baseline.Scope == Scope && baseline.Path == Path && baseline.Name == Name ? baseline.Revision : 0));
            ApplySaved(saved);
            StatusText = LocalizedText.RefWithFallback("registry.status.saved", "Value saved.");
        }
        catch (Exception ex) { StatusText = LocalizedText.RefWithFallback("registry.status.save_failed", "Could not save value: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is null) return;
        try
        {
            await client.ForUser(SelectedKey?.TargetUserId).DeleteAsync(SelectedEntry.Source.Scope, SelectedEntry.Source.Path, SelectedEntry.Source.Name);
            Entries.Remove(SelectedEntry);
            SelectedEntry = null;
            StatusText = LocalizedText.RefWithFallback("registry.status.deleted", "Value deleted.");
        }
        catch (Exception ex) { StatusText = LocalizedText.RefWithFallback("registry.status.delete_failed", "Could not delete value: {0}", ex.Message); }
    }

    [RelayCommand]
    private Task NewValue() => SelectedKey is { IsRegistryKey: true, Scope: { } scope, Path: { } path } && ShowNewValueDialogAsync is not null
        ? ShowNewValueDialogAsync(scope, path) : Task.CompletedTask;

    [RelayCommand]
    private Task NewKey() => SelectedKey is { CanCreateChildKey: true, Scope: { } scope, Path: { } path } && ShowNewKeyDialogAsync is not null
        ? ShowNewKeyDialogAsync(scope, path) : Task.CompletedTask;

    [RelayCommand]
    private async Task DeleteKeyAsync()
    {
        if (SelectedKey is not { CanManageKey: true, Scope: { } scope, Path: { } path } key) return;
        try
        {
            await client.ForUser(key.TargetUserId).DeleteKeyAsync(scope, path);
            var parent = key.Parent;
            parent?.Children.Remove(key);
            SelectedKey = parent;
            StatusText = LocalizedText.RefWithFallback("registry.status.deleted", "Value deleted.");
        }
        catch (Exception ex) { StatusText = LocalizedText.RefWithFallback("registry.status.delete_failed", "Could not delete value: {0}", ex.Message); }
    }

    [RelayCommand]
    private Task EditSelectedAsync() => SelectedEntry is not null && ShowEditDialogAsync is not null ? ShowEditDialogAsync(SelectedEntry) : Task.CompletedTask;

    public void ApplyCreatedKey(RegistryKeyDto created)
    {
        if (SelectedKey is { } parent && parent.Scope == created.Scope && string.Equals(parent.Path, ParentPath(created.Path), StringComparison.Ordinal))
        {
            parent.Children.Add(new RegistryKeyNode(LastSegment(created.Path), created.Path, created.Scope, parent));
            parent.ChildrenLoaded = true;
            parent.IsExpanded = true;
        }
        StatusText = LocalizedText.RefWithFallback("registry.status.saved", "Value saved.");
    }

    public void ApplySaved(RegistryEntryDto entry)
    {
        if (SelectedKey?.Scope != entry.Scope || !string.Equals(SelectedKey.Path, entry.Path, StringComparison.Ordinal)) return;
        var index = Entries.ToList().FindIndex(x => x.Source.Name == entry.Name);
        if (index >= 0) Entries[index] = RegistryEntryRow.From(entry);
        else Entries.Add(RegistryEntryRow.From(entry));
        _ = LoadValuesAsync(SelectedKey);
    }

    [RelayCommand]
    private async Task NavigateAsync()
    {
        var input = NavigationPathInput.Trim().Trim('\\');
        const string root = "HKEY_USERS";
        if (input.StartsWith(root, StringComparison.OrdinalIgnoreCase)) input = input[root.Length..].Trim('\\');
        if (input.StartsWith("Current User\\", StringComparison.OrdinalIgnoreCase)) input = input["Current User\\".Length..];
        RegistryKeyNode? current;
        if (input.StartsWith("Other Users\\", StringComparison.OrdinalIgnoreCase))
        {
            var other = Keys.SingleOrDefault()?.Children.SingleOrDefault(node => node.Kind == RegistryKeyKind.OtherUsers);
            await LoadChildrenAsync(other);
            input = input["Other Users\\".Length..];
            var split = input.Split('\\', 2);
            current = other?.Children.FirstOrDefault(node => node.Name.Equals(split[0], StringComparison.OrdinalIgnoreCase)
                || node.TargetUserId?.ToString("D") == split[0]);
            input = split.Length > 1 ? split[1] : "";
        }
        else current = Keys.SingleOrDefault()?.Children.SingleOrDefault(node => node.Kind == RegistryKeyKind.CurrentUser);
        if (string.IsNullOrWhiteSpace(input)) { SelectedKey = current ?? Keys.SingleOrDefault(); return; }
        foreach (var segment in input.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null) break;
            await LoadChildrenAsync(current);
            current = current.Children.FirstOrDefault(x => x.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
        }
        if (current is null) StatusText = LocalizedText.RefWithFallback("registry.status.path_not_found", "Registry key not found: {0}", NavigationPathInput);
        else SelectedKey = current;
    }

    private async Task LoadValuesAsync(RegistryKeyNode? key, bool refreshChildren = false)
    {
        var generation = ++_loadGeneration;
        Entries.Clear();
        SelectedEntry = null;
        if (key is null || !key.IsRegistryKey || key.Scope is not { } scope || key.Path is not { } path)
        {
            StatusText = LocalizedText.RefWithFallback("registry.status.empty", "No values in this key.");
            return;
        }
        IsLoading = true;
        try
        {
            if (refreshChildren) key.ChildrenLoaded = false;
            await LoadChildrenAsync(key);
            var values = await client.ForUser(key.TargetUserId).ListValuesAsync(scope, path);
            if (generation != _loadGeneration || !ReferenceEquals(SelectedKey, key)) return;
            foreach (var entry in values) Entries.Add(RegistryEntryRow.From(entry));
            StatusText = values.Count == 0 ? LocalizedText.RefWithFallback("registry.status.empty", "No values in this key.") : LocalizedText.RefWithFallback("registry.status.count", "{0} value(s).", values.Count);
        }
        catch (Exception ex) { StatusText = LocalizedText.RefWithFallback("registry.status.unavailable", "Registry unavailable: {0}", ex.Message); }
        finally { if (generation == _loadGeneration) IsLoading = false; }
    }

    private void BuildStaticTree()
    {
        if (Keys.Count != 0) return;
        var root = new RegistryKeyNode("HKEY_USERS", null, null, null, RegistryKeyKind.Root);
        var current = new RegistryKeyNode(LocalizedText.Get("registry.tree.current_user", "Current User"), null, null, root, RegistryKeyKind.CurrentUser);
        current.Children.Add(new RegistryKeyNode("Workspace", "Workspace", RegistryScope.Workspace, current));
        root.Children.Add(current);
        root.Children.Add(new RegistryKeyNode(LocalizedText.Get("registry.tree.other_users", "Other Users"), null, null, root, RegistryKeyKind.OtherUsers));
        Keys.Add(root);
        SelectedKey ??= current;
    }

    private static string ParentPath(string path) => path[..path.LastIndexOf('\\')];
    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];
    private static string DisplayPath(RegistryKeyNode? node) => node?.Kind switch
    {
        null or RegistryKeyKind.Root => "HKEY_USERS",
        RegistryKeyKind.CurrentUser => "HKEY_USERS\\Current User",
        RegistryKeyKind.OtherUsers => "HKEY_USERS\\Other Users",
        _ => node.TargetUserId is { } user ? "HKEY_USERS\\Other Users\\" + user.ToString("D") + (node.Path is null ? "" : "\\" + node.Path)
            : "HKEY_USERS\\Current User\\" + node.Path,
    };
}

public sealed record RegistryEntryRow(RegistryEntryDto Source, string Name, string DesiredValue, string Type)
{
    public static RegistryEntryRow From(RegistryEntryDto entry) => new(entry, entry.Name, Format(entry.DesiredValue), entry.ValueType.ToString());
    private static string Format(JsonElement value) => value.GetRawText() is { Length: > 240 } text ? text[..237] + "…" : value.GetRawText();
}

public enum RegistryKeyKind { Root, CurrentUser, OtherUsers, OtherUser, Key }

public sealed partial class RegistryKeyNode : ObservableObject
{
    public RegistryKeyNode(string name, string? path, RegistryScope? scope, RegistryKeyNode? parent, RegistryKeyKind kind = RegistryKeyKind.Key, Guid? targetUserId = null)
    { Name = name; Path = path; Scope = scope; Parent = parent; Kind = kind; TargetUserId = targetUserId ?? parent?.TargetUserId; }
    public Guid? TargetUserId { get; }
    public string Name { get; }
    public string? Path { get; }
    public RegistryScope? Scope { get; }
    public RegistryKeyNode? Parent { get; }
    public RegistryKeyKind Kind { get; }
    public bool IsRegistryKey => Kind == RegistryKeyKind.Key;
    public bool CanCreateChildKey => Scope == RegistryScope.Workspace && Path is not null;
    public bool CanManageKey => Scope == RegistryScope.Workspace && Path is not null && !string.Equals(Path, "Workspace", StringComparison.Ordinal);
    public ObservableCollection<RegistryKeyNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _childrenLoaded;
    [ObservableProperty] private bool _isLoadingChildren;
}
