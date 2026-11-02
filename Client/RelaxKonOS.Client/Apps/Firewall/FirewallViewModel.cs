using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>
/// Window-local host firewall editor. The host remains the only source of truth: every accepted
/// write is followed by a re-read, and a refused write keeps the user's draft instead of guessing
/// at optimistic state.
/// </summary>
public sealed partial class FirewallViewModel : LocalizedObservableObject
{
    private readonly IRemoteFirewallClient _client;
    private readonly IAppPermissionScope _permissions;
    private readonly List<FirewallRuleRow> _allRules = [];
    private readonly IReadOnlyList<FirewallOption> _directions;
    private readonly IReadOnlyList<FirewallOption> _protocols;
    private bool _windowsBackend;
    private string _backend = string.Empty;
    private string? _version;
    private string? _hostIncoming;
    private string? _hostOutgoing;

    public FirewallViewModel(IRemoteFirewallClient client, IAppPermissionScope permissions)
    {
        _client = client;
        _permissions = permissions;
        _directions = [new FirewallOption("in", "firewall.choice.in"), new FirewallOption("out", "firewall.choice.out")];
        _protocols =
        [
            new FirewallOption("tcp", "firewall.choice.tcp"),
            new FirewallOption("udp", "firewall.choice.udp"),
            new FirewallOption("any", "firewall.choice.any"),
        ];
        SyncOptions(false);
        // The default-policy editor starts empty: a host that cannot report its policies must not
        // read as if the user had already changed them.
        SelectedAction = Pick(Actions, "allow");
        SelectedDirection = _directions[0];
        SelectedProtocol = _protocols[0];
        StatusText = LocalizedText.Ref("firewall.status.loading");
    }

    public ObservableCollection<FirewallRuleRow> Rules { get; } = [];
    public ObservableCollection<FirewallOption> Policies { get; } = [];
    public ObservableCollection<FirewallOption> Actions { get; } = [];
    public IReadOnlyList<FirewallOption> Directions => _directions;
    public IReadOnlyList<FirewallOption> Protocols => _protocols;

