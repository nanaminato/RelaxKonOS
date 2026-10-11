using System.Collections.ObjectModel;
using System.Net;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>Window-local host firewall editor state; host authorization belongs to the shared broker.</summary>
public sealed partial class FirewallViewModel : ObservableObject
{
    private bool _windowsBackend;
    private readonly IRemoteFirewallClient _client;
    private readonly IAppPermissionScope _permissions;

    public FirewallViewModel(IRemoteFirewallClient client, IAppPermissionScope permissions)
    {
        _client = client;
        _permissions = permissions;
        Policies = [Option("allow", "firewall.choice.allow"), Option("deny", "firewall.choice.deny"), Option("reject", "firewall.choice.reject")];
        Actions = [.. Policies, Option("limit", "firewall.choice.limit")];
        Directions = [Option("in", "firewall.choice.in"), Option("out", "firewall.choice.out")];
        Protocols = [Option("tcp", "firewall.choice.tcp"), Option("udp", "firewall.choice.udp"), Option("any", "firewall.choice.any")];
        SelectedIncomingPolicy = Policies[1];
        SelectedOutgoingPolicy = Policies[0];
        SelectedAction = Actions[0];
        SelectedDirection = Directions[0];
        SelectedProtocol = Protocols[0];
    }

    public ObservableCollection<FirewallRuleDto> Rules { get; } = [];
    public ObservableCollection<FirewallOption> Policies { get; }
    public ObservableCollection<FirewallOption> Actions { get; }
    public IReadOnlyList<FirewallOption> Directions { get; }
    public IReadOnlyList<FirewallOption> Protocols { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand))]
    private FirewallRuleDto? _selectedRule;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand), nameof(SaveDefaultsCommand), nameof(ShowAddRuleEditorCommand), nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand), nameof(ClearEditorCommand))]
    private bool _isAvailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))]
    private bool _isEnabled;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(EnableCommand), nameof(DisableCommand), nameof(SaveDefaultsCommand), nameof(ShowAddRuleEditorCommand), nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand), nameof(ClearEditorCommand))]
    private bool _isLoading;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveDefaultsCommand))] private FirewallOption? _selectedIncomingPolicy;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveDefaultsCommand))] private FirewallOption? _selectedOutgoingPolicy;
    [ObservableProperty] private FirewallOption? _selectedAction;
    [ObservableProperty] private FirewallOption? _selectedDirection;
    [ObservableProperty] private FirewallOption? _selectedProtocol;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private string _port = string.Empty;

    /// <summary>Provided by the window to surface unavailable privileged operations prominently.</summary>
    public Func<string?, Task>? ShowPrivilegedHelperUnavailableAsync { get; set; }
    /// <summary>Provided by the window because editing is rendered in a window-owned modal dialog.</summary>
    public Func<bool, Task>? ShowRuleEditorAsync { get; set; }

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            Rules.Clear();
            SelectedRule = null;
            IsAvailable = false;
            IsEnabled = false;
            StatusText = LocalizedText.Ref("firewall.permission.read_required");
            return;
        }

        IsLoading = true;
        try
        {
            var status = await _client.GetStatusAsync();
            Rules.Clear();
            SelectedRule = null;
            _windowsBackend = status.Backend == "windows-defender";
            IsAvailable = status.IsAvailable;
            IsEnabled = status.IsEnabled;
            if (!status.IsAvailable)
            {
                StatusText = LocalizedText.Ref("firewall.status.unavailable", ProblemText(status.ProblemCode));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
                return;
            }

            Policies.Clear();
            Policies.Add(Option("allow", "firewall.choice.allow"));
            Policies.Add(Option("deny", "firewall.choice.deny"));
            Actions.Clear();
            foreach (var policy in Policies) Actions.Add(policy);
            if (status.Backend != "windows-defender")
            {
                Policies.Add(Option("reject", "firewall.choice.reject"));
                Actions.Add(Option("reject", "firewall.choice.reject"));
                Actions.Add(Option("limit", "firewall.choice.limit"));
            }
            SelectedIncomingPolicy = _windowsBackend && status.DefaultIncomingPolicy is null ? null : Find(Policies, status.DefaultIncomingPolicy, "deny");
            SelectedOutgoingPolicy = _windowsBackend && status.DefaultOutgoingPolicy is null ? null : Find(Policies, status.DefaultOutgoingPolicy, "allow");
            foreach (var rule in await _client.ListRulesAsync()) Rules.Add(rule);
            StatusText = _windowsBackend ? LocalizedText.Ref("firewall.status.windows_ready") : LocalizedText.Ref(status.IsEnabled ? "firewall.status.ready_enabled" : "firewall.status.ready_disabled", status.Backend, status.Version ?? "");
        }
        catch (Exception exception)
        {
            Rules.Clear();
            SelectedRule = null;
            IsAvailable = false;
            IsEnabled = false;
            StatusText = LocalizedText.Ref("firewall.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableAsync() => ApplyAsync(() => _client.SetEnabledAsync(new UpdateFirewallEnabledRequest(true)));

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync() => ApplyAsync(() => _client.SetEnabledAsync(new UpdateFirewallEnabledRequest(false)));

    [RelayCommand(CanExecute = nameof(CanSaveDefaults))]
    private Task SaveDefaultsAsync() => ApplyAsync(() => _client.SetDefaultsAsync(new UpdateFirewallDefaultsRequest(
        SelectedIncomingPolicy?.Value ?? "deny", SelectedOutgoingPolicy?.Value ?? "allow")));

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ShowAddRuleEditorAsync()
    {
        ClearEditor();
        if (ShowRuleEditorAsync is not null) await ShowRuleEditorAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateRule))]
    private async Task ShowEditRuleEditorAsync()
    {
        if (SelectedRule is null) return;
        LoadRuleIntoEditor(SelectedRule);
        if (ShowRuleEditorAsync is not null) await ShowRuleEditorAsync(true);
    }

    public async Task<bool> AddRuleAsync()
    {
        if (!CanManage) return false;
        if (!TryBuildRule(out var rule)) return false;
        var success = await ApplyAsync(() => _client.CreateRuleAsync(rule));
        if (success) ClearEditor();
        return success;
    }

    public async Task<bool> UpdateRuleAsync()
    {
        if (!CanManage) return false;
        var target = SelectedRule;
        if (SelectedRule is null || !TryBuildRule(out var rule)) return false;
        var success = await ApplyAsync(() => _client.UpdateRuleAsync(target!.Number,
            new UpdateFirewallRuleRequest(rule.Action, rule.Direction, rule.Protocol, rule.Source, rule.Destination, rule.Port)));
        if (success) ClearEditor();
        return success;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteRule))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null) return;
        if (await ApplyAsync(() => _client.DeleteRuleAsync(SelectedRule.Number))) ClearEditor();
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void ClearEditor()
    {
        SelectedRule = null;
        SelectedAction = Actions[0];
        SelectedDirection = Directions[0];
        SelectedProtocol = Protocols[0];
        Source = string.Empty;
        Destination = string.Empty;
        Port = string.Empty;
    }

    partial void OnSelectedRuleChanged(FirewallRuleDto? value)
    {
        if (value is null) return;
        LoadRuleIntoEditor(value);
    }

    private void LoadRuleIntoEditor(FirewallRuleDto value)
    {
        SelectedAction = Find(Actions, value.Action, "allow");
        SelectedDirection = Find(Directions, value.Direction, "in");
        SelectedProtocol = Find(Protocols, value.Protocol, "any");
        Source = value.Source == "any" ? string.Empty : value.Source;
        Destination = value.Destination == "any" ? string.Empty : value.Destination;
        Port = value.Port == "any" ? string.Empty : value.Port;
    }

    private bool TryBuildRule(out CreateFirewallRuleRequest rule)
    {
        var port = Port.Trim();
        if (!IsEndpoint(Source) || !IsEndpoint(Destination))
        {
            StatusText = LocalizedText.Ref("firewall.validation.address_invalid");
            rule = default!;
            return false;
        }
        if (!string.IsNullOrEmpty(port) && !IsPort(port))
        {
            StatusText = LocalizedText.Ref("firewall.validation.port_invalid");
            rule = default!;
            return false;
        }

        rule = new CreateFirewallRuleRequest(SelectedAction?.Value ?? "allow", SelectedDirection?.Value ?? "in", SelectedProtocol?.Value ?? "tcp",
            NormalizeEndpoint(Source), NormalizeEndpoint(Destination), string.IsNullOrEmpty(port) ? "any" : port);
        return true;
    }

    private async Task<bool> ApplyAsync(Func<Task<FirewallOperationResult>> operation)
    {
        if (IsLoading || !IsAvailable) return false;
        // CanExecute only controls the UI. Check again here so invoking a command directly
        // can never turn a read-only firewall grant into a host configuration change.
        if (!HasManagePermission)
        {
            StatusText = LocalizedText.Ref("firewall.permission.manage_required");
            return false;
        }

        IsLoading = true;
        var success = false;
        try
        {
            var result = await operation();
            StatusText = result.Success ? LocalizedText.Ref("firewall.operation.succeeded") : LocalizedText.Ref("firewall.operation.failed", ProblemText(result.ProblemCode));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            success = result.Success;
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Ref("firewall.operation.failed", exception.Message);
        }
        finally { IsLoading = false; }
        // A successful change is immediately re-read from UFW so button state and
        // rule numbers always reflect the host rather than optimistic local state.
        if (success) await RefreshAsync();
        return success;
    }

    private static string ProblemText(string? problemCode) =>
        PrivilegedHelperProblemText.FormatOrFallback(problemCode, "unknown error");

    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        PrivilegedHelperProblemText.TryFormat(problemCode, out _)
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;

    private bool HasReadPermission => _permissions.IsGranted(AppPermissions.ServerFirewallRead);
    private bool HasManagePermission => HasReadPermission && _permissions.IsGranted(AppPermissions.ServerFirewallManage);
    private bool CanRefresh => HasReadPermission && !IsLoading;
    private bool CanManage => HasManagePermission && IsAvailable && !IsLoading;
    private bool CanSaveDefaults => CanManage && SelectedIncomingPolicy is not null && SelectedOutgoingPolicy is not null;
    private bool CanEnable => CanManage && (_windowsBackend || !IsEnabled);
    private bool CanDisable => CanManage && (_windowsBackend || IsEnabled);
    private bool CanUpdateRule => CanManage && SelectedRule is not null;
    private bool CanDeleteRule => CanManage && SelectedRule is not null;

    private static FirewallOption Option(string value, string labelKey) => new(value, LocalizedText.Get(labelKey));
    private static FirewallOption Find(IEnumerable<FirewallOption> options, string? value, string fallback) =>
        options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
        ?? options.First(option => option.Value == fallback);
    private static string NormalizeEndpoint(string value) => string.IsNullOrWhiteSpace(value) ? "any" : value.Trim();
    private static bool IsEndpoint(string value)
    {
        var normalized = NormalizeEndpoint(value);
        if (normalized.Equals("any", StringComparison.OrdinalIgnoreCase) || normalized.Equals("anywhere", StringComparison.OrdinalIgnoreCase)) return true;
        var slash = normalized.IndexOf('/');
        var address = slash < 0 ? normalized : normalized[..slash];
        if (!IPAddress.TryParse(address, out var parsed)) return false;
        return slash < 0 || int.TryParse(normalized[(slash + 1)..], out var prefix) && prefix >= 0 && prefix <= (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32);
    }
    private static bool IsPort(string value)
    {
        var parts = value.Split(':');
        return parts.Length is 1 or 2 && parts.All(part => int.TryParse(part, out var port) && port is > 0 and <= 65535)
            && (parts.Length == 1 || int.Parse(parts[0]) <= int.Parse(parts[1]));
    }
}

public sealed record FirewallOption(string Value, string Label);
