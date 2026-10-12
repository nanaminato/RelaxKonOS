using System.Text.Json;
using Avalonia.Controls;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;

internal static class RegistryChecks
{
    public static async Task RunAsync()
    {
        var client = new FakeRegistryClient();
        var vm = new RegistryViewModel(client);
        await vm.RefreshAsync();
        var server = vm.Keys.Single();
        var hkcu = server.Children.Single(node => node.Kind == RegistryKeyKind.CurrentUser);
        var hku = server.Children.Single(node => node.Kind == RegistryKeyKind.Users);
        Check(hkcu.Name.Contains("nanami"), "HKCU must identify the authenticated remote user.");
        await vm.LoadChildrenAsync(hku);
        var alias = hku.Children.Single(node => node.TargetUserId == client.Owner).Children.Single();
        var own = hkcu.Children.Single();
        var other = hku.Children.Single(node => node.TargetUserId == client.Other).Children.Single();
        Check(own.Address == alias.Address, "HKCU and HKU must share the same canonical address.");
        Check(hku.Children.Single(node => node.TargetUserId == client.Other).Children.Count == 1, "Workspace discovery failed.");

        var slow = new TaskCompletionSource<IReadOnlyList<RegistryEntryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Pending = slow.Task;
        vm.SelectedKey = own;
        client.Pending = null;
        vm.SelectedKey = other;
        await Until(() => vm.Entries.Count == 1 && !vm.IsLoading);
        var status = vm.StatusText.Resolve();
        slow.SetException(new InvalidOperationException("late owner error"));
        await Task.Delay(30);
        Check(vm.StatusText.Resolve() == status && vm.Entries.Single().Source.Address.OwnerUserId == client.Other,
            "A late error overwrote another user's selection.");
        vm.ApplySaved(client.Value(client.Owner, "changed"));
        Check(vm.Entries.Single().DesiredValue == "alice", "A save callback crossed owner boundaries.");

        vm.NavigationPathInput = "HKCU\\Workspace";
        await vm.NavigateCommand.ExecuteAsync(null);
        await Until(() => vm.Entries.Count == 1 && !vm.IsLoading);
        Check(vm.NavigationPathInput == "HKEY_CURRENT_USER\\Workspace", "HKCU input did not normalize.");
        vm.NavigationPathInput = $"HKU\\{client.Other:D}\\Workspace";
        await vm.NavigateCommand.ExecuteAsync(null);
        Check(vm.SelectedKey?.Address == other.Address, "HKU did not resolve the stable user ID.");
        vm.MoveHistory(-1);
        Check(vm.SelectedKey?.Address == own.Address, "Navigation history changed the owner.");

        var entry = client.Value(client.Other, "alice");
        var dialog = new RegistryValueDialogViewModel(RegistryEntryRow.From(entry), client.ForUser(client.Other), _ => { }, _ => { });
        Check(dialog.ValueText == "alice" && !dialog.CanChangeIdentity, "Managed strings should not require JSON quoting.");
        dialog.ValueText = "edited";
        await dialog.SaveCommand.ExecuteAsync(null);
        Check(client.SavedOwner == client.Other && client.SavedRequest?.Value.GetString() == "edited", "Dialog lost its fixed owner or string type.");
        dialog.ResetCommand.Execute(null);
        Check(dialog.ValueText == "default", "Restore default must use the schema value.");
        var flag = entry with { ValueType = RegistryValueType.Boolean, DesiredValue = JsonSerializer.SerializeToElement(true) };
        var booleanDialog = new RegistryValueDialogViewModel(RegistryEntryRow.From(flag), client.ForUser(client.Other), _ => { }, _ => { });
        Check(booleanDialog.IsBoolean && booleanDialog.BooleanValue, "Boolean editor did not load its typed value.");
        booleanDialog.BooleanValue = false;
        await booleanDialog.SaveCommand.ExecuteAsync(null);
        Check(client.SavedRequest?.Value.ValueKind == JsonValueKind.False, "Boolean control did not save a Boolean.");

        await CheckPaneStatesAsync(client, vm, own, other);
        await CheckRenameAsync(client, vm, entry);
        await CheckEditorsAsync(client);
        CheckSurfaces(client, vm, entry);

        Console.WriteLine("PASS: registry aliases, stable-owner navigation, late-error isolation, fixed dialogs and typed default editing.");
        Console.WriteLine("PASS: registry read-outcome states, status bar owner/permission, rename, structured list, hexadecimal integer and colour palette.");
    }

