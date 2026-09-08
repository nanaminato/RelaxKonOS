using System.Collections.ObjectModel;
using System.Net;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>Window-local Linux UFW editor state. Passwords are requested one-shot and never retained by the view model.</summary>
public sealed partial class FirewallViewModel : ObservableObject
{
    private readonly IRemoteFirewallClient _client;
    private readonly IAuthSession _session;
    private readonly IAppPermissionScope _permissions;

    public FirewallViewModel(IRemoteFirewallClient client, IAuthSession session, IAppPermissionScope permissions)
    {
        _client = client;
        _session = session;
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
    public IReadOnlyList<FirewallOption> Policies { get; }
    public IReadOnlyList<FirewallOption> Actions { get; }
    public IReadOnlyList<FirewallOption> Directions { get; }
    public IReadOnlyList<FirewallOption> Protocols { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand))]
    private FirewallRuleDto? _selectedRule;
    [ObservableProperty] private string _statusText = LocalizedText.Get("firewall.status.loading");
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand), nameof(SaveDefaultsCommand), nameof(ShowAddRuleEditorCommand), nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand), nameof(ClearEditorCommand))]
    private bool _isAvailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))]
    private bool _isEnabled;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(EnableCommand), nameof(DisableCommand), nameof(SaveDefaultsCommand), nameof(ShowAddRuleEditorCommand), nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand), nameof(ClearEditorCommand))]
    private bool _isLoading;
    [ObservableProperty] private FirewallOption? _selectedIncomingPolicy;
    [ObservableProperty] private FirewallOption? _selectedOutgoingPolicy;
    [ObservableProperty] private FirewallOption? _selectedAction;
    [ObservableProperty] private FirewallOption? _selectedDirection;
    [ObservableProperty] private FirewallOption? _selectedProtocol;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private string _port = string.Empty;

    public bool IsRoot => string.Equals(_session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    /// <summary>Provided by the window so a credential is collected only for the pending operation.</summary>
    public Func<Task<string?>>? RequestPasswordAsync { get; set; }
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
            StatusText = LocalizedText.Get("firewall.permission.read_required");
            return;
        }

        IsLoading = true;
        try
        {
            var status = await _client.GetStatusAsync();
            Rules.Clear();
            SelectedRule = null;
            IsAvailable = status.IsAvailable;
            IsEnabled = status.IsEnabled;
            if (!status.IsAvailable)
            {
                StatusText = LocalizedText.Format("firewall.status.unavailable", ProblemText(status.ProblemCode));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
                return;
            }

            SelectedIncomingPolicy = Find(Policies, status.DefaultIncomingPolicy, "deny");
            SelectedOutgoingPolicy = Find(Policies, status.DefaultOutgoingPolicy, "allow");
            foreach (var rule in await _client.ListRulesAsync()) Rules.Add(rule);
            StatusText = LocalizedText.Format(status.IsEnabled ? "firewall.status.ready_enabled" : "firewall.status.ready_disabled", status.Backend, status.Version ?? "");
        }
        catch (Exception exception)
        {
            Rules.Clear();
            SelectedRule = null;
            IsAvailable = false;
            IsEnabled = false;
            StatusText = LocalizedText.Format("firewall.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableAsync() => ApplyAsync(confirmation => _client.SetEnabledAsync(new UpdateFirewallEnabledRequest(true, confirmation)));

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync() => ApplyAsync(confirmation => _client.SetEnabledAsync(new UpdateFirewallEnabledRequest(false, confirmation)));

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task SaveDefaultsAsync() => ApplyAsync(confirmation => _client.SetDefaultsAsync(new UpdateFirewallDefaultsRequest(
        SelectedIncomingPolicy?.Value ?? "deny", SelectedOutgoingPolicy?.Value ?? "allow", confirmation)));

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
        if (!TryBuildRule(out var rule)) return false;
        var success = await ApplyAsync(confirmation => _client.CreateRuleAsync(rule with { CredentialConfirmation = confirmation }));
        if (success) ClearEditor();
        return success;
    }

    public async Task<bool> UpdateRuleAsync()
    {
        if (SelectedRule is null || !TryBuildRule(out var rule)) return false;
        var success = await ApplyAsync(confirmation => _client.UpdateRuleAsync(SelectedRule.Number,
            new UpdateFirewallRuleRequest(rule.Action, rule.Direction, rule.Protocol, rule.Source, rule.Destination, rule.Port, confirmation)));
        if (success) ClearEditor();
        return success;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteRule))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is null) return;
        if (await ApplyAsync(confirmation => _client.DeleteRuleAsync(SelectedRule.Number, new DeleteFirewallRuleRequest(confirmation)))) ClearEditor();
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
            StatusText = LocalizedText.Get("firewall.validation.address_invalid");
            rule = default!;
            return false;
        }
        if (!string.IsNullOrEmpty(port) && !IsPort(port))
        {
            StatusText = LocalizedText.Get("firewall.validation.port_invalid");
            rule = default!;
            return false;
        }

        rule = new CreateFirewallRuleRequest(SelectedAction?.Value ?? "allow", SelectedDirection?.Value ?? "in", SelectedProtocol?.Value ?? "tcp",
            NormalizeEndpoint(Source), NormalizeEndpoint(Destination), string.IsNullOrEmpty(port) ? "any" : port, null);
        return true;
    }

    private async Task<bool> ApplyAsync(Func<FirewallCredentialConfirmation?, Task<FirewallOperationResult>> operation)
    {
        // CanExecute only controls the UI. Check again here so invoking a command directly
        // can never turn a read-only firewall grant into a host configuration change.
        if (!HasManagePermission)
        {
            StatusText = LocalizedText.Get("firewall.permission.manage_required");
            return false;
        }

        FirewallCredentialConfirmation? confirmation = null;
        if (!IsRoot)
        {
            var password = await (RequestPasswordAsync?.Invoke() ?? Task.FromResult<string?>(null));
            if (password is null) return false;
            confirmation = new FirewallCredentialConfirmation(password);
        }

        IsLoading = true;
        var success = false;
        try
        {
            var result = await operation(confirmation);
            StatusText = result.Success ? LocalizedText.Get("firewall.operation.succeeded") : LocalizedText.Format("firewall.operation.failed", ProblemText(result.ProblemCode));
            if (!result.Success) await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            success = result.Success;
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("firewall.operation.failed", exception.Message);
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
    private bool CanEnable => CanManage && !IsEnabled;
    private bool CanDisable => CanManage && IsEnabled;
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
