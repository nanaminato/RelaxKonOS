using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Windows-style, on-demand registry browser. Values are fetched only for the selected key.</summary>
public sealed partial class RegistryViewModel(IRegistryClient client, RelaxKonOS.Client.Services.ClientPresentationPreferences? preferences = null) : LocalizedObservableObject
{
    public RegistryAppearanceSettings Appearance => preferences?.Registry ?? RegistryAppearanceSettings.Default;
    public void StartAppearanceUpdates()
    {
        if (preferences is null) return;
        preferences.Changed += OnAppearanceChanged;
        preferences.Activate();
    }
    public void StopAppearanceUpdates()
    {
        if (preferences is null) return;
        preferences.Changed -= OnAppearanceChanged;
        preferences.Deactivate();
    }
    private void OnAppearanceChanged(object? sender, EventArgs args) => OnPropertyChanged(nameof(Appearance));
    public Task SaveSidebarWidthAsync(int width) => preferences?.SaveSidebarWidthAsync(width) ?? Task.CompletedTask;
    private readonly Dictionary<RegistryKeyNode, Task> _childLoads = [];
    private long _loadGeneration;
    private long _navigationGeneration;
    private RegistryContextDto? _context;
    private readonly List<RegistryKeyNode> _history = [];
    private int _historyIndex = -1;
    private bool _movingHistory;

    public ObservableCollection<RegistryKeyNode> Keys { get; } = [];
    public ObservableCollection<RegistryEntryRow> Entries { get; } = [];

    [ObservableProperty] private LocalizedStatus _statusText = LocalizedText.RefWithFallback("registry.status.loading", "Loading registry…");
    [ObservableProperty] private string _navigationPathInput = "HKEY_USERS";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private RegistryKeyNode? _selectedKey;
    [ObservableProperty] private RegistryEntryRow? _selectedEntry;
    [ObservableProperty] private RegistryPaneState _paneState = RegistryPaneState.Idle;

    public Func<RegistryEntryRow, Task>? ShowEditDialogAsync { get; set; }
    public Func<RegistryEntryRow, Task>? ShowRenameDialogAsync { get; set; }
    public Func<RegistryScope, string, Task>? ShowNewValueDialogAsync { get; set; }
    public Func<RegistryScope, string, Task>? ShowNewKeyDialogAsync { get; set; }

    /// <summary>Server, signed-in account, target owner and scope, so a write never hides who it affects.</summary>
    public LocalizedStatus TargetText => LocalizedStatus.Literal(_context is null ? "" : SelectedKey?.Address is { } address
        ? address.Scope == RegistryScope.Server ? $"{address.ServerId} · {LocalizedText.Get("registry.scope.server", "Server instance")} ({address.ScopeId:D})"
        : $"{address.ServerId} · {_context.Username} → {OwnerName(address.OwnerUserId!.Value)} ({address.OwnerUserId:D}) · {ScopeLabel(address.Scope)}"
        : $"{_context.ServerId} · {_context.Username}");

    /// <summary>Values listed for the selected key. Empty text when the key has none, so it never reads as "0" noise.</summary>
    public LocalizedStatus ValueCountText => Entries.Count == 0
        ? LocalizedStatus.Literal("")
        : LocalizedStatus.Key("registry.status.count", "{0} value(s).", Entries.Count);

    /// <summary>True while the selected hive belongs to another account, where every operation needs a grant.</summary>
    public bool ShowPermissionNotice => SelectedKey?.Address is { } address && _context is not null && (address.Scope == RegistryScope.Server || address.OwnerUserId != _context.CurrentUserId);
    public LocalizedStatus PermissionText => LocalizedText.RefWithFallback("registry.permission.required", "Administrator authorization required");

    public bool CanCreateValue => SelectedKey?.CanCreateChildKey == true;
    public bool CanDeleteValue => SelectedEntry?.Source.Schema.CanDelete == true;
    public bool CanEditValue => SelectedEntry?.Source.Schema.CanWrite == true;
    public bool CanRenameValue => SelectedEntry?.Source.Schema.CanRename == true;
    public bool CanResetValue => SelectedEntry?.Source.Schema.DefaultValue is not null;
    public bool CanCreateKey => SelectedKey?.CanCreateChildKey == true;
    public bool CanDeleteKey => SelectedKey?.CanManageKey == true;