    /// <summary>A refused read, a transport failure and an empty key must never render as each other.</summary>
    private static async Task CheckPaneStatesAsync(FakeRegistryClient client, RegistryViewModel vm, RegistryKeyNode own, RegistryKeyNode other)
    {
        var emptyText = LocalizedText.Get("registry.status.empty", "No values in this key.");
        client.Failure = new RelaxKonOSAuthException(new ProblemDetails(
            "https://relaxkonos.app/problems/elevation-required", "Registry", 403, null, null));
        vm.SelectedKey = own;
        vm.SelectedKey = other;
        await Until(() => vm.PaneState == RegistryPaneState.Denied && !vm.IsLoading);
        Check(vm.Entries.Count == 0 && vm.StatusText.Resolve() != emptyText,
            "A refused read was reported as an empty key.");

        client.Failure = new HttpRequestException("connection reset");
        vm.SelectedKey = own;
        await Until(() => vm.PaneState == RegistryPaneState.Disconnected && !vm.IsLoading);
        Check(vm.StatusText.Resolve() != emptyText, "A transport failure was reported as an empty key.");

        client.Failure = null;
        vm.SelectedKey = other;
        await Until(() => vm.PaneState == RegistryPaneState.Ready && !vm.IsLoading);
        Check(vm.Entries.Count == 1 && vm.StatusText.Resolve() == "", "A successful read must leave the pane in a ready state.");
        Check(vm.ShowPermissionNotice && vm.TargetText.Resolve().Contains("alice") && vm.TargetText.Resolve().Contains("nanami"),
            "The status bar must show the signed-in account, the target owner and the permission requirement.");
        Check(vm.ValueCountText.Resolve().Contains("1"), "The status bar must report the value count.");
        vm.SelectedKey = own;
        await Until(() => vm.PaneState == RegistryPaneState.Ready && !vm.IsLoading);
        Check(!vm.ShowPermissionNotice, "The own hive must not demand administrator authorization.");
    }

    private static async Task CheckRenameAsync(FakeRegistryClient client, RegistryViewModel vm, RegistryEntryDto otherEntry)
    {
        var custom = otherEntry with
        {
            Address = new("server", client.Other, RegistryScope.Workspace, client.OtherWorkspaceId, "Workspace\\Custom"),
            Path = "Workspace\\Custom", Name = "Alpha", OwnerUsername = "alice",
            Schema = otherEntry.Schema with { CanDelete = true, CanRename = true, DefaultValue = null },
        };
        vm.SelectedEntry = RegistryEntryRow.From(custom);
        Check(vm.CanRenameValue && !vm.CanResetValue, "A custom value must expose rename but not restore-default.");
        RegistryEntryRow? requested = null;
        vm.ShowRenameDialogAsync = row => { requested = row; return Task.CompletedTask; };
        await vm.RenameSelectedCommand.ExecuteAsync(null);
        Check(requested?.Source.Name == "Alpha", "F2 rename did not open the fixed-target dialog for the selected value.");

        var rename = new RegistryRenameDialogViewModel(requested!, client.ForUser(client.Other), _ => { }, _ => { });
        Check(rename.TargetLabel.Contains("alice"), "The rename dialog must name the fixed target owner.");
        rename.NewName = "Beta";
        await rename.SaveCommand.ExecuteAsync(null);
        Check(client.RenameRequest is { Name: "Alpha", NewName: "Beta" } && client.RenameOwner == client.Other,
            "The rename dialog did not send the fixed address with the new name.");
        Check(client.RenameRequest is { } sent && sent.Scope == RegistryScope.Workspace && sent.Path == "Workspace\\Custom",
            "The rename lost its key path or scope.");
    }

