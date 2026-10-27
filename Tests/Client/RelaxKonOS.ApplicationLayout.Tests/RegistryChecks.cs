using System.Text.Json;
using Avalonia.Controls;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;

internal static class RegistryChecks
{
    public static async Task RunAsync(RelaxKonOS.Client.Services.ShellSettings settings)
    {
        await PresentationPreferencesChecks.RunAsync();
        await ServerRuntimeClientChecks.RunAsync(settings);
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
        await CheckJsonEditorAsync(client);
        CheckSurfaces(client, vm, entry);
        CheckTreeScrollbars(client);
        await CheckListNavigationAsync(client, vm, own);

        Console.WriteLine("PASS: registry aliases, stable-owner navigation, late-error isolation, fixed dialogs and typed default editing.");
        Console.WriteLine("PASS: registry read-outcome states, status bar owner/permission, rename, typed editors and lazy JSON editing/mode switching.");
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
        integerDialog.IsHex = false;
        integerDialog.NumberValue = 1.5m;
        await integerDialog.SaveCommand.ExecuteAsync(null);
        Check(integerDialog.Error is not null && client.SavedRequest!.Value.GetInt64() == 255,
            "Integer settings must reject fractional input instead of silently truncating it.");

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

    private static async Task CheckJsonEditorAsync(FakeRegistryClient client)
    {
        var value = JsonSerializer.Deserialize<JsonElement>("""{"nested":{"items":[{"text":"hello","enabled":true},null,1.25]},"untouched":{"large":9007199254740993}}""");
        var entry = CustomEntry(client, "document", RegistryValueType.Json, value, RegistryEditorKind.Json, value);
        var dialog = new RegistryValueDialogViewModel(RegistryEntryRow.From(entry), client.ForUser(client.Other), _ => { }, _ => { });
        var root = dialog.JsonRoots.Single();
        var nested = root.Children.Single(node => node.Name == "nested");
        Check(nested.Children.Single().Name == "…", "Nested JSON was eagerly materialized.");
        nested.IsExpanded = true;
        var items = nested.Children.Single();
        items.IsExpanded = true;
        items.Children[0].IsExpanded = true;
        items.Children[0].Children.Single(node => node.Name == "text").ValueText = "quoted \"text\"\n中文";
        var flag = items.Children[0].Children.Single(node => node.Name == "enabled");
        flag.ValueText = "false";
        await dialog.SaveCommand.ExecuteAsync(null);
        var saved = client.SavedRequest!.Value;
        Check(saved.GetProperty("nested").GetProperty("items")[0].GetProperty("text").GetString() == "quoted \"text\"\n中文",
            "Structured strings lost escaping or Unicode.");
        Check(saved.GetProperty("untouched").GetProperty("large").GetInt64() == 9007199254740993,
            "An unopened branch lost numeric precision.");
        Check(saved.GetProperty("nested").GetProperty("items")[1].ValueKind == JsonValueKind.Null && client.SavedRequest.ExpectedRevision == 7,
            "JSON null or versioned save was lost.");
        flag.ValueText = "123";
        dialog.IsRawJson = true;
        Check(!dialog.IsRawJson && dialog.Error is not null, "Invalid property type escaped through mode switching.");
        flag.ValueText = "true";
        dialog.IsRawJson = true;
        Check(dialog.IsRawJson && dialog.ValueText.Contains("9007199254740993"), "Raw mode discarded tree edits.");
        dialog.ValueText = "{broken";
        dialog.IsRawJson = false;
        Check(dialog.IsRawJson && dialog.Error is not null, "Malformed JSON replaced the property tree.");
        dialog.ValueText = "{\"new\": [false, null]}";
        dialog.IsRawJson = false;
        Check(dialog.IsStructuredJson && dialog.JsonRoots.Single().Children.Single().Name == "new", "Raw edits did not rebuild the tree.");
        dialog.ResetCommand.Execute(null);
        Check(dialog.JsonRoots.Single().BuildValue()!.ToJsonString() == value.GetRawText(), "Reset did not rebuild the JSON document.");
    }

    private static void CheckTreeScrollbars(FakeRegistryClient client)
    {
        var vm = new RegistryViewModel(client);
        var root = new RegistryKeyNode("Root", null, null, null, RegistryKeyKind.Root) { IsExpanded = true };
        for (var index = 0; index < 80; index++)
            root.Children.Add(new RegistryKeyNode($"[{index}] A long registry node name that must remain accessible by horizontal scrolling", null, null, root, RegistryKeyKind.Root));
        vm.Keys.Add(root);
        var view = new RegistryView { DataContext = vm };
        var window = new Window { Width = 700, Height = 380, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var tree = view.FindControl<TreeView>("RegistryTree")!;
        var bars = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tree).OfType<Avalonia.Controls.Primitives.ScrollBar>().ToArray();
        var vertical = bars.Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical);
        var horizontal = bars.Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal);
        Check(vertical.IsVisible && horizontal.IsVisible, "Overflowing registry trees must retain both scrollbars.");
        Check(vertical.Bounds.Width == 6 && horizontal.Bounds.Height == 6, "Registry scrollbar tracks must be 6px thick.");
        var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tree).OfType<ScrollViewer>().First();
        Check(!scroller.AllowAutoHide && scroller.Extent.Width > scroller.Viewport.Width && scroller.Extent.Height > scroller.Viewport.Height,
            "Tree scrollbars must remain visible when overflow exists.");
        scroller.Offset = new Avalonia.Vector(100, 100);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(scroller.Offset.X > 0 && scroller.Offset.Y > 0, "Both tree scrolling axes must remain usable.");
        var output = System.IO.Path.Combine(AppContext.BaseDirectory, "layout-qa");
        System.IO.Directory.CreateDirectory(output);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(700, 380));
        bitmap.Render(window);
        bitmap.Save(System.IO.Path.Combine(output, "registry-scrollbars.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
    }

    private static async Task CheckListNavigationAsync(FakeRegistryClient client, RegistryViewModel vm, RegistryKeyNode own)
    {
        var document = client.Value(client.Owner, "") with
        {
            Name = "windows", ValueType = RegistryValueType.Json, Revision = 23,
            DesiredValue = JsonSerializer.Deserialize<JsonElement>("""[{"key":"relaxkonos.settings:设置","width":1110,"layout":{"panes":[{"name":"左侧","size":240}]}},{"title":"终端","width":900},"bash",null]"""),
            Schema = new("windows", true, false, false, null) { Editor = RegistryEditorKind.Json },
        };
        client.Values = [document];
        vm.SelectedKey = own;
        await vm.RefreshAsync();
        var list = own.Children.Single(node => node.Kind == RegistryKeyKind.Json);
        Check(list.Name.StartsWith("windows · 4") && list.Children.Single().Kind == RegistryKeyKind.Placeholder, "List roots need counts and lazy children.");
        await vm.LoadChildrenAsync(list);
        Check(list.Children.Select(node => node.Name).SequenceEqual(new[] { "[0] 设置", "[1] 终端", "[2] \"bash\"", "[3] null" }), "List labels must combine indexes with readable descriptions.");
        var first = list.Children[0];
        vm.SelectedKey = first;
        await Until(() => vm.PaneState == RegistryPaneState.Ready && vm.Entries.Count == 3);
        Check(!vm.CanCreateKey && !vm.CanDeleteKey && first.Children.Single().Name == "layout", "Virtual nodes must not expose real key mutations.");
        var width = vm.Entries.Single(row => row.Name == "width");
        vm.SelectedEntry = width;
        Check(vm.CanEditValue && !vm.CanDeleteValue && !vm.CanRenameValue && !vm.CanResetValue, "Projected properties must inherit write permission without document-level destructive actions.");
        var dialog = new RegistryValueDialogViewModel(width, client.ForUser(client.Owner), _ => { }, saved => vm.ApplySaved(saved));
        dialog.NumberValue = 1280;
        await dialog.SaveCommand.ExecuteAsync(null);
        var request = client.SavedRequest!;
        Check(dialog.Error is null && request.Name == "windows" && request.Path == "Workspace" && request.ExpectedRevision == 23 && client.SavedOwner == client.Owner,
            "Editing a property must write the original document identity, owner and revision.");
        Check(request.Value[0].GetProperty("width").GetInt32() == 1280 && request.Value[1].GetProperty("width").GetInt32() == 900 && request.Value[3].ValueKind == JsonValueKind.Null,
            "Editing one property must preserve siblings and null items.");
        await Until(() => vm.Entries.Any(row => row.Name == "width" && row.DesiredValue == "1280"));
        own.Parent!.IsExpanded = true;
        own.IsExpanded = true;
        list.IsExpanded = true;
        first.IsExpanded = true;
        var preview = new Window { Width = 1120, Height = 700, Content = new RegistryView { DataContext = vm } };
        preview.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var output = System.IO.Path.Combine(AppContext.BaseDirectory, "layout-qa");
        System.IO.Directory.CreateDirectory(output);
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1120, 700)))
        {
            bitmap.Render(preview);
            bitmap.Save(System.IO.Path.Combine(output, "registry-list-tree.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        preview.Close();
        var layout = first.Children.Single();
        await vm.LoadChildrenAsync(layout);
        var panes = layout.Children.Single();
        await vm.LoadChildrenAsync(panes);
        vm.SelectedKey = panes.Children.Single();
        await Until(() => vm.Entries.Any(row => row.Name == "size"));
        var nestedRow = vm.Entries.Single(row => row.Name == "size");
        var nestedRequest = RegistryJsonProjection.Write(nestedRow, JsonSerializer.SerializeToElement(300));
        Check(nestedRequest.Value[0].GetProperty("layout").GetProperty("panes")[0].GetProperty("size").GetInt32() == 300
            && nestedRequest.Value[0].GetProperty("width").GetInt32() == 1280, "Nested array edits must preserve the rest of the same document.");
        Check(!RegistryJsonProjection.Row(document with { Schema = document.Schema with { CanWrite = false } }, "width", ["0", "width"]).Source.Schema.CanWrite,
            "JSON properties must preserve the server's read-only capability.");
        Check(vm.NavigationPathInput.EndsWith("\\windows\\[0]\\layout\\panes\\[0]"), "JSON addresses must use indexes rather than display names.");
        vm.NavigationPathInput = "HKCU\\Workspace\\windows\\[2]";
        await vm.NavigateCommand.ExecuteAsync(null);
        await Until(() => vm.Entries.Count == 1 && vm.Entries[0].DesiredValue == "bash");
        Check(vm.Entries[0].JsonPath.SequenceEqual(new[] { "2" }), "Primitive array elements must remain editable by index.");
        client.Values = [document with { DesiredValue = JsonSerializer.Deserialize<JsonElement>("""[{"name":"更新"},1,"zsh"]""") }];
        await vm.RefreshAsync();
        Check(vm.Entries.Single().DesiredValue == "zsh", "Refresh must resolve the selected index against the latest document.");
        await vm.LoadChildrenAsync(list);
        Check(list.Children.Count == 3 && list.Children[0].Name == "[0] 更新", "Refresh must rebuild labels and remove old array elements.");
        client.Values = [document with { DesiredValue = JsonSerializer.Deserialize<JsonElement>("[]") }];
        await vm.RefreshAsync();
        Check(vm.PaneState == RegistryPaneState.Failed && vm.Entries.Count == 0, "A removed index must report a read failure rather than stale data or an empty key.");
        await vm.LoadChildrenAsync(list);
        Check(list.Children.Count == 0, "An empty array must remove stale item nodes.");
        client.Values = null;
        Console.WriteLine("PASS: lazy main-tree list navigation, labels, nested property writes, owner/revision preservation and refresh.");
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
            new RegistryValueDialogView { DataContext = new RegistryValueDialogViewModel(RegistryEntryRow.From(
                CustomEntry(client, "document", RegistryValueType.Json, JsonSerializer.Deserialize<JsonElement>("""{"appearance":{"fontSize":14,"colors":{"background":"#101828"}},"shells":["pwsh","bash"],"enabled":true}"""),
                    RegistryEditorKind.Json, null)), owned, _ => { }, _ => { }) },
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
            if (surface.DataContext is RegistryValueDialogViewModel { IsStructuredJson: true } json)
            {
                var appearance = json.JsonRoots.Single().Children.Single(node => node.Name == "appearance");
                appearance.IsExpanded = true;
                appearance.Children.Single(node => node.Name == "colors").IsExpanded = true;
                json.SelectedJsonNode = appearance.Children.Single(node => node.Name == "fontSize");
            }
            var numeric = surface.DataContext is RegistryValueDialogViewModel { ValueType: RegistryValueType.Number };
            var window = new Window { Width = numeric ? 600 : 900, Height = numeric ? 320 : 620, Content = surface };
            window.Show();
            if (numeric || surface is RegistryView || surface.DataContext is RegistryValueDialogViewModel { IsStructuredJson: true })
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var output = System.IO.Path.Combine(AppContext.BaseDirectory, "layout-qa");
                System.IO.Directory.CreateDirectory(output);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(numeric ? 600 : 900, numeric ? 320 : 620));
                bitmap.Render(window);
                bitmap.Save(System.IO.Path.Combine(output, numeric ? "registry-number.png" : surface is RegistryView ? "registry-browser.png" : "registry-json.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
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
        public IReadOnlyList<RegistryEntryDto>? Values { get; set; }
        public Guid? SavedOwner { get; private set; }
        public PutRegistryEntryRequest? SavedRequest { get; private set; }
        public Guid? RenameOwner { get; private set; }
        public RenameRegistryEntryRequest? RenameRequest { get; private set; }
        public FakeRegistryClient() { }
        private FakeRegistryClient(FakeRegistryClient root, Guid target) { _root = root; _target = target; }
        public IRegistryClient ForServer(Guid instanceId) => throw new NotSupportedException();
        public IRegistryClient ForUser(Guid targetUserId) => new FakeRegistryClient(Root, targetUserId);
        public Task<RegistryContextDto> GetContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RegistryContextDto("server", Root.Owner, "nanami", Root.Workspace, true, null, Guid.NewGuid()));
        public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RegistryUserDto>>([
            new(Root.Owner, "nanami", true, Root.Workspace), new(Root.Other, "alice", false, Root.OtherWorkspace)]);
        public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RegistryKeyDto>>([]);
        public Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) =>
            Root.Failure is { } failure ? Task.FromException<IReadOnlyList<RegistryEntryDto>>(failure)
            : Root.Pending ?? Task.FromResult<IReadOnlyList<RegistryEntryDto>>(Root.Values ?? [Root.Value(_target, _target == Root.Owner ? "nanami" : "alice")]);
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