    private IRegistryClient ClientFor(RegistryAddress address) => address.Scope == RegistryScope.Server
        ? client.ForServer(address.ScopeId) : client.ForUser(address.OwnerUserId!.Value);

    private string OwnerName(Guid owner) => owner == _context?.CurrentUserId ? _context.Username
        : Keys.FirstOrDefault()?.Children.FirstOrDefault(node => node.Kind == RegistryKeyKind.Users)
            ?.Children.FirstOrDefault(node => node.TargetUserId == owner)?.UserName ?? owner.ToString("D");

    private static string ScopeLabel(RegistryScope scope) => scope switch
    {
        RegistryScope.Server => LocalizedText.Get("registry.scope.server", "Server instance"),
        RegistryScope.User => LocalizedText.Get("registry.scope.user", "User"),
        RegistryScope.Device => LocalizedText.Get("registry.scope.device", "Device"),
        _ => LocalizedText.Get("registry.scope.workspace", "Workspace"),
    };

    public void MoveHistory(int direction)
    {
        var next = _historyIndex + direction;
        if (next < 0 || next >= _history.Count) return;
        _movingHistory = true;
        try { _historyIndex = next; SelectedKey = _history[next]; }
        finally { _movingHistory = false; }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            if (preferences is not null) { if (_context is null) await preferences.EnsureLoadedAsync(); else await preferences.RefreshAsync(); }
            if (_context is null) { _context = await client.GetContextAsync(); BuildStaticTree(); NotifyContext(); }
            if (SelectedKey is { } key)
            {
                if (key.Kind == RegistryKeyKind.Users) { key.ChildrenLoaded = false; await LoadChildrenAsync(key); }
                else await LoadValuesAsync(key, refreshChildren: true);
            }
        }
        catch (Exception error) { SetFailure(error); }
    }

    private void NotifyContext()
    {
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(ShowPermissionNotice));
    }

    partial void OnSelectedKeyChanged(RegistryKeyNode? value)
    {
        ++_navigationGeneration;
        if (!_movingHistory && value is not null)
        {
            if (_historyIndex + 1 < _history.Count) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(value); _historyIndex = _history.Count - 1;
        }
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(ShowPermissionNotice));
        NavigationPathInput = DisplayPath(value);
        OnPropertyChanged(nameof(CanCreateKey));
        OnPropertyChanged(nameof(CanDeleteKey));
        OnPropertyChanged(nameof(CanCreateValue));
        if (value?.Kind == RegistryKeyKind.Users) _ = LoadChildrenAsync(value);
        _ = LoadValuesAsync(value);
    }

    partial void OnSelectedEntryChanged(RegistryEntryRow? value)
    {
        OnPropertyChanged(nameof(CanDeleteValue));
        OnPropertyChanged(nameof(CanEditValue));
        OnPropertyChanged(nameof(CanRenameValue));
        OnPropertyChanged(nameof(CanResetValue));
    }

    public async Task LoadChildrenAsync(RegistryKeyNode? key)
    {
        if (key is null) return;
        if (_childLoads.TryGetValue(key, out var pending)) { await pending; return; }
        var load = LoadChildrenCoreAsync(key);
        _childLoads[key] = load;
        try { await load; }
        finally { _childLoads.Remove(key); }
    }
    private async Task LoadChildrenCoreAsync(RegistryKeyNode? key)
    {
        if (key?.Kind == RegistryKeyKind.Json)
        {
            if (!key.ChildrenLoaded) PopulateJsonChildren(key);
            return;
        }
        if (key is { Kind: RegistryKeyKind.Users, ChildrenLoaded: false, IsLoadingChildren: false })
        {
            key.IsLoadingChildren = true;
            try
            {
                var users = await client.ListUsersAsync();
                var existingUsers = key.Children.Where(node => node.Kind == RegistryKeyKind.User).ToDictionary(node => node.TargetUserId!.Value);
                key.Children.Clear();
                foreach (var user in users)
                {
                    var label = user.Username + (users.Count(item => item.Username == user.Username) > 1 ? " [" + user.Id.ToString("N")[..8] + "]" : "")
                        + (user.IsCurrentUser ? " [" + LocalizedText.Get("registry.tree.current_user", "Current User") + "]" : "");
                    var owner = existingUsers.GetValueOrDefault(user.Id) ?? new RegistryKeyNode(label, null, null, key, RegistryKeyKind.User, user.Id);
                    owner.UserName = user.Username;
                    if (user.WorkspaceId is { } workspaceId && !owner.Children.Any(node => node.Address?.ScopeId == workspaceId)) { owner.Children.Clear(); owner.Children.Add(WorkspaceNode(owner, user.Id, workspaceId)); }
                    if (user.WorkspaceId is null) owner.Children.Clear();
                    key.Children.Add(owner);
                }
                key.ChildrenLoaded = true;
            }
            catch (Exception error) { if (ReferenceEquals(SelectedKey, key)) SetFailure(error); }
            finally { key.IsLoadingChildren = false; }
            return;
        }
        if (key is not { IsRegistryKey: true, Scope: { } scope, Path: { } path } || key.ChildrenLoaded || key.IsLoadingChildren)
            return;
        key.IsLoadingChildren = true;
        try
        {
            var children = await ClientFor(key.Address!).ListKeysAsync(scope, path);
            foreach (var child in children) VerifyAddress(child.Address, key, child.Path);
            var values = await ClientFor(key.Address!).ListValuesAsync(scope, path);
            foreach (var entry in values) VerifyAddress(entry.Address, key, path);
            key.CachedValues = values;
            var virtualRoots = key.Children.Where(node => node.Kind == RegistryKeyKind.Json).ToDictionary(node => node.Document!.Name);
            var existing = key.Children.Where(node => node.Kind == RegistryKeyKind.Key && node.Path is not null).ToDictionary(node => node.Path!);
            key.Children.Clear();
            foreach (var child in children)
            {
                VerifyAddress(child.Address, key, child.Path);
                var node = existing.GetValueOrDefault(child.Path) ?? new RegistryKeyNode(LastSegment(child.Path), child.Path, child.Scope, key);
                node.Address = child.Address; node.AllowCreate = child.CanCreateChild; node.AllowDelete = child.CanDelete;
                key.Children.Add(node);
            }
            AddJsonRoots(key, values, virtualRoots);
            key.ChildrenLoaded = true;
        }
        finally { key.IsLoadingChildren = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is not { Source.Schema.CanDelete: true } selected) return;
        var key = SelectedKey;
        try
        {
            await ClientFor(selected.Source.Address).DeleteAsync(selected.Source.Scope, selected.Source.Path, selected.Source.Name);
            if (!ReferenceEquals(SelectedKey, key)) return;
            Entries.Remove(selected);
            SelectedEntry = null;
            OnPropertyChanged(nameof(ValueCountText));
            StatusText = LocalizedText.RefWithFallback("registry.status.deleted", "Value deleted.");
        }
        catch (Exception ex) { if (ReferenceEquals(SelectedKey, key)) StatusText = LocalizedText.RefWithFallback("registry.status.delete_failed", "Could not delete value: {0}", ex.Message); }
    }

    [RelayCommand]
    private Task NewValue() => SelectedKey is { CanCreateChildKey: true, Scope: { } scope, Path: { } path } && ShowNewValueDialogAsync is not null
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
            await ClientFor(key.Address!).DeleteKeyAsync(scope, path);
            var parent = key.Parent;
            parent?.Children.Remove(key);
            InvalidateOwner(Keys, key.Address!);
            if (!ReferenceEquals(SelectedKey, key)) return;
            SelectedKey = parent;
            StatusText = LocalizedText.RefWithFallback("registry.status.key_deleted", "Key deleted.");
        }
        catch (Exception ex) { if (ReferenceEquals(SelectedKey, key)) StatusText = LocalizedText.RefWithFallback("registry.status.delete_failed", "Could not delete value: {0}", ex.Message); }
    }

    [RelayCommand]
    private Task EditSelectedAsync() => SelectedEntry?.Source.Schema.CanWrite == true && ShowEditDialogAsync is not null ? ShowEditDialogAsync(SelectedEntry) : Task.CompletedTask;

    /// <summary>F2 is available only for a custom value the server declared renameable.</summary>
    [RelayCommand]
    private Task RenameSelectedAsync() => SelectedEntry is { Source.Schema.CanRename: true } row && ShowRenameDialogAsync is not null
        ? ShowRenameDialogAsync(row) : Task.CompletedTask;

    /// <summary>Restore default writes the schema default through the same versioned document write.</summary>
    [RelayCommand]
    private async Task ResetSelectedAsync()
    {
        if (SelectedEntry is not { Source.Schema.DefaultValue: { } defaultValue } selected) return;
        var key = SelectedKey;
        try
        {
            var result = await ClientFor(selected.Source.Address).SaveAsync(new PutRegistryEntryRequest(
                selected.Source.Scope, selected.Source.Path, selected.Source.Name, selected.Source.ValueType, defaultValue, selected.Source.Revision));
            if (!ReferenceEquals(SelectedKey, key)) return;
            ApplySaved(result, LocalizedText.RefWithFallback("registry.status.restored", "Default restored."));
        }
        catch (Exception ex) { if (ReferenceEquals(SelectedKey, key)) StatusText = LocalizedText.RefWithFallback("registry.status.save_failed", "Could not save value: {0}", ex.Message); }
    }

    public void ApplyCreatedKey(RegistryKeyDto created)
    {
        if (SelectedKey is { } parent && parent.Address?.OwnerUserId == created.Address.OwnerUserId && parent.Address!.ServerId == created.Address.ServerId && parent.Address.ScopeId == created.Address.ScopeId && parent.Scope == created.Scope && string.Equals(parent.Path, ParentPath(created.Path), StringComparison.Ordinal))
        {
            parent.Children.Add(new RegistryKeyNode(LastSegment(created.Path), created.Path, created.Scope, parent) { Address = created.Address, AllowCreate = created.CanCreateChild, AllowDelete = created.CanDelete });
            parent.ChildrenLoaded = true;
            parent.IsExpanded = true;
        }
        InvalidateOwner(Keys, created.Address);
    }

    public void ApplySaved(RegistryEntryDto entry, LocalizedStatus? status = null)
    {
        UpdateJsonDocuments(Keys, entry);
        InvalidateOwner(Keys, entry.Address);
        if (SelectedKey?.Address != entry.Address) return;
        var index = Entries.ToList().FindIndex(x => x.Source.Name == entry.Name);
        if (index >= 0) Entries[index] = RegistryEntryRow.From(entry);
        else Entries.Add(RegistryEntryRow.From(entry));
        _ = ReloadAfterSaveAsync(SelectedKey, status ?? LocalizedText.RefWithFallback("registry.status.saved", "Value saved."));
    }

    public void ApplyRenamed(RegistryEntryDto entry)
    {
        UpdateJsonDocuments(Keys, entry);
        InvalidateOwner(Keys, entry.Address);
        if (SelectedKey?.Address != entry.Address) return;
        _ = ReloadAfterSaveAsync(SelectedKey, LocalizedText.RefWithFallback("registry.status.renamed", "Value renamed."));
    }

    private async Task ReloadAfterSaveAsync(RegistryKeyNode? key, LocalizedStatus? status)
    {
        await LoadValuesAsync(key);
        if (status is { } text && ReferenceEquals(SelectedKey, key)) StatusText = text;
    }

    [RelayCommand]
    private async Task NavigateAsync()
    {
        var generation = ++_navigationGeneration;
        var input = NavigationPathInput.Trim().Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var server = Keys.SingleOrDefault();
        RegistryKeyNode? current = null;
        var start = 1;
        if (input.Length >= 2 && input[0].Equals("Server", StringComparison.OrdinalIgnoreCase) && input[1].Equals("Runtime", StringComparison.OrdinalIgnoreCase))
            { current = server?.Children.FirstOrDefault(node => node.Scope == RegistryScope.Server); start = 2; }
        else if (input.Length > 0 && (input[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) || input[0].Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)))
            current = server?.Children.Single(node => node.Kind == RegistryKeyKind.CurrentUser);
        else if (input.Length > 0 && (input[0].Equals("HKU", StringComparison.OrdinalIgnoreCase) || input[0].Equals("HKEY_USERS", StringComparison.OrdinalIgnoreCase)))
        {
            current = server?.Children.Single(node => node.Kind == RegistryKeyKind.Users);
            if (input.Length > 1)
            {
                await LoadChildrenAsync(current);
                current = Guid.TryParse(input[1], out var owner) ? current?.Children.SingleOrDefault(node => node.TargetUserId == owner) : null;
                start = 2;
            }
        }
        try
        {
            for (var i = start; i < input.Length && current is not null; i++)
            {
                await LoadChildrenAsync(current);
                if (generation != _navigationGeneration) return;
                current = current.Children.FirstOrDefault(node => node.NavigationName.Equals(input[i], StringComparison.OrdinalIgnoreCase));
            }
            if (generation != _navigationGeneration) return;
            if (current is null) StatusText = LocalizedText.RefWithFallback("registry.status.path_not_found", "Registry key not found: {0}", NavigationPathInput);
            else
            {
                for (var ancestor = current.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    ancestor.IsExpanded = true;
                SelectedKey = current;
            }
        }
        catch (Exception error) { if (generation == _navigationGeneration) SetFailure(error); }
    }

    private async Task LoadValuesAsync(RegistryKeyNode? key, bool refreshChildren = false)
    {
        var generation = ++_loadGeneration;
        Entries.Clear();
        SelectedEntry = null;
        OnPropertyChanged(nameof(ValueCountText));
        if (key is { Kind: RegistryKeyKind.Json, Document: { } document })
        {
            IsLoading = true;
            PaneState = RegistryPaneState.Loading;
            try
            {
                if (refreshChildren)
                {
                    var latest = await ClientFor(document.Address).ListValuesAsync(document.Scope, document.Path);
                    if (generation != _loadGeneration || !ReferenceEquals(SelectedKey, key)) return;
                    var updated = latest.SingleOrDefault(entry => entry.Name == document.Name)
                        ?? throw new InvalidOperationException(LocalizedText.Get("registry.status.path_not_found", "Registry key not found: {0}"));
                    if (updated.Address != document.Address) throw new InvalidOperationException("Registry response target mismatch.");
                    UpdateJsonDocuments(Keys, updated);
                }
                PopulateJsonChildren(key);
                var value = RegistryJsonProjection.Resolve(key.Document!.DesiredValue, key.JsonPath);
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var property in value.EnumerateObject())
                        Entries.Add(RegistryJsonProjection.Row(key.Document, property.Name, [.. key.JsonPath, property.Name]));
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        var part = (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Entries.Add(RegistryJsonProjection.Row(key.Document, RegistryJsonProjection.Label(part, item), [.. key.JsonPath, part]));
                    }
                }
                else Entries.Add(RegistryJsonProjection.Row(key.Document, key.NavigationName, key.JsonPath));
                PaneState = Entries.Count == 0 ? RegistryPaneState.Empty : RegistryPaneState.Ready;
                StatusText = Entries.Count == 0 ? LocalizedText.RefWithFallback("registry.status.empty", "No values in this key.") : LocalizedStatus.Literal("");
                OnPropertyChanged(nameof(ValueCountText));
            }
            catch (Exception error) { if (generation == _loadGeneration) SetFailure(error); }
            finally { if (generation == _loadGeneration) IsLoading = false; }
            return;
        }
        if (key is null || !key.IsRegistryKey || key.Scope is not { } scope || key.Path is not { } path)
        {
            IsLoading = false;
            PaneState = RegistryPaneState.Idle;
            StatusText = LocalizedStatus.Literal("");
            return;
        }
        IsLoading = true;
        PaneState = RegistryPaneState.Loading;
        StatusText = LocalizedText.RefWithFallback("registry.status.loading", "Loading registry…");
        try
        {
            if (refreshChildren) key.ChildrenLoaded = false;
            await LoadChildrenAsync(key);
            var values = key.CachedValues ?? await ClientFor(key.Address!).ListValuesAsync(scope, path);
            key.CachedValues = null;
            if (generation != _loadGeneration || !ReferenceEquals(SelectedKey, key)) return;
            foreach (var entry in values) VerifyAddress(entry.Address, key, path);
            foreach (var entry in values) Entries.Add(RegistryEntryRow.From(entry));
            var jsonRoots = key.Children.Where(node => node.Kind == RegistryKeyKind.Json).ToDictionary(node => node.Document!.Name);
            foreach (var node in jsonRoots.Values) key.Children.Remove(node);
            AddJsonRoots(key, values, jsonRoots);
            PaneState = values.Count == 0 ? RegistryPaneState.Empty : RegistryPaneState.Ready;
            StatusText = values.Count == 0 ? LocalizedText.RefWithFallback("registry.status.empty", "No values in this key.") : LocalizedStatus.Literal("");
            OnPropertyChanged(nameof(ValueCountText));
        }
        catch (Exception ex) { if (generation == _loadGeneration && ReferenceEquals(SelectedKey, key)) SetFailure(ex); }
        finally { if (generation == _loadGeneration) IsLoading = false; }
    }

    /// <summary>
    /// Separates the read outcomes the design calls out: an authorization refusal and a transport
    /// failure must never be rendered as "this key has no values".
    /// </summary>
    private void SetFailure(Exception error)
    {
        PaneState = Classify(error);
        StatusText = PaneState switch
        {
            RegistryPaneState.Denied when SelectedKey?.Scope == RegistryScope.Server => LocalizedText.RefWithFallback("registry.status.server_access_denied", "Server runtime management requires administrator authorization."),
            RegistryPaneState.Denied => LocalizedText.RefWithFallback("registry.status.access_denied",
                "Access denied: administrator authorization is required to open another account's hive."),
            RegistryPaneState.Disconnected => LocalizedText.RefWithFallback("registry.status.disconnected",
                "Cannot reach the server: {0}", error.Message),
            _ => LocalizedText.RefWithFallback("registry.status.failed", "Could not read this key: {0}", ErrorText(error)),
        };
        OnPropertyChanged(nameof(ValueCountText));
    }

    private static RegistryPaneState Classify(Exception error) => error switch
    {
        RelaxKonOSAuthException { Status: 403 } => RegistryPaneState.Denied,
        HttpRequestException => RegistryPaneState.Disconnected,
        TaskCanceledException => RegistryPaneState.Disconnected,
        _ => RegistryPaneState.Failed,
    };

    private static string ErrorText(Exception error) => error is RelaxKonOSAuthException { Status: 401 }
        ? LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry.")
        : error.Message;

    private void BuildStaticTree()
    {
        if (Keys.Count != 0) return;
        var context = _context!;
        var root = new RegistryKeyNode(context.ServerId, null, null, null, RegistryKeyKind.Root) { IsExpanded = true };
        var current = new RegistryKeyNode("HKEY_CURRENT_USER — " + context.Username, null, null, root, RegistryKeyKind.CurrentUser, context.CurrentUserId);
        if (context.WorkspaceId is { } workspace) current.Children.Add(WorkspaceNode(current, context.CurrentUserId, workspace));
        if (context.DeviceId is { } device)
            current.Children.Add(new RegistryKeyNode("Device", "Device", RegistryScope.Device, current)
            { Address = new(context.ServerId, context.CurrentUserId, RegistryScope.Device, device, "Device") });
        root.Children.Add(new RegistryKeyNode(LocalizedText.Get("registry.tree.server_runtime", "Server runtime"), "Server\\Runtime", RegistryScope.Server, root)
        { Address = new(context.ServerId, null, RegistryScope.Server, context.ServerInstanceId, "Server\\Runtime") });
        root.Children.Add(current);
        root.Children.Add(new RegistryKeyNode("HKEY_USERS", null, null, root, RegistryKeyKind.Users));
        Keys.Add(root);
        SelectedKey ??= current;
    }

    private RegistryKeyNode WorkspaceNode(RegistryKeyNode parent, Guid owner, Guid workspace) => new("Workspace", "Workspace", RegistryScope.Workspace, parent)
    { Address = new(_context!.ServerId, owner, RegistryScope.Workspace, workspace, "Workspace"), AllowCreate = true };
    private static void VerifyAddress(RegistryAddress address, RegistryKeyNode key, string path)
    {
        if (address != key.Address! with { KeyPath = path }) throw new InvalidOperationException("Registry response owner does not match the requested address.");
    }
    private static void InvalidateOwner(IEnumerable<RegistryKeyNode> nodes, RegistryAddress address)
    {
        foreach (var node in nodes)
        {
            if (node.Address is { } target && target.ServerId == address.ServerId && target.OwnerUserId == address.OwnerUserId
                && target.Scope == address.Scope && target.ScopeId == address.ScopeId) { node.ChildrenLoaded = false; node.CachedValues = null; }
            InvalidateOwner(node.Children, address);
        }
    }
    private static void AddJsonRoots(RegistryKeyNode key, IReadOnlyList<RegistryEntryDto> values, Dictionary<string, RegistryKeyNode> previous)
    {
        foreach (var entry in values.Where(entry => entry.DesiredValue.ValueKind == JsonValueKind.Array))
        {
            var node = previous.GetValueOrDefault(entry.Name) ?? JsonKey(key, entry, [], entry.Name);
            UpdateJsonDocuments([node], entry);
            node.Name = RegistryJsonProjection.NodeLabel(entry.Name, entry.DesiredValue);
            key.Children.Add(node);
        }
    }

    private static RegistryKeyNode JsonKey(RegistryKeyNode parent, RegistryEntryDto document, IReadOnlyList<string> path, string label)
    {
        var node = new RegistryKeyNode(RegistryJsonProjection.NodeLabel(label, RegistryJsonProjection.Resolve(document.DesiredValue, path)), document.Path, document.Scope, parent, RegistryKeyKind.Json)
        { Document = document, Address = document.Address, JsonPath = path };
        var value = RegistryJsonProjection.Resolve(document.DesiredValue, path);
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 || value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any())
            node.Children.Add(new RegistryKeyNode("…", null, null, node, RegistryKeyKind.Placeholder));
        return node;
    }

    private static void PopulateJsonChildren(RegistryKeyNode key)
    {
        if (key.ChildrenLoaded) return;
        var value = RegistryJsonProjection.Resolve(key.Document!.DesiredValue, key.JsonPath);
        var previous = key.Children.Where(node => node.Kind == RegistryKeyKind.Json).ToDictionary(node => node.JsonPath[^1]);
        key.Children.Clear();
        void Add(string part, string label)
        {
            var node = previous.GetValueOrDefault(part) ?? JsonKey(key, key.Document, [.. key.JsonPath, part], label);
            node.Name = RegistryJsonProjection.NodeLabel(label, RegistryJsonProjection.Resolve(key.Document.DesiredValue, node.JsonPath));
            key.Children.Add(node);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var part = (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Add(part, RegistryJsonProjection.Label(part, item));
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Add(property.Name, property.Name);
        key.ChildrenLoaded = true;
    }

    private static void UpdateJsonDocuments(IEnumerable<RegistryKeyNode> nodes, RegistryEntryDto entry)
    {
        foreach (var node in nodes)
        {
            if (node.Document is { } document && document.Address == entry.Address && document.Name == entry.Name)
            {
                node.Document = entry;
                node.ChildrenLoaded = false;
                try
                {
                    var value = RegistryJsonProjection.Resolve(entry.DesiredValue, node.JsonPath);
                    var label = node.JsonPath.Count == 0 ? entry.Name
                        : node.Parent?.JsonValueKind == JsonValueKind.Array ? RegistryJsonProjection.Label(node.JsonPath[^1], value) : node.JsonPath[^1];
                    node.Name = RegistryJsonProjection.NodeLabel(label, value);
                    node.NotifyJsonChanged();
                }
                catch (Exception error) when (error is KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
                { /* Removed paths are reconciled when their parent is populated; selection reports the read failure. */ }
            }
            UpdateJsonDocuments(node.Children, entry);
        }
    }

    private static string ParentPath(string path) => path[..path.LastIndexOf('\\')];
    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];
    private static string DisplayPath(RegistryKeyNode? node)
    {
        if (node?.Scope == RegistryScope.Server) return node.Path ?? "Server\\Runtime";
        if (node?.Kind == RegistryKeyKind.Json) return DisplayPath(node.Parent) + "\\" + node.NavigationName;
        if (node is null || node.Kind == RegistryKeyKind.Root) return "";
        if (node.Kind == RegistryKeyKind.Users) return "HKEY_USERS";
        var current = node;
        while (current.Parent is not null && current.Kind != RegistryKeyKind.CurrentUser && current.Kind != RegistryKeyKind.User) current = current.Parent;
        var prefix = current.Kind == RegistryKeyKind.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_USERS\\" + node.TargetUserId?.ToString("D");
        return prefix + (node.Path is null ? "" : "\\" + node.Path);
    }
}

/// <summary>Read outcome of the value pane. Only <see cref="Ready"/> and <see cref="Empty"/> mean "we have data".</summary>
public enum RegistryPaneState { Idle, Loading, Ready, Empty, Denied, Failed, Disconnected }

public sealed record RegistryEntryRow(RegistryEntryDto Source, string Name, string DesiredValue, string Type)
{
    public RegistryEntryDto? Document { get; init; }
    public IReadOnlyList<string> JsonPath { get; init; } = [];
    public static RegistryEntryRow From(RegistryEntryDto entry) => new(entry, entry.Name, Format(entry.DesiredValue), entry.ValueType.ToString());
    private static string Format(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return text.Length > 240 ? text[..237] + "…" : text;
    }
}

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
