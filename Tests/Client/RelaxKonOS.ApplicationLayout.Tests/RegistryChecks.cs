using System.Text.Json;
using RelaxKonOS.Client.Apps.Registry;
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
        Console.WriteLine("PASS: registry aliases, stable-owner navigation, late-error isolation, fixed dialogs and typed default editing.");
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
        public Task<IReadOnlyList<RegistryEntryDto>>? Pending { get; set; }
        public Guid? SavedOwner { get; private set; }
        public PutRegistryEntryRequest? SavedRequest { get; private set; }
        public FakeRegistryClient() { }
        private FakeRegistryClient(FakeRegistryClient root, Guid target) { _root = root; _target = target; }
        public IRegistryClient ForUser(Guid targetUserId) => new FakeRegistryClient(Root, targetUserId);
        public Task<RegistryContextDto> GetContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RegistryContextDto("server", Root.Owner, "nanami", Root.Workspace, true));
        public Task<IReadOnlyList<RegistryUserDto>> ListUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RegistryUserDto>>([
            new(Root.Owner, "nanami", true, Root.Workspace), new(Root.Other, "alice", false, Root.OtherWorkspace)]);
        public Task<IReadOnlyList<RegistryKeyDto>> ListKeysAsync(RegistryScope scope, string parentPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RegistryKeyDto>>([]);
        public Task<IReadOnlyList<RegistryEntryDto>> ListValuesAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) => Root.Pending ?? Task.FromResult<IReadOnlyList<RegistryEntryDto>>([Root.Value(_target, _target == Root.Owner ? "nanami" : "alice")]);
        public RegistryEntryDto Value(Guid owner, string text) => new(RegistryScope.Workspace, "Workspace", "field", RegistryValueType.String,
            JsonSerializer.SerializeToElement(text), 1, RegistryEntryState.Synced, DateTimeOffset.UtcNow, null, RegistryApplyMode.Immediate, null, null, null)
        {
            Address = new("server", owner, RegistryScope.Workspace, owner == Owner ? Workspace : OtherWorkspace, "Workspace"),
            Schema = new("field", true, false, false, JsonSerializer.SerializeToElement("default")),
        };
        public Task<RegistryEntryDto> SaveAsync(PutRegistryEntryRequest request, CancellationToken cancellationToken = default)
        {
            Root.SavedOwner = _target; Root.SavedRequest = request;
            return Task.FromResult(Root.Value(_target, "saved") with { DesiredValue = request.Value, ValueType = request.ValueType });
        }
        public Task<RegistryKeyDto> CreateKeyAsync(CreateRegistryKeyRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteKeyAsync(RegistryScope scope, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(RegistryScope scope, string path, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
