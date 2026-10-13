using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;

internal static class PresentationPreferencesChecks
{
    public static async Task RunAsync()
    {
        var owner = Guid.NewGuid(); var workspace = Guid.NewGuid(); var firstDevice = Guid.NewGuid();
        var session = DispatchProxy.Create<IAuthSession, PresentationSessionProxy>();
        var state = (PresentationSessionProxy)(object)session;
        state.Values["State"] = AuthSessionState.Authenticated;
        state.Values["CurrentUser"] = new UserDto(owner, "owner", HostPlatformKind.Windows, "owner", DateTimeOffset.UtcNow, null);
        state.Values["CurrentWorkspace"] = new WorkspaceDto(workspace, owner, "Workspace", WorkspaceState.Running, DateTimeOffset.UtcNow, null);
        state.Values["CurrentDevice"] = new DeviceDto(firstDevice, "first", "windows", "test", null);
        state.Values["EffectiveBaseUrl"] = "https://example.test";
        var changes = new PresentationSettingsChanges();
        var client = new Client(owner, workspace, state, changes);
        using var preferences = new ClientPresentationPreferences(client, session, changes);
        await preferences.RefreshAsync();
        Check(preferences.Registry == RegistryAppearanceSettings.Default && preferences.TerminalPadding == TerminalPaddingSettings.Default,
            "Client presentation defaults were not read from device scope.");
        var vm = new RegistryViewModel(client, preferences);
        await vm.RefreshAsync();
        Check(vm.Keys.Single().Children.Single(node => node.Kind == RegistryKeyKind.CurrentUser).Children.Any(node => node.Scope == RegistryScope.Device),
            "The current client device must appear in HKCU.");
        var view = new RegistryView { DataContext = vm };
        var window = new Window { Width = 900, Height = 550, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var fields = await client.ListValuesAsync(RegistryScope.Device, ClientPresentationPaths.Registry);
        async Task Save(string name, int value)
        {
            var field = (await client.ListValuesAsync(RegistryScope.Device, ClientPresentationPaths.Registry)).Single(entry => entry.Name == name);
            await client.SaveAsync(new(field.Scope, field.Path, field.Name, field.ValueType, JsonSerializer.SerializeToElement(value), field.Revision));
        }
        await Save("scrollbarThickness", 9);
        await Save("treeRowHeight", 32);
        await Save("iconSize", 20);
        await Save("valueRowHeight", 34);
        await preferences.SaveSidebarWidthAsync(360);
        Dispatcher.UIThread.RunJobs();
        Check(preferences.Registry.ScrollbarThickness == 9 && vm.Appearance.IconSize == 20 && (double)view.Resources["RegistryTreeRowHeight"]! == 32,
            "Successful own-device writes must immediately change the displayed resources.");
        Check(view.FindControl<Grid>("RegistryPanes")!.ColumnDefinitions[0].Width.Value == 360,
            "The sidebar width must use the same preference as registry editing.");
        var source = fields.Single(entry => entry.Name == "scrollbarThickness") with { DesiredValue = JsonSerializer.SerializeToElement(4) };
        changes.Publish(source with { Address = source.Address with { OwnerUserId = Guid.NewGuid() } });
        changes.Publish(source with { Address = source.Address with { ScopeId = Guid.NewGuid() } });
        changes.Publish(source with { Address = source.Address with { ServerId = "https://other.test" } });
        Check(preferences.Registry.ScrollbarThickness == 9, "Another owner, device or server changed the current view.");
        state.Values["CurrentDevice"] = new DeviceDto(Guid.NewGuid(), "second", "windows", "test", null);
        await preferences.RefreshAsync();
        Check(preferences.Registry == RegistryAppearanceSettings.Default, "A second client device inherited the first device's geometry.");
        state.Values["CurrentDevice"] = new DeviceDto(firstDevice, "first", "windows", "test", null);
        await preferences.RefreshAsync();
        Check(preferences.Registry.SidebarWidth == 360 && preferences.Registry.ScrollbarThickness == 9, "Returning to the first device lost persisted geometry.");
        var restore = (await client.ListValuesAsync(RegistryScope.Device, ClientPresentationPaths.Registry)).Single(entry => entry.Name == "scrollbarThickness");
        await client.SaveAsync(new(restore.Scope, restore.Path, restore.Name, restore.ValueType, restore.Schema.DefaultValue!.Value, restore.Revision));
        Check(preferences.Registry.ScrollbarThickness == 6 && preferences.Registry.IconSize == 20, "Restore default must preserve other appearance fields.");
        var lastValid = preferences.Registry;
        client.FailReads = true;
        try { await preferences.RefreshAsync(); throw new InvalidOperationException("Read failure was hidden."); }
        catch (System.Net.Http.HttpRequestException) { }
        Check(preferences.Registry == lastValid, "A disconnected read must preserve the last valid appearance.");
        client.FailReads = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PendingRead = gate.Task;
        var delayed = preferences.RefreshAsync();
        client.PendingRead = null;
        await Save("scrollbarThickness", 8);
        gate.SetResult();
        await delayed;
        Check(preferences.Registry.ScrollbarThickness == 8, "A delayed read overwrote a newer successful write notification.");
        var terminal = TerminalSettingsDto.Default with { ScrollbackLines = 0, CursorStyle = "bar", CursorBlink = false };
        changes.Publish("https://example.test", workspace, terminal);
        Check(preferences.Terminal == terminal, "Terminal settings writes must notify other active views.");
        var hkcu = vm.Keys.Single().Children.Single(node => node.Kind == RegistryKeyKind.CurrentUser);
        hkcu.IsExpanded = true;
        var deviceNode = hkcu.Children.Single(node => node.Scope == RegistryScope.Device);
        deviceNode.IsExpanded = true;
        await vm.LoadChildrenAsync(deviceNode);
        var clientNode = deviceNode.Children.Single();
        clientNode.IsExpanded = true;
        await vm.LoadChildrenAsync(clientNode);
        vm.SelectedKey = clientNode.Children.Single(node => node.Path == ClientPresentationPaths.Registry);
        for (var index = 0; index < 30; index++)
            hkcu.Children.Add(new RegistryKeyNode("A long node for checking horizontal and vertical scrollbars " + index, null, null, hkcu, RegistryKeyKind.Root));
        Dispatcher.UIThread.RunJobs();
        var tree = view.FindControl<TreeView>("RegistryTree")!;
        var bars = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tree).OfType<Avalonia.Controls.Primitives.ScrollBar>().ToArray();
        Check(bars.Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Vertical).Bounds.Width == 8
            && bars.Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal).Bounds.Height == 8,
            "Changing scrollbarThickness must resize both visible scrollbar tracks.");
        Check(vm.Entries.Count == 5 && vm.SelectedKey.Address!.ScopeId == firstDevice, "Device settings must be navigable as managed fields.");
        var output = Path.Combine(AppContext.BaseDirectory, "layout-qa"); Directory.CreateDirectory(output);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 550));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, "registry-custom-appearance.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        using (var appearance = new RelaxKonOS.Client.Services.Theming.AppearanceService(Application.Current!, new RelaxKonOS.Client.Services.Theming.SystemStyleRegistry()))
        {
            appearance.Apply(RelaxKonOS.Protocol.Desktop.ThemeKind.Dark, AppearancePreferencesDto.Default, null);
            Dispatcher.UIThread.RunJobs();
            using var dark = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 550));
            dark.Render(window); dark.Save(Path.Combine(output, "registry-custom-appearance-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            appearance.Apply(RelaxKonOS.Protocol.Desktop.ThemeKind.Light, AppearancePreferencesDto.Default, null);
        }
        window.Close();
        Console.WriteLine("PASS: client-device settings, immediate appearance updates, sidebar persistence, reset and foreign-context isolation.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Client(Guid owner, Guid workspace, PresentationSessionProxy state, PresentationSettingsChanges changes) : IRegistryClient
    {
        private readonly Dictionary<(Guid, string), (JsonObject Value, long Revision)> _documents = [];
        public bool FailReads;
        public Task? PendingRead;
        private Guid Device => ((DeviceDto)state.Values["CurrentDevice"]!).Id;
        public IRegistryClient ForUser(Guid id) { Check(id == owner, "Wrong settings owner."); return this; }
        public Task<RegistryContextDto> GetContextAsync(CancellationToken ct = default) => Task.FromResult(new RegistryContextDto("https://example.test", owner, "owner", workspace, false, Device));
        public async Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken ct = default)
        {
            if (path is not (ClientPresentationPaths.Registry or ClientPresentationPaths.Terminal or "Workspace\\Terminal")) return [];
            var id = scope == RegistryScope.Device ? Device : workspace;
            var key = (id, path);
            object defaults = path switch { ClientPresentationPaths.Registry => RegistryAppearanceSettings.Default, ClientPresentationPaths.Terminal => TerminalPaddingSettings.Default, _ => TerminalSettingsDto.Default };
            if (!_documents.TryGetValue(key, out var doc)) _documents[key] = doc = ((JsonObject)JsonSerializer.SerializeToNode(defaults, RelaxKonOSJsonOptions.Default)!, 1);
            var defaultFields = (JsonObject)JsonSerializer.SerializeToNode(defaults, RelaxKonOSJsonOptions.Default)!;
            var result = doc.Value.Select(pair =>
            {
                var value = JsonSerializer.SerializeToElement(pair.Value);
                var type = value.ValueKind switch { JsonValueKind.String => RegistryValueType.String, JsonValueKind.Number => RegistryValueType.Number, JsonValueKind.True or JsonValueKind.False => RegistryValueType.Boolean, _ => RegistryValueType.Json };
                return new RegistryEntryDto(scope, path, pair.Key, type, value, doc.Revision, RegistryEntryState.Synced, DateTimeOffset.UtcNow, null, RegistryApplyMode.Immediate, null, null, null)
                { Address = new("https://example.test", owner, scope, id, path), Schema = new(pair.Key, true, false, false, JsonSerializer.SerializeToElement(defaultFields[pair.Key])) };
            }).ToArray();
            if (PendingRead is { } pending) await pending;
            if (FailReads) throw new System.Net.Http.HttpRequestException("disconnected");
            return result;
        }
        public async Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken ct = default)
        {
            var key = (request.Scope == RegistryScope.Device ? Device : workspace, request.Path);
            var doc = _documents[key];
            Check(doc.Revision == request.ExpectedRevision, "Sidebar must use the current revision.");
            doc.Value[request.Name] = JsonNode.Parse(request.Value.GetRawText());
            _documents[key] = (doc.Value, doc.Revision + 1);
            var entry = (await ListValuesAsync(request.Scope, request.Path)).Single(field => field.Name == request.Name);
            changes.Publish(entry); return entry;
        }
        public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string path, CancellationToken ct = default)
        {
            var paths = scope == RegistryScope.Device ? path switch
            {
                "Device" => new[] { "Device\\Client" },
                "Device\\Client" => new[] { ClientPresentationPaths.Registry, ClientPresentationPaths.Terminal },
                _ => Array.Empty<string>(),
            } : Array.Empty<string>();
            return Task.FromResult<IReadOnlyList<RegistryKeyDto>>(paths.Select(child => new RegistryKeyDto(scope, child)
            { Address = new("https://example.test", owner, scope, Device, child) }).ToArray());
        }
        public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RegistryUserDto>>([]);
        public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RegistryEntryDto> RenameAsync(RenameRegistryEntryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

public class PresentationSessionProxy : DispatchProxy
{
    public Dictionary<string, object?> Values { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name.StartsWith("get_", StringComparison.Ordinal)
        ? Values.GetValueOrDefault(method.Name[4..]) : method.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)
        ? null : throw new NotSupportedException(method.Name);
}