    private static async Task CheckEditorsAsync(FakeRegistryClient client)
    {
        var listEntry = CustomEntry(client, "visibleAppIds", RegistryValueType.Json, JsonSerializer.SerializeToElement(Array.Empty<string>()),
            RegistryEditorKind.StringList, JsonSerializer.SerializeToElement(Array.Empty<string>()));
        var listDialog = new RegistryValueDialogViewModel(RegistryEntryRow.From(listEntry), client.ForUser(client.Other), _ => { }, _ => { });
        Check(listDialog.IsStringList && !listDialog.IsJson && listDialog.ListItems.Count == 0,
            "An empty string list must use the list editor instead of raw JSON.");
        foreach (var item in new[] { "relaxkonos.terminal", "relaxkonos.browser" })
        {
            listDialog.NewListItem = item;
            listDialog.AddListItemCommand.Execute(null);
        }
        listDialog.RemoveListItemCommand.Execute(listDialog.ListItems[1]);
        listDialog.NewListItem = "relaxkonos.registry";
        listDialog.AddListItemCommand.Execute(null);
        await listDialog.SaveCommand.ExecuteAsync(null);
        Check(client.SavedRequest?.Value.GetArrayLength() == 2 && client.SavedRequest.Value[1].GetString() == "relaxkonos.registry",
            "The list editor must save a real array with the edited rows.");

        var integerEntry = CustomEntry(client, "fontWeight", RegistryValueType.Number, JsonSerializer.SerializeToElement(16),
            RegistryEditorKind.Integer, JsonSerializer.SerializeToElement(16));
        var integerDialog = new RegistryValueDialogViewModel(RegistryEntryRow.From(integerEntry), client.ForUser(client.Other), _ => { }, _ => { });
        Check(integerDialog.IsInteger && !integerDialog.IsNumber, "An integer field must not use the floating-point editor.");
        integerDialog.IsHex = true;
        Check(integerDialog.HexText == "0x10", "The hexadecimal display must render the current value.");
        integerDialog.HexText = "0xFF";
        Check(integerDialog.NumberValue == 255, "Hexadecimal input must update the numeric value.");
        await integerDialog.SaveCommand.ExecuteAsync(null);
        Check(client.SavedRequest?.Value.ValueKind == JsonValueKind.Number && client.SavedRequest.Value.GetInt64() == 255,
            "An integer field must submit a number, never hexadecimal text.");

        var colorEntry = CustomEntry(client, "backgroundColor", RegistryValueType.String, JsonSerializer.SerializeToElement("#0C0C0C"),
            RegistryEditorKind.Color, JsonSerializer.SerializeToElement("#0C0C0C"));
        var colorDialog = new RegistryValueDialogViewModel(RegistryEntryRow.From(colorEntry), client.ForUser(client.Other), _ => { }, _ => { });
        Check(colorDialog.IsColor && !colorDialog.IsText && colorDialog.Palette.Count > 0, "A colour field must offer the palette and preview.");
        colorDialog.ValueText = "not-a-colour";
        await colorDialog.SaveCommand.ExecuteAsync(null);
        Check(colorDialog.Error is not null, "An illegal colour must be rejected before the write reaches the server.");
        colorDialog.PickColorCommand.Execute("#3B78FF");
        Check(colorDialog.ValueText == "#3B78FF", "A palette swatch must set a legal colour value.");
        await colorDialog.SaveCommand.ExecuteAsync(null);
        Check(colorDialog.Error is null && client.SavedRequest?.Value.GetString() == "#3B78FF", "A palette colour must save as a string.");
    }

    private static RegistryEntryDto CustomEntry(FakeRegistryClient client, string name, RegistryValueType type, JsonElement value,
        RegistryEditorKind editor, JsonElement? defaultValue)
    {
        const string path = "Workspace\\Desktop\\desktopDisplay";
        return new RegistryEntryDto(RegistryScope.Workspace, path, name, type, value, 7, RegistryEntryState.Synced,
            DateTimeOffset.UtcNow, null, RegistryApplyMode.Immediate, null, null, null)
        {
            Address = new("server", client.Other, RegistryScope.Workspace, client.OtherWorkspaceId, path),
            Schema = new(name, true, true, false, defaultValue) { Editor = editor },
        };
    }