    [ObservableProperty] private FirewallRuleRow? _selectedRow;
    [ObservableProperty] private FirewallRuleDto? _selectedRule;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private LocalizedStatus _unavailableReason;
    [ObservableProperty] private LocalizedStatus _editorMessage;
    [ObservableProperty] private string _ruleFilter = string.Empty;
    [ObservableProperty] private bool _isEditingRule;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(EnableCommand), nameof(DisableCommand), nameof(SaveDefaultsCommand),
        nameof(ShowAddRuleEditorCommand), nameof(ShowEditRuleEditorCommand), nameof(DeleteRuleCommand), nameof(ClearDraftCommand))]
    private bool _isLoading;

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _hasFacts;
    [ObservableProperty] private FirewallOption? _selectedIncomingPolicy;
    [ObservableProperty] private FirewallOption? _selectedOutgoingPolicy;
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
    /// <summary>Provided by the window. A host change is never applied without a decision; when the
    /// window supplies no confirmation (headless checks) the action proceeds.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public async Task StartAsync() => await RefreshAsync();

    // ---- host facts -------------------------------------------------------------------------

    public bool IsWindowsBackend => _windowsBackend;
    public bool IsReadOnly => HasReadPermission && !HasManagePermission;

    public string BackendLabel => _backend switch
    {
        "ufw" => T("firewall.backend.ufw", "UFW"),
        "windows-defender" => T("firewall.backend.windows", "Windows Defender Firewall"),
        "" => T("firewall.value.unknown", "Unknown"),
        _ => _backend,
    };

    public string VersionLabel => string.IsNullOrWhiteSpace(_version) ? T("firewall.value.unknown", "Unknown") : _version;

    public string HostIncomingLabel => PolicyLabel(_hostIncoming);
    public string HostOutgoingLabel => PolicyLabel(_hostOutgoing);

    public string StatePillText =>
        !HasReadPermission ? T("firewall.pill.no_permission", "No access")
        : !HasFacts ? T("firewall.pill.loading", "Reading")
        : !IsAvailable ? T("firewall.pill.unavailable", "Unavailable")
        : IsEnabled ? T("firewall.pill.active", "Active")
        : T("firewall.pill.inactive", "Off");

    public bool StatePillOk => HasReadPermission && HasFacts && IsAvailable && IsEnabled;
    public bool StatePillWarn => HasReadPermission && HasFacts && IsAvailable && !IsEnabled;
    public bool StatePillBad => HasReadPermission && HasFacts && !IsAvailable;
    public bool StatePillNeutral => !StatePillOk && !StatePillWarn && !StatePillBad;

    public bool ShowNoPermission => !HasReadPermission;
    public bool ShowUnavailable => HasReadPermission && HasFacts && !IsAvailable;
    public bool ShowReadOnly => HasFacts && IsAvailable && IsReadOnly;
    public bool ShowWindowsNote => HasFacts && IsAvailable && IsWindowsBackend;

    public bool ShowEnableAction => HasFacts && IsAvailable && (IsWindowsBackend || !IsEnabled);
    public bool ShowDisableAction => HasFacts && IsAvailable && (IsWindowsBackend || IsEnabled);
    public bool HasProtectionActionHint => HasFacts && IsAvailable;
    public string ProtectionActionHint => IsEnabled
        ? T("firewall.protection.disable_hint", "Stopping the firewall makes every rule inactive until it is started again.")
        : T("firewall.protection.enable_hint", "Starting the firewall applies every rule; connections that are not allowed are dropped.");

    public string RuleCountText => Rules.Count == _allRules.Count
        ? LocalizedText.Format("firewall.rules.count", _allRules.Count)
        : LocalizedText.Format("firewall.rules.count_filtered", Rules.Count, _allRules.Count);

    public bool HasRules => _allRules.Count > 0;
    public bool ShowRulesEmpty => HasFacts && IsAvailable && Rules.Count == 0 && _allRules.Count == 0;
    public bool ShowFilterEmpty => Rules.Count == 0 && _allRules.Count > 0;
    public bool HasFilter => RuleFilter.Trim().Length > 0;

    // ---- default policies -------------------------------------------------------------------

    public bool CanEditDefaults => HasFacts && IsAvailable && !IsReadOnly && !IsLoading;

    /// <summary>True when the two selections differ from what the host last reported.</summary>
    public bool HasDefaultChanges =>
        !string.Equals(SelectedIncomingPolicy?.Value, _hostIncoming, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(SelectedOutgoingPolicy?.Value, _hostOutgoing, StringComparison.OrdinalIgnoreCase);

    public string DefaultsSummary
    {
        get
        {
            if (!HasDefaultChanges)
            {
                // "Matches the host" would be a claim about facts the host never reported.
                if (!string.IsNullOrEmpty(_hostIncoming) || !string.IsNullOrEmpty(_hostOutgoing))
                    return T("firewall.defaults.unchanged", "Default policies match the host.");
                return IsWindowsBackend
                    ? T("firewall.value.mixed", "Differs across profiles")
                    : T("firewall.defaults.unknown", "The host did not report its default policies.");
            }

            var parts = new List<string>(2);
            if (!string.Equals(SelectedIncomingPolicy?.Value, _hostIncoming, StringComparison.OrdinalIgnoreCase))
                parts.Add(LocalizedText.Format("firewall.defaults.change_item",
                    T("firewall.default_incoming", "Incoming policy"), PolicyLabel(_hostIncoming), SelectedIncomingPolicy?.Label ?? ""));
            if (!string.Equals(SelectedOutgoingPolicy?.Value, _hostOutgoing, StringComparison.OrdinalIgnoreCase))
                parts.Add(LocalizedText.Format("firewall.defaults.change_item",
                    T("firewall.default_outgoing", "Outgoing policy"), PolicyLabel(_hostOutgoing), SelectedOutgoingPolicy?.Label ?? ""));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Saving a deny default cuts every connection the rules do not allow, including this session.</summary>
    public bool ShowLockoutWarning => HasDefaultChanges
        && (string.Equals(SelectedIncomingPolicy?.Value, "deny", StringComparison.OrdinalIgnoreCase)
            || string.Equals(SelectedOutgoingPolicy?.Value, "deny", StringComparison.OrdinalIgnoreCase));

    // ---- rule draft -------------------------------------------------------------------------

    public string? SourceProblem => EndpointProblem(Source, "firewall.validation.source_invalid",
        "Source must be any or a valid IP/CIDR, such as 192.0.2.10, 192.0.2.0/24 or 2001:db8::/32.");

    public string? DestinationProblem => EndpointProblem(Destination, "firewall.validation.destination_invalid",
        "Destination must be any or a valid IP/CIDR, such as 192.0.2.10, 192.0.2.0/24 or 2001:db8::/32.");

    public string? PortProblem
    {
        get
        {
            var port = Port.Trim();
            if (port.Length == 0) return null;
            if (!IsPort(port))
                return T("firewall.validation.port_invalid", "Enter a port from 1 to 65535, or a start:end range.");
            if (SelectedProtocol?.Value == "any" && !IsAnyEndpoint(port))
                return T("firewall.validation.protocol_port", "The any protocol can only use the any port.");
            return null;
        }
    }

    public bool IsDraftValid => SourceProblem is null && DestinationProblem is null && PortProblem is null;
    public bool CanSubmitDraft => IsDraftValid && !IsLoading && CanManage && !IsReadOnly;
    public string SubmitText => IsEditingRule
        ? T("firewall.rule.update", "Update rule")
        : T("firewall.rule.add", "Add rule");

    /// <summary>The rule the current draft would create, phrased the same way as the table read-back.</summary>
    public string DraftSummary => LocalizedText.Format("firewall.sentence.rule",
        SelectedAction?.Label ?? string.Empty, SelectedDirection?.Label ?? string.Empty, SelectedProtocol?.Label ?? string.Empty,
        string.Format(CultureInfo.CurrentCulture, T("firewall.sentence.port", "port {0}"),
            IsAnyEndpoint(Port.Trim()) ? T("firewall.value.any", "Any") : Port.Trim()),
        string.Format(CultureInfo.CurrentCulture, T("firewall.sentence.from", "from {0}"), AddressPhrase(Source)),
        string.Format(CultureInfo.CurrentCulture, T("firewall.sentence.to", "to {0}"), AddressPhrase(Destination)));

    public bool HasSelection => SelectedRule is not null;
    /// <summary>A refusal raised while the rule dialog is open; the dialog is modal, so the page
    /// status bar behind it is not a place the user can read.</summary>
    public bool HasEditorMessage => !EditorMessage.IsEmpty;
    public string SelectedRuleNumberText => SelectedRule is null
        ? string.Empty
        : LocalizedText.Format("firewall.rule.number_value", SelectedRule.Number);
    public string SelectedRuleSentence => SelectedRow?.Sentence ?? string.Empty;

    // ---- navigation state -------------------------------------------------------------------

    partial void OnIsLoadingChanged(bool value) { RefreshFactProperties(); RefreshCommands(); }
    partial void OnEditorMessageChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasEditorMessage));
    partial void OnIsAvailableChanged(bool value) => RefreshFactProperties();
    partial void OnIsEnabledChanged(bool value) => RefreshFactProperties();
    partial void OnHasFactsChanged(bool value) => RefreshFactProperties();
    partial void OnSelectedIncomingPolicyChanged(FirewallOption? value) => RefreshDefaults();
    partial void OnSelectedOutgoingPolicyChanged(FirewallOption? value) => RefreshDefaults();
    partial void OnSelectedActionChanged(FirewallOption? value) => RefreshDraft();
    partial void OnSelectedDirectionChanged(FirewallOption? value) => RefreshDraft();
    partial void OnSelectedProtocolChanged(FirewallOption? value) => RefreshDraft();
    partial void OnSourceChanged(string value) => RefreshDraft();
    partial void OnDestinationChanged(string value) => RefreshDraft();
    partial void OnPortChanged(string value) => RefreshDraft();
    partial void OnRuleFilterChanged(string value)
    {
        ApplyFilter();
        ClearFilterCommand.NotifyCanExecuteChanged();
        RefreshFactProperties();
    }

    partial void OnSelectedRowChanged(FirewallRuleRow? value)
    {
        if (value is not null && !Equals(SelectedRule, value.Rule)) SelectedRule = value.Rule;
        else if (value is null && SelectedRule is not null) SelectedRule = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedRuleNumberText));
        OnPropertyChanged(nameof(SelectedRuleSentence));
    }

    partial void OnSelectedRuleChanged(FirewallRuleDto? value)
    {
        if (value is not null && SelectedRow?.Number != value.Number)
            SelectedRow = Rules.FirstOrDefault(row => row.Number == value.Number);
        if (value is not null) LoadDraft(value);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedRuleNumberText));
        OnPropertyChanged(nameof(SelectedRuleSentence));
        RefreshCommands();
    }

    // ---- commands ---------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!HasReadPermission)
        {
            _allRules.Clear();
            ApplyFilter();
            SelectedRule = null;
            IsAvailable = false;
            IsEnabled = false;
            HasFacts = true;
            StatusText = LocalizedText.Ref("firewall.permission.read_required");
            return;
        }

        var selectedNumber = SelectedRule?.Number;
        var pendingIncoming = SelectedIncomingPolicy?.Value;
        var pendingOutgoing = SelectedOutgoingPolicy?.Value;
        var keepPendingDefaults = HasDefaultChanges;
        IsLoading = true;
        StatusText = LocalizedText.Ref("firewall.status.loading");
        try
        {
            var status = await _client.GetStatusAsync();
            var windows = string.Equals(status.Backend, "windows-defender", StringComparison.OrdinalIgnoreCase);
            _windowsBackend = windows;
            _backend = status.Backend;
            _version = status.Version;
            SyncOptions(windows);
            IsAvailable = status.IsAvailable;
            IsEnabled = status.IsEnabled;
            HasFacts = true;

            // A pending default-policy edit survives a refresh only while the host still reports the
            // values that edit was based on; otherwise the host reading wins and the editor follows it.
            var hostUnchanged = string.Equals(_hostIncoming, status.DefaultIncomingPolicy, StringComparison.Ordinal)
                && string.Equals(_hostOutgoing, status.DefaultOutgoingPolicy, StringComparison.Ordinal);
            _hostIncoming = status.DefaultIncomingPolicy;
            _hostOutgoing = status.DefaultOutgoingPolicy;
            UnavailableReason = LocalizedStatus.Literal(string.Empty);

            if (!status.IsAvailable)
            {
                _allRules.Clear();
                ApplyFilter();
                SelectedRule = null;
                AdoptHostDefaults();
                UnavailableReason = LocalizedStatus.Literal(FirewallProblemText.Format(status.ProblemCode,
                    T("firewall.value.unknown", "Unknown")));
                StatusText = LocalizedText.Ref("firewall.status.unavailable", FirewallProblemText.Format(status.ProblemCode,
                    T("firewall.value.unknown", "Unknown")));
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(status.ProblemCode);
                return;
            }

            if (keepPendingDefaults && hostUnchanged
                && Pick(Policies, pendingIncoming) is { } incoming
                && Pick(Policies, pendingOutgoing) is { } outgoing)
            {
                SelectedIncomingPolicy = incoming;
                SelectedOutgoingPolicy = outgoing;
            }
            else AdoptHostDefaults();

            var rules = await _client.ListRulesAsync();
            _allRules.Clear();
            foreach (var rule in rules) _allRules.Add(new FirewallRuleRow(rule));
            ApplyFilter();
            if (selectedNumber is { } number)
                SelectedRow = Rules.FirstOrDefault(row => row.Number == number);
            StatusText = windows
                ? LocalizedText.Ref("firewall.status.windows_ready")
                : LocalizedText.Ref(status.IsEnabled ? "firewall.status.ready_enabled" : "firewall.status.ready_disabled",
                    BackendLabel, VersionLabel);
        }
        catch (Exception exception)
        {
            _allRules.Clear();
            ApplyFilter();
            SelectedRule = null;
            IsAvailable = false;
            IsEnabled = false;
            HasFacts = true;
            UnavailableReason = LocalizedStatus.Literal(exception.Message);
            StatusText = LocalizedText.Ref("firewall.status.failed", exception.Message);
        }
        finally
        {
            IsLoading = false;
            RefreshFactProperties();
            RefreshCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private async Task EnableAsync()
    {
        if (!await ConfirmOperationAsync("firewall.confirm.enable_title", "firewall.confirm.enable_message")) return;
        await ApplyAsync(() => _client.SetEnabledAsync(new UpdateFirewallEnabledRequest(true)), "firewall.operation.enabled");
    }

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync()
    {
        if (!await ConfirmOperationAsync("firewall.confirm.disable_title", "firewall.confirm.disable_message")) return;
        await ApplyAsync(() => _client.SetEnabledAsync(new UpdateFirewallEnabledRequest(false)), "firewall.operation.disabled");
    }

    [RelayCommand(CanExecute = nameof(CanSaveDefaults))]
    private async Task SaveDefaultsAsync()
    {
        var summary = DefaultsSummary;
        if (!await ConfirmOperationAsync("firewall.confirm.defaults_title", "firewall.confirm.defaults_message",
                ShowLockoutWarning ? $"{summary} {T("firewall.defaults.lockout_warning", string.Empty)}" : summary)) return;
        await ApplyAsync(() => _client.SetDefaultsAsync(new UpdateFirewallDefaultsRequest(
            SelectedIncomingPolicy?.Value ?? "deny", SelectedOutgoingPolicy?.Value ?? "allow")), "firewall.operation.defaults_saved");
    }

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private async Task ShowAddRuleEditorAsync()
    {
        ClearDraft();
        IsEditingRule = false;
        if (ShowRuleEditorAsync is not null) await ShowRuleEditorAsync(false);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateSelected))]
    private async Task ShowEditRuleEditorAsync()
    {
        if (SelectedRule is not { } rule) return;
        LoadDraft(rule);
        IsEditingRule = true;
        if (ShowRuleEditorAsync is not null) await ShowRuleEditorAsync(true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteRuleAsync()
    {
        if (SelectedRule is not { } target) return;
        if (!await ConfirmOperationAsync("firewall.confirm.delete_title", "firewall.confirm.delete_message",
                target.Number, SelectedRow?.Sentence ?? string.Empty)) return;
        if (await ApplyAsync(() => _client.DeleteRuleAsync(target.Number), "firewall.operation.rule_deleted")) ClearDraft();
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void ClearDraft()
    {
        SelectedRule = null;
        SelectedRow = null;
        SelectedAction = Pick(Actions, "allow");
        SelectedDirection = _directions[0];
        SelectedProtocol = _protocols[0];
        Source = string.Empty;
        Destination = string.Empty;
        Port = string.Empty;
        IsEditingRule = false;
        EditorMessage = default;
    }

    [RelayCommand(CanExecute = nameof(HasFilter))]
    private void ClearFilter() => RuleFilter = string.Empty;

    /// <summary>Submits the draft from the rule dialog, which is either a new rule or a replacement.</summary>
    public Task<bool> SubmitDraftAsync() => IsEditingRule ? UpdateRuleAsync() : AddRuleAsync();

    public async Task<bool> AddRuleAsync()
    {
        if (!CanManage) return false;
        if (!TryBuildRule(out var rule)) return false;
        var success = await ApplyAsync(() => _client.CreateRuleAsync(rule), "firewall.operation.rule_added");
        if (success) ClearDraft();
        return success;
    }

    public async Task<bool> UpdateRuleAsync()
    {
        if (!CanManage) return false;
        var target = SelectedRule;
        if (target is null || !TryBuildRule(out var rule)) return false;
        var success = await ApplyAsync(() => _client.UpdateRuleAsync(target.Number,
            new UpdateFirewallRuleRequest(rule.Action, rule.Direction, rule.Protocol, rule.Source, rule.Destination, rule.Port)),
            "firewall.operation.rule_updated");
        if (success) ClearDraft();
        return success;
    }

    /// <summary>The dialog guard compares this against the state it opened with.</summary>
    public (string? Action, string? Direction, string? Protocol, string Source, string Destination, string Port) DraftSnapshot() =>
        (SelectedAction?.Value, SelectedDirection?.Value, SelectedProtocol?.Value, Source, Destination, Port);

    // ---- writes -----------------------------------------------------------------------------

    private async Task<bool> ApplyAsync(Func<Task<FirewallOperationResult>> operation, string successKey)
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
            StatusText = result.Success
                ? LocalizedText.Ref(successKey)
                : LocalizedText.Ref("firewall.operation.failed", FirewallProblemText.Format(result.ProblemCode,
                    T("firewall.value.unknown", "Unknown")));
            if (!result.Success)
            {
                EditorMessage = StatusText;
                await ShowPrivilegedHelperUnavailableAsyncIfNeeded(result.ProblemCode);
            }
            success = result.Success;
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Ref("firewall.operation.failed", exception.Message);
            EditorMessage = StatusText;
        }
        finally { IsLoading = false; }
        // A successful change is immediately re-read from the host so button state and rule numbers
        // always reflect the host rather than optimistic local state.
        if (success) await RefreshAsync();
        return success;
    }

    private Task<bool> ConfirmOperationAsync(string titleKey, string messageKey, params object?[] arguments)
    {
        if (ConfirmAsync is null) return Task.FromResult(true);
        var title = LocalizedText.Get(titleKey, titleKey);
        var message = LocalizedText.Format(messageKey, arguments);
        return ConfirmAsync(title, message);
    }

    private Task ShowPrivilegedHelperUnavailableAsyncIfNeeded(string? problemCode) =>
        PrivilegedHelperProblemText.TryFormat(problemCode, out _)
            ? ShowPrivilegedHelperUnavailableAsync?.Invoke(problemCode) ?? Task.CompletedTask
            : Task.CompletedTask;

    // ---- derived state ----------------------------------------------------------------------

    private void RefreshFactProperties()
    {
        OnPropertyChanged(nameof(StatePillText));
        OnPropertyChanged(nameof(StatePillOk));
        OnPropertyChanged(nameof(StatePillWarn));
        OnPropertyChanged(nameof(StatePillBad));
        OnPropertyChanged(nameof(StatePillNeutral));
        OnPropertyChanged(nameof(BackendLabel));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(HostIncomingLabel));
        OnPropertyChanged(nameof(HostOutgoingLabel));
        OnPropertyChanged(nameof(ShowNoPermission));
        OnPropertyChanged(nameof(ShowUnavailable));
        OnPropertyChanged(nameof(ShowReadOnly));
        OnPropertyChanged(nameof(ShowWindowsNote));
        OnPropertyChanged(nameof(ShowEnableAction));
        OnPropertyChanged(nameof(ShowDisableAction));
        OnPropertyChanged(nameof(HasProtectionActionHint));
        OnPropertyChanged(nameof(ProtectionActionHint));
        OnPropertyChanged(nameof(CanEditDefaults));
        OnPropertyChanged(nameof(ShowRulesEmpty));
        OnPropertyChanged(nameof(ShowFilterEmpty));
        OnPropertyChanged(nameof(RuleCountText));
        OnPropertyChanged(nameof(CanSubmitDraft));
        OnPropertyChanged(nameof(DefaultsSummary));
        OnPropertyChanged(nameof(HasDefaultChanges));
        OnPropertyChanged(nameof(ShowLockoutWarning));
        RefreshCommands();
    }

    private void RefreshDefaults()
    {
        OnPropertyChanged(nameof(HasDefaultChanges));
        OnPropertyChanged(nameof(DefaultsSummary));
        OnPropertyChanged(nameof(ShowLockoutWarning));
        SaveDefaultsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDraft()
    {
        // Editing the draft invalidates whatever the host said about the previous attempt.
        EditorMessage = default;
        OnPropertyChanged(nameof(SourceProblem));
        OnPropertyChanged(nameof(DestinationProblem));
        OnPropertyChanged(nameof(PortProblem));
        OnPropertyChanged(nameof(IsDraftValid));
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(CanSubmitDraft));
        OnPropertyChanged(nameof(SubmitText));
    }

    private void RefreshCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        SaveDefaultsCommand.NotifyCanExecuteChanged();
        ShowAddRuleEditorCommand.NotifyCanExecuteChanged();
        ShowEditRuleEditorCommand.NotifyCanExecuteChanged();
        DeleteRuleCommand.NotifyCanExecuteChanged();
        ClearDraftCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var previous = SelectedRow;
        var filter = RuleFilter.Trim();
        var matches = filter.Length == 0
            ? _allRules
            : _allRules.Where(row => row.Matches(filter)).ToList();
        Rules.Clear();
        foreach (var row in matches) Rules.Add(row);
        SelectedRow = previous is not null && matches.Contains(previous) ? previous : null;
        OnPropertyChanged(nameof(RuleCountText));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(ShowRulesEmpty));
        OnPropertyChanged(nameof(ShowFilterEmpty));
        OnPropertyChanged(nameof(HasFilter));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void LoadDraft(FirewallRuleDto value)
    {
        SelectedAction = Pick(Actions, value.Action) ?? Pick(Actions, "allow");
        SelectedDirection = _directions.FirstOrDefault(option => option.Value == value.Direction) ?? _directions[0];
        SelectedProtocol = _protocols.FirstOrDefault(option => option.Value == value.Protocol) ?? _protocols[0];
        Source = IsAnyEndpoint(value.Source) ? string.Empty : value.Source;
        Destination = IsAnyEndpoint(value.Destination) ? string.Empty : value.Destination;
        Port = IsAnyEndpoint(value.Port) ? string.Empty : value.Port;
    }

    private void AdoptHostDefaults()
    {
        // No fallback value: an unreported policy stays unselected instead of inventing a change.
        SelectedIncomingPolicy = Pick(Policies, _hostIncoming);
        SelectedOutgoingPolicy = Pick(Policies, _hostOutgoing);
    }

    private bool TryBuildRule(out CreateFirewallRuleRequest rule)
    {
        var port = Port.Trim();
        if (SourceProblem is { } sourceProblem)
        {
            StatusText = LocalizedStatus.Literal(sourceProblem);
            EditorMessage = StatusText;
            rule = default!;
            return false;
        }
        if (DestinationProblem is { } destinationProblem)
        {
            StatusText = LocalizedStatus.Literal(destinationProblem);
            EditorMessage = StatusText;
            rule = default!;
            return false;
        }
        if (PortProblem is { } portProblem)
        {
            StatusText = LocalizedStatus.Literal(portProblem);
            EditorMessage = StatusText;
            rule = default!;
            return false;
        }

        rule = new CreateFirewallRuleRequest(SelectedAction?.Value ?? "allow", SelectedDirection?.Value ?? "in",
            SelectedProtocol?.Value ?? "tcp", NormalizeEndpoint(Source), NormalizeEndpoint(Destination),
            string.IsNullOrEmpty(port) ? "any" : port);
        return true;
    }

    // ---- options ----------------------------------------------------------------------------

    private void SyncOptions(bool windows)
    {
        var policies = new List<(string Value, string Key)>
        {
            ("allow", "firewall.choice.allow"),
            ("deny", "firewall.choice.deny"),
        };
        if (!windows) policies.Add(("reject", "firewall.choice.reject"));
        Sync(Policies, policies);

        var actions = new List<(string Value, string Key)>(policies);
        if (!windows) actions.Add(("limit", "firewall.choice.limit"));
        Sync(Actions, actions);

        // A backend switch can remove a value the draft or the default editor was using; reject and
        // limit only exist on UFW, so a Windows host must not keep a selection the list no longer has.
        SelectedAction = Pick(Actions, SelectedAction?.Value) ?? Pick(Actions, "allow");
        if (SelectedIncomingPolicy is { } incoming && !Policies.Contains(incoming))
            SelectedIncomingPolicy = Pick(Policies, incoming.Value);
        if (SelectedOutgoingPolicy is { } outgoing && !Policies.Contains(outgoing))
            SelectedOutgoingPolicy = Pick(Policies, outgoing.Value);
    }

    private static void Sync(ObservableCollection<FirewallOption> target, IReadOnlyList<(string Value, string Key)> desired)
    {
        if (target.Count == desired.Count
            && target.Select(option => option.Value).SequenceEqual(desired.Select(entry => entry.Value), StringComparer.Ordinal))
            return;
        target.Clear();
        foreach (var (value, key) in desired) target.Add(new FirewallOption(value, key));
    }

    private string PolicyLabel(string? value) => value switch
    {
        null or "" => IsWindowsBackend
            ? T("firewall.value.mixed", "Differs across profiles")
            : T("firewall.value.unknown", "Unknown"),
        _ => Pick(Policies, value)?.Label ?? value,
    };

    private string? EndpointProblem(string value, string key, string fallback) =>
        IsEndpoint(value) ? null : T(key, fallback);

    private bool HasReadPermission => _permissions.IsGranted(AppPermissions.ServerFirewallRead);
    private bool HasManagePermission => HasReadPermission && _permissions.IsGranted(AppPermissions.ServerFirewallManage);

    private bool CanRefresh => HasReadPermission && !IsLoading;
    private bool CanManage => HasManagePermission && IsAvailable && !IsLoading;
    private bool CanSaveDefaults => CanManage && HasDefaultChanges
        && SelectedIncomingPolicy is not null && SelectedOutgoingPolicy is not null;
    private bool CanEnable => CanManage && (IsWindowsBackend || !IsEnabled);
    private bool CanDisable => CanManage && (IsWindowsBackend || IsEnabled);
    private bool CanAddRule => CanManage;
    private bool CanUpdateSelected => CanManage && SelectedRule is not null;
    private bool CanDeleteSelected => CanManage && SelectedRule is not null;

    private static FirewallOption? Pick(IEnumerable<FirewallOption> options, string? value) =>
        value is null ? null : options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));

    private static bool IsAnyEndpoint(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized)
            || normalized.Equals("any", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("anywhere", StringComparison.OrdinalIgnoreCase);
    }

    private static string AddressPhrase(string value) =>
        IsAnyEndpoint(value) ? LocalizedText.Get("firewall.value.any", "Any") : value.Trim();

    private static string NormalizeEndpoint(string value) => string.IsNullOrWhiteSpace(value) ? "any" : value.Trim();

    private static bool IsEndpoint(string value)
    {
        var normalized = NormalizeEndpoint(value);
        if (IsAnyEndpoint(normalized)) return true;
        var slash = normalized.IndexOf('/');
        var address = slash < 0 ? normalized : normalized[..slash];
        if (!IPAddress.TryParse(address, out var parsed)) return false;
        return slash < 0
            || int.TryParse(normalized[(slash + 1)..], out var prefix) && prefix >= 0
                && prefix <= (parsed.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32);
    }

    private static bool IsPort(string value)
    {
        var parts = value.Split(':');
        return parts.Length is 1 or 2 && parts.All(part => int.TryParse(part, out var port) && port is > 0 and <= 65535)
            && (parts.Length == 1 || int.Parse(parts[0]) <= int.Parse(parts[1]));
    }
}