    /// <summary>
    /// Every redesigned surface must parse and apply its templates: a missing click handler or a
    /// malformed binding is invisible to a view-model-only check.
    /// </summary>
    private static void CheckSurfaces(FakeRegistryClient client, RegistryViewModel vm, RegistryEntryDto entry)
    {
        var owned = client.ForUser(client.Other);
        var stringEntry = entry with { OwnerUsername = "alice", Schema = entry.Schema with { DefaultValue = JsonSerializer.SerializeToElement("default") } };
        var surfaces = new Control[]
        {
            new RegistryView { DataContext = vm },
            new RegistryValueDialogView { DataContext = new RegistryValueDialogViewModel(RegistryEntryRow.From(stringEntry), owned, _ => { }, _ => { }) },
            new RegistryValueDialogView { DataContext = new RegistryValueDialogViewModel(RegistryEntryRow.From(
                CustomEntry(client, "visibleAppIds", RegistryValueType.Json, JsonSerializer.SerializeToElement(new[] { "relaxkonos.terminal" }),
                    RegistryEditorKind.StringList, JsonSerializer.SerializeToElement(Array.Empty<string>()))), owned, _ => { }, _ => { }) },
            new RegistryValueDialogView { DataContext = new RegistryValueDialogViewModel(RegistryEntryRow.From(
                CustomEntry(client, "fontWeight", RegistryValueType.Number, JsonSerializer.SerializeToElement(14),
                    RegistryEditorKind.Integer, JsonSerializer.SerializeToElement(14))), owned, _ => { }, _ => { }) },
            new RegistryValueDialogView { DataContext = new RegistryValueDialogViewModel(RegistryEntryRow.From(
                CustomEntry(client, "backgroundColor", RegistryValueType.String, JsonSerializer.SerializeToElement("#0C0C0C"),
                    RegistryEditorKind.Color, JsonSerializer.SerializeToElement("#0C0C0C"))), owned, _ => { }, _ => { }) },
            new RegistryRenameDialogView { DataContext = new RegistryRenameDialogViewModel(RegistryEntryRow.From(
                entry with { OwnerUsername = "alice", Schema = entry.Schema with { CanRename = true } }), owned, _ => { }, _ => { }) },
            new RegistryKeyDialogView { DataContext = new RegistryKeyDialogViewModel(RegistryScope.Workspace, "Workspace", owned, _ => { }, _ => { }) },
        };
        foreach (var surface in surfaces)
        {
            var window = new Window { Width = 900, Height = 620, Content = surface };
            window.Show();
            window.Close();
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Check(condition(), "Registry request did not complete.");
    }

    private sealed class FakeRegistryClient : IRegistryClient
    {
        private readonly FakeRegistryClient? _root;
        private readonly Guid _target;
        private FakeRegistryClient Root => _root ?? this;
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        private Guid Workspace { get; } = Guid.NewGuid();
        private Guid OtherWorkspace { get; } = Guid.NewGuid();
        public Guid OtherWorkspaceId => OtherWorkspace;
        public Task<IReadOnlyList<RegistryEntryDto>>? Pending { get; set; }
        public Exception? Failure { get; set; }
        public Guid? SavedOwner { get; private set; }
        public PutRegistryEntryRequest? SavedRequest { get; private set; }
        public Guid? RenameOwner { get; private set; }
        public RenameRegistryEntryRequest? RenameRequest { get; private set; }
        public FakeRegistryClient() { }
        private FakeRegistryClient(FakeRegistryClient root, Guid target) { _root = root; _target = target; }
        public IRegistryClient ForUser(Guid targetUserId) => new FakeRegistryClient(Root, targetUserId);
        public Task<RegistryContextDto> GetContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RegistryContextDto("server", Root.Owner, "nanami", Root.Workspace, true));
        public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RegistryUserDto>>([
            new(Root.Owner, "nanami", true, Root.Workspace), new(Root.Other, "alice", false, Root.OtherWorkspace)]);
        public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RegistryKeyDto>>([]);
        public Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) =>
            Root.Failure is { } failure ? Task.FromException<IReadOnlyList<RegistryEntryDto>>(failure)
            : Root.Pending ?? Task.FromResult<IReadOnlyList<RegistryEntryDto>>([Root.Value(_target, _target == Root.Owner ? "nanami" : "alice")]);
        public RegistryEntryDto Value(Guid owner, string text) => new(RegistryScope.Workspace, "Workspace", "field", RegistryValueType.String,
            JsonSerializer.SerializeToElement(text), 1, RegistryEntryState.Synced, DateTimeOffset.UtcNow, null, RegistryApplyMode.Immediate, null, null, null)
        {
            Address = new("server", owner, RegistryScope.Workspace, owner == Owner ? Workspace : OtherWorkspace, "Workspace"),
            Schema = new("field", true, false, false, JsonSerializer.SerializeToElement("default")),
        };
        public Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken cancellationToken = default)
        {
            Root.SavedOwner = _target; Root.SavedRequest = request;
            return Task.FromResult(Root.Value(_target, "saved") with
            {
                DesiredValue = request.Value, ValueType = request.ValueType, Path = request.Path, Name = request.Name,
                Address = new("server", _target, request.Scope, _target == Root.Owner ? Root.Workspace : Root.OtherWorkspace, request.Path),
            });
        }
        public Task<RegistryEntryDto> RenameAsync(RenameRegistryEntryRequest request, CancellationToken cancellationToken = default)
        {
            Root.RenameOwner = _target; Root.RenameRequest = request;
            return Task.FromResult(Root.Value(_target, "renamed") with
            {
                Name = request.NewName, Path = request.Path,
                Address = new("server", _target, request.Scope, _target == Root.Owner ? Root.Workspace : Root.OtherWorkspace, request.Path),
            });
        }
        public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
