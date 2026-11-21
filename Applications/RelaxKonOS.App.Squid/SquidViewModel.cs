using RelaxKonOS.AppSDK;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.App.Squid;

/// <summary>
/// Window-local Squid console. Three things are deliberately kept apart: the facts read from the
/// server, the local draft, and a submitted operation whose outcome is not yet confirmed. A draft
/// edit never rewrites a fact, an unconfirmed operation blocks every new mutation, and an action the
/// host would refuse (unmanaged configuration, missing package, interrupted transaction) is disabled
/// with an explanation instead of being offered and failing afterwards.
/// </summary>
public partial class SquidViewModel : ObservableObject, IDisposable
{
    /// <summary>The workspace host supplies the shortcut to its component management surface.</summary>
    public Func<IRelayCommand?>? GetComponentManagementCommand { get; init; }
    private readonly IServerSquid _client;
    private readonly Func<string, string, string> _localize;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private bool _disposed;
    private bool _previewGenerated;
    private Func<string, CancellationToken, Task<ProxyOperationAcceptedDto>>? _pendingSubmission;
    private string? _pendingKey;
    private Guid? _operation;

    public SquidViewModel(IServerSquid client, Func<string, string, string> localize)
    {
        _client = client;
        _localize = localize;
    }

    public void RefreshLanguage() => OnPropertyChanged(string.Empty);

    /// <summary>Draft fields. They only reach the host through preview, validate and apply.</summary>
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private string _port = "3128";
    [ObservableProperty] private bool _allowRemoteClients;
    [ObservableProperty] private string _allowedNetworks = "127.0.0.1/32\n::1/128";
    [ObservableProperty] private string _allowedDomains = "";
    [ObservableProperty] private string _deniedDomains = "";
    [ObservableProperty] private string _upstreamHost = "";
    [ObservableProperty] private string _upstreamPort = "7890";
    [ObservableProperty] private bool _adoptExistingConfiguration;

    [ObservableProperty] private int _accessModeIndex;
    [ObservableProperty] private string _newAccountName = "";
    [ObservableProperty] private string _accountPassword = "";
    [ObservableProperty] private SquidAccount? _selectedAccount;
    public ObservableCollection<SquidAccount> Accounts { get; } = [];
    public Func<string, Task<bool>>? ConfirmDeleteAccountAsync { get; set; }
    public bool RequiresPassword => AccessModeIndex != (int)SquidAccessMode.IpWhitelist;
    /// <summary>The three modes differ in more than a name; the dialog explains the selected one.</summary>
    public bool IsIpWhitelistMode => AccessModeIndex == (int)SquidAccessMode.IpWhitelist;
    public bool IsPasswordMode => AccessModeIndex == (int)SquidAccessMode.Password;
    public bool IsCombinedMode => AccessModeIndex == (int)SquidAccessMode.PasswordAndIpWhitelist;
    public bool HasAccountDraft => AccountPassword.Length > 0;
    public bool HasAccounts => Accounts.Count > 0;
    public bool IsLastEnabledAccountProtected => SelectedAccount is { Enabled: true } && Accounts.Count(x => x.Enabled) == 1
        && Snapshot?.State.Configuration?.AccessMode is SquidAccessMode.Password or SquidAccessMode.PasswordAndIpWhitelist;
    public bool HasEnabledAccounts => Accounts.Any(x => x.Enabled);
    public bool ShowNoEnabledAccounts => RequiresPassword && !HasEnabledAccounts;
    public bool CanManageAccounts => CanMutate;
    /// <summary>The account editor edits a selection; without one it would open an empty form.</summary>
    public bool CanEditSelectedAccount => CanManageAccounts && SelectedAccount is not null;
    public bool CanCreateAccount => CanManageAccounts && SquidAccountValidation.ValidUsername(NewAccountName) && SquidAccountValidation.ValidPassword(AccountPassword);
    public bool CanResetAccountPassword => CanManageAccounts && SelectedAccount is not null && SquidAccountValidation.ValidPassword(AccountPassword);
    public bool CanRemoveSelectedAccount => CanManageAccounts && SelectedAccount is not null
        && !IsLastEnabledAccountProtected;
    public bool CanToggleAccount => CanManageAccounts && SelectedAccount is not null && (!SelectedAccount.Enabled || CanRemoveSelectedAccount);
    public SquidMessage ToggleAccountText => L(SelectedAccount?.Enabled == true ? "account_disable" : "account_enable");
    partial void OnAccessModeIndexChanged(int value) => OnDraftChanged();
    partial void OnNewAccountNameChanged(string value) => NotifyActions();
    partial void OnAccountPasswordChanged(string value) { OnPropertyChanged(nameof(HasAccountDraft)); NotifyActions(); }
    partial void OnSelectedAccountChanged(SquidAccount? value) => NotifyActions();

    [ObservableProperty] private SquidConfigurationSnapshot? _snapshot;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRead;
    [ObservableProperty] private bool _canManage;
    [ObservableProperty] private bool _isUnavailable;
    [ObservableProperty] private bool _isConfigurationValid = true;
    [ObservableProperty] private bool _isPreviewStale;
    [ObservableProperty] private bool _outcomeFailed;

    /// <summary>Single-sentence host summary for the status bar and tooltips.</summary>
    [ObservableProperty] private SquidMessage _statusText;
    /// <summary>What the window is doing right now; empty when idle.</summary>
    [ObservableProperty] private SquidMessage _activityText;
    /// <summary>Native diagnostics or the last transport error. Never a claim about the runtime.</summary>
    [ObservableProperty] private SquidMessage _detailText;
    [ObservableProperty] private SquidMessage _previewText;
    [ObservableProperty] private SquidMessage _serviceStateText;
    [ObservableProperty] private SquidMessage _bootStateText;
    [ObservableProperty] private SquidMessage _revisionText;
    [ObservableProperty] private SquidMessage _diskStateText;
    [ObservableProperty] private SquidMessage _outcomeText;
    [ObservableProperty] private string _pendingReference = "";

    /// <summary>Renderer problems, grouped by the card that can fix them.</summary>
    public ObservableCollection<SquidProblem> ListenerProblems { get; } = [];
    public ObservableCollection<SquidProblem> SourceProblems { get; } = [];
    public ObservableCollection<SquidProblem> DomainProblems { get; } = [];
    public ObservableCollection<SquidProblem> UpstreamProblems { get; } = [];

    public Func<Task<bool>>? ConfirmApplyAsync { get; set; }
    public Func<Task<bool>>? ConfirmReloadAsync { get; set; }

    public bool HasDetail => !DetailText.IsEmpty;
    public bool HasPreview => !PreviewText.IsEmpty;
    public bool HasOutcome => !OutcomeText.IsEmpty;
    /// <summary>A failed operation keeps the result visible until the next action replaces it.</summary>
    public bool ShowOutcomeFailure => HasOutcome && OutcomeFailed;
    public bool ShowOutcomeSuccess => HasOutcome && !OutcomeFailed;
    /// <summary>The action bar shows one line: the last outcome, otherwise the stale-preview warning.</summary>
    public bool ShowBarStale => IsPreviewStale && !HasOutcome;
    public bool ShowActionBarStatus => HasOutcome || IsPreviewStale;
    public bool HasPending => _pendingKey is not null || _operation is not null;
    public bool HasUnsavedChanges
    {
        get
        {
            if (!_initialized) return false;
            try
            {
                return SquidConfigurationRenderer.Preview(BuildConfiguration()).Configuration
                    != SquidConfigurationRenderer.Preview(Snapshot?.State.Configuration ?? new SquidConfiguration()).Configuration;
            }
            catch (InvalidOperationException) { return true; }
        }
    }

    public bool IsRunning => Snapshot?.Status is { Running: true };
    public bool IsEnabled => Snapshot?.Status is { Enabled: true };
    /// <summary>The host reported a usable Squid; every lifecycle fact is meaningful only then.</summary>
    public bool HasServiceState => Snapshot?.Status is { Supported: true, Installed: true };
    /// <summary>Only a file carrying the RelaxKonOS header may be started, reloaded or enabled.</summary>
    public bool IsConfigurationAdopted => Snapshot?.Status is { Managed: true };
    public bool ShowUnsupported => Snapshot?.Status is { Supported: false };
    public bool ShowRecoveryRequired => Snapshot?.Status is { RecoveryRequired: true };
    public bool ShowAdoptionRequired => Snapshot?.Status is { Supported: true, Installed: true, Managed: false };
    /// <summary>A supported host without the package: the window can only point at Component Center.</summary>
    public bool ShowInstallRequired => Snapshot?.Status is { Supported: true, Installed: false };
    /// <summary>The unsupported notice already explains a host that cannot run Squid.</summary>
    public bool ShowReadOnly => CanRead && !CanManage && Snapshot?.Status is { Supported: true };
    /// <summary>Replacing an unmanaged file requires the explicit ownership opt-in.</summary>
    public bool NeedsAdoption => Snapshot?.Status is { Installed: true, Managed: false } && !AdoptExistingConfiguration;

    /// <summary>
    /// One label answers "is the gateway up?" for the whole page. It follows the same facts the
    /// service-action buttons use, so a green dot can never sit next to an unknown service.
    /// </summary>
    public SquidMessage StatePillText => ShowUnsupported ? L("state.unsupported")
        : IsUnavailable ? L("state.unavailable")
        : ShowRecoveryRequired ? L("state.recovery")
        : !HasServiceState ? L("state.not_installed")
        : IsRunning ? L("running") : L("stopped");
    public bool StatePillOk => HasServiceState && IsRunning && !ShowRecoveryRequired && !IsUnavailable;
    public bool StatePillWarn => HasServiceState && !IsRunning && !ShowRecoveryRequired && !IsUnavailable;
    public bool StatePillBad => ShowUnsupported || ShowRecoveryRequired || IsUnavailable;
    public bool StatePillNeutral => !StatePillOk && !StatePillWarn && !StatePillBad;
    /// <summary>Why the lifecycle buttons are not offered, stated once instead of left to inference.</summary>
    public SquidMessage ServiceControlReasonText => ShowUnsupported ? L("control_reason.unsupported")
        : IsUnavailable ? L("control_reason.unavailable")
        : ShowInstallRequired ? L("control_reason.not_installed")
        : HasPending ? L("control_reason.pending")
        : !CanManage ? L("control_reason.read_only")
        : ShowRecoveryRequired ? L("control_reason.recovery")
        : HasServiceState && !IsConfigurationAdopted ? L("control_reason.adoption")
        : HasServiceState && !IsRunning ? L("control_reason.stopped")
        : SquidMessage.Literal("");
    public bool HasServiceControlReason => !ServiceControlReasonText.IsEmpty;

    // Read-back of the draft on the page that owns it: a closed editor must not be the only place
    // the current configuration can be seen. None of these is an input.
    public SquidMessage ListenerSummary => L("summary.endpoint", BindAddress.Trim(), Port);
    public SquidMessage RemoteClientsSummary => L(AllowRemoteClients ? "summary.remote_allowed" : "summary.remote_local");
    public SquidMessage NetworksSummary => L("summary.count", Lines(AllowedNetworks).Length);
    public SquidMessage AllowedDomainsSummary => Lines(AllowedDomains).Length == 0
        ? L("summary.all_allowed") : L("summary.count", Lines(AllowedDomains).Length);
    public SquidMessage DeniedDomainsSummary => L("summary.count", Lines(DeniedDomains).Length);
    public SquidMessage UpstreamSummary => string.IsNullOrWhiteSpace(UpstreamHost)
        ? L("summary.direct") : L("summary.endpoint", UpstreamHost.Trim(), UpstreamPort);
    public SquidMessage AccessModeSummary => L(AccessModeIndex switch
    {
        (int)SquidAccessMode.Password => "mode_password",
        (int)SquidAccessMode.PasswordAndIpWhitelist => "mode_both",
        _ => "mode_ip"
    });
    public SquidMessage AccountsSummary => Accounts.Count == 0
        ? L("accounts_empty") : L("summary.accounts", Accounts.Count, Accounts.Count(account => account.Enabled));
    public bool HasListenerProblems => ListenerProblems.Count > 0;
    public bool HasSourceProblems => SourceProblems.Count > 0;
    public bool HasDomainProblems => DomainProblems.Count > 0;
    public bool HasUpstreamProblems => UpstreamProblems.Count > 0;
    /// <summary>Which of the four draft cards a reader has to open before publishing.</summary>
    public SquidMessage DraftProblemText => IsConfigurationValid ? L("draft.ok")
        : L("draft.blocked", SquidMessage.Join(" · ", ListenerProblems.Concat(SourceProblems)
            .Concat(DomainProblems).Concat(UpstreamProblems).Select(problem => problem.Message)));
    public bool HasDraftProblem => !IsConfigurationValid;
    /// <summary>The runtime card states whether the draft still matches what the server has.</summary>
    public SquidMessage DraftStateText => HasUnsavedChanges ? L("draft.dirty") : L("draft.saved");
    /// <summary>
    /// One line states whether the primary action can run and, when it cannot, which gate is closed.
    /// The buttons already know this; a reader should not have to try them to find out.
    /// </summary>
    public bool IsReadyToApply => CanApply;
    public SquidMessage ApplyReadinessText => CanApply ? L("ready.apply")
        : L("ready.blocked", SquidMessage.Join("；", ApplyBlockers()));
    private IEnumerable<SquidMessage> ApplyBlockers()
    {
        if (ShowUnsupported) { yield return L("blocker.unsupported"); yield break; }
        if (IsUnavailable) { yield return L("blocker.unavailable"); yield break; }
        if (ShowInstallRequired) { yield return L("blocker.not_installed"); yield break; }
        if (ShowRecoveryRequired) { yield return L("blocker.recovery"); yield break; }
        if (!CanManage) { yield return L("blocker.read_only"); yield break; }
        if (HasPending) yield return L("blocker.pending");
        if (!IsConfigurationValid) yield return L("blocker.invalid");
        if (!IsConfigurationAdopted && !AdoptExistingConfiguration) yield return L("blocker.adoption");
        if (RequiresPassword && !HasEnabledAccounts) yield return L("blocker.accounts");
    }
    /// <summary>The component centre owns installation; this window only points at it.</summary>
    public IRelayCommand? OpenComponentCenterCommand => GetComponentManagementCommand?.Invoke();
    public bool CanOpenComponentCenter => OpenComponentCenterCommand is not null;

    /// <summary>A host that cannot run Squid offers no draft to edit; the notice explains why instead.</summary>
    public bool CanEdit => !_disposed && !_lifetime.IsCancellationRequested && CanRead && CanManage && !IsBusy && !HasPending
        && Snapshot?.Status is not { Supported: false };
    public bool CanMutate => CanEdit && Snapshot?.Status is { Supported: true, Installed: true, RecoveryRequired: false };
    /// <summary>Stop and disable stay available during host recovery; nothing else does.</summary>
    public bool CanControlService => CanEdit && Snapshot?.Status is { Supported: true, Installed: true };
    public bool CanRefresh => !_disposed && !_lifetime.IsCancellationRequested && CanRead && !IsBusy;
    public bool CanResume => !_disposed && !_lifetime.IsCancellationRequested && CanRead && !IsBusy && HasPending;
    public bool CanLoadStored => CanEdit && HasUnsavedChanges;
    public bool CanPreview => CanEdit && IsConfigurationValid;
    public bool CanValidate => CanControlService && IsConfigurationValid;
    public bool CanApply => CanMutate && IsConfigurationValid && !NeedsAdoption && (!RequiresPassword || HasEnabledAccounts);
    public bool ShowPackageUnsupported => Snapshot?.Status is { Supported: true, PackageManagementSupported: false };
    public bool CanStart => CanMutate && !IsRunning && IsConfigurationAdopted;
    public bool CanStopService => CanControlService && IsRunning;
    public bool CanRestartService => CanMutate && IsRunning && IsConfigurationAdopted;
    public bool CanReloadService => CanMutate && IsRunning && IsConfigurationAdopted;
    public bool CanEnableService => CanMutate && !IsEnabled && IsConfigurationAdopted;
    public bool CanDisableService => CanControlService && IsEnabled;
    /// <summary>One boot control instead of two mutually exclusive buttons.</summary>
    public bool CanToggleBoot => CanEnableService || CanDisableService;
    public SquidMessage BootActionText => L(IsEnabled ? "disable" : "enable");

    private static readonly string[] DerivedState =
    [
        nameof(HasAccounts), nameof(IsLastEnabledAccountProtected), nameof(RequiresPassword),
        nameof(IsIpWhitelistMode), nameof(IsPasswordMode), nameof(IsCombinedMode), nameof(HasEnabledAccounts), nameof(ShowNoEnabledAccounts), nameof(CanManageAccounts),
        nameof(CanCreateAccount), nameof(CanEditSelectedAccount), nameof(CanResetAccountPassword), nameof(CanRemoveSelectedAccount), nameof(CanToggleAccount), nameof(ToggleAccountText),
        nameof(ShowPackageUnsupported), nameof(CanEdit), nameof(CanMutate), nameof(CanControlService), nameof(CanRefresh), nameof(CanResume),
        nameof(CanLoadStored), nameof(CanPreview), nameof(CanValidate), nameof(CanApply), nameof(CanStart),
        nameof(CanStopService), nameof(CanRestartService), nameof(CanReloadService), nameof(CanEnableService),
        nameof(CanDisableService), nameof(CanToggleBoot), nameof(BootActionText),
        nameof(HasPending), nameof(HasUnsavedChanges),
        nameof(IsRunning), nameof(IsEnabled), nameof(IsConfigurationAdopted), nameof(ShowUnsupported),
        nameof(ShowRecoveryRequired), nameof(ShowAdoptionRequired),
        nameof(ShowReadOnly), nameof(NeedsAdoption),
        nameof(HasServiceState), nameof(ShowInstallRequired), nameof(StatePillText), nameof(StatePillOk),
        nameof(StatePillWarn), nameof(StatePillBad), nameof(StatePillNeutral), nameof(ServiceControlReasonText),
        nameof(HasServiceControlReason), nameof(ListenerSummary), nameof(RemoteClientsSummary), nameof(NetworksSummary),
        nameof(AllowedDomainsSummary), nameof(DeniedDomainsSummary), nameof(UpstreamSummary), nameof(AccessModeSummary),
        nameof(AccountsSummary), nameof(HasListenerProblems), nameof(HasSourceProblems), nameof(HasDomainProblems),
        nameof(HasUpstreamProblems), nameof(DraftProblemText), nameof(HasDraftProblem), nameof(DraftStateText),
        nameof(IsReadyToApply), nameof(ApplyReadinessText), nameof(OpenComponentCenterCommand), nameof(CanOpenComponentCenter)
    ];

    partial void OnIsBusyChanged(bool value) => NotifyActions();
    partial void OnCanReadChanged(bool value) => NotifyActions();
    partial void OnCanManageChanged(bool value) => NotifyActions();
    partial void OnSnapshotChanged(SquidConfigurationSnapshot? value) => NotifyActions();
    partial void OnIsConfigurationValidChanged(bool value) => NotifyActions();
    partial void OnIsPreviewStaleChanged(bool value) => NotifyPreviewState();
    partial void OnAdoptExistingConfigurationChanged(bool value) => NotifyActions();
    partial void OnDetailTextChanged(SquidMessage value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnPreviewTextChanged(SquidMessage value) => OnPropertyChanged(nameof(HasPreview));
    partial void OnOutcomeTextChanged(SquidMessage value) => NotifyOutcome();
    partial void OnOutcomeFailedChanged(bool value) => NotifyOutcome();

    private void NotifyOutcome()
    {
        OnPropertyChanged(nameof(HasOutcome));
        OnPropertyChanged(nameof(ShowOutcomeFailure));
        OnPropertyChanged(nameof(ShowOutcomeSuccess));
        NotifyPreviewState();
    }

    private void NotifyPreviewState()
    {
        OnPropertyChanged(nameof(ShowBarStale));
        OnPropertyChanged(nameof(ShowActionBarStatus));
    }

    partial void OnBindAddressChanged(string value) => OnDraftChanged();
    partial void OnPortChanged(string value) => OnDraftChanged();
    partial void OnAllowRemoteClientsChanged(bool value) => OnDraftChanged();
    partial void OnAllowedNetworksChanged(string value) => OnDraftChanged();
    partial void OnAllowedDomainsChanged(string value) => OnDraftChanged();
    partial void OnDeniedDomainsChanged(string value) => OnDraftChanged();
    partial void OnUpstreamHostChanged(string value) => OnDraftChanged();
    partial void OnUpstreamPortChanged(string value) => OnDraftChanged();

    private void NotifyActions()
    {
        foreach (var property in DerivedState) OnPropertyChanged(property);
        CreateAccountCommand.NotifyCanExecuteChanged();
        ResetAccountPasswordCommand.NotifyCanExecuteChanged();
        ToggleAccountCommand.NotifyCanExecuteChanged();
        DeleteAccountCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        LoadStoredCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        EnableCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        ToggleBootCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Every edit re-runs the shared renderer, so a problem shows up in the card that owns it.</summary>
    private void OnDraftChanged()
    {
        Revalidate();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        if (_previewGenerated) IsPreviewStale = true;
        NotifyActions();
    }

    private void Revalidate()
    {
        ListenerProblems.Clear(); SourceProblems.Clear(); DomainProblems.Clear(); UpstreamProblems.Clear();
        if (!_initialized) { IsConfigurationValid = false; return; }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        var port = 3128;
        if (!int.TryParse(Port, out port)) { Add(ListenerProblems, L("invalid_port")); reported.Add("listener.port.invalid"); }
        var upstreamPort = 7890;
        var hasUpstream = !string.IsNullOrWhiteSpace(UpstreamHost);
        if (hasUpstream && !int.TryParse(UpstreamPort, out upstreamPort))
        {
            Add(UpstreamProblems, L("invalid_port")); reported.Add("upstream.port.invalid_or_loop");
        }

        var configuration = new SquidConfiguration
        {
            AccessMode = (SquidAccessMode)AccessModeIndex,
            BindAddress = BindAddress.Trim(),
            Port = port,
            AllowRemoteClients = AllowRemoteClients,
            AllowedNetworks = Lines(AllowedNetworks),
            AllowedDomains = Lines(AllowedDomains),
            DeniedDomains = Lines(DeniedDomains),
            Upstream = hasUpstream ? new(UpstreamHost.Trim(), upstreamPort) : null,
        };
        var preview = SquidConfigurationRenderer.Preview(configuration);
        foreach (var code in preview.Errors)
        {
            if (!reported.Add(code)) continue;
            switch (code)
            {
                case "listener.address.invalid":
                case "listener.port.invalid":
                case "listener.remote.opt_in_required":
                    Add(ListenerProblems, Problem(code)); break;
                case "networks.required_or_too_many":
                case "network.invalid_or_unrestricted":
                    Add(SourceProblems, Problem(code)); break;
                case "domains.invalid_or_too_many":
                case "domain.invalid":
                    Add(DomainProblems, Problem(code)); break;
                case "upstream.port.invalid_or_loop":
                case "upstream.host.invalid":
                    Add(UpstreamProblems, Problem(code)); break;
                default:
                    Add(ListenerProblems, Problem(code)); break;
            }
        }
        IsConfigurationValid = preview.Valid;
        // The card summaries, the draft verdict and the readiness line all read these collections,
        // so a keystroke that changes any of them must refresh the derived surface too.
        NotifyActions();
    }

    private static void Add(ObservableCollection<SquidProblem> problems, SquidMessage message) =>
        problems.Add(new SquidProblem(message));

    /// <summary>Maps a renderer problem code to a sentence; unknown codes keep the code visible.</summary>
    private SquidMessage Problem(string code) =>
        new(() => string.Format(CultureInfo.CurrentCulture, _localize("squid.problem." + code, "Configuration rejected: {0}"), code));

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => CanRefresh ? WorkAsync(() => LoadAsync(), true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanLoadStored))]
    private async Task LoadStoredAsync()
    {
        if (!CanLoadStored) return;
        if (HasUnsavedChanges && (ConfirmReloadAsync is null || !await ConfirmReloadAsync())) return;
        await WorkAsync(async () =>
        {
            await LoadAsync(replaceDraft: true);
            PreviewText = "";
            _previewGenerated = false;
            IsPreviewStale = false;
            AdoptExistingConfiguration = false;
        });
    }

    /// <summary>
    /// Reads host facts. The draft is only replaced when the caller asked for it, so a refresh can
    /// never discard an edit the user is still working on.
    /// </summary>
    private async Task LoadAsync(bool replaceDraft = false)
    {
        var snapshot = await _client.ReadAsync(_lifetime.Token);
        Snapshot = snapshot;
        IsUnavailable = false;
        var status = snapshot.Status;
        var selectedName = SelectedAccount?.Username;
        var accounts = status.Supported ? await _client.ReadAccountsAsync(_lifetime.Token) : [];
        Accounts.Clear();
        foreach (var account in accounts) Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(x => x.Username == selectedName);
        NotifyActions();
        var installed = status is { Supported: true, Installed: true };
        ServiceStateText = installed ? L(status.Running ? "running" : "stopped") : Divider();
        BootStateText = installed ? L(status.Enabled ? "enabled" : "disabled") : Divider();
        RevisionText = L("revision_summary", snapshot.State.DesiredRevision,
            snapshot.State.PublishedRevision?.ToString() ?? DividerText);
        DiskStateText = L(snapshot.MatchesPublishedConfiguration ? "matching" : "unconfirmed");
        StatusText = !status.Supported ? L("unsupported") : !status.Installed ? L("install_required")
            : status.RecoveryRequired ? L("recovery_required")
            : L("status", ServiceStateText, BootStateText, snapshot.State.DesiredRevision,
                snapshot.State.PublishedRevision?.ToString() ?? DividerText, DiskStateText);
        if (_initialized && !replaceDraft) return;
        var configuration = snapshot.State.Configuration ?? new SquidConfiguration();
        AccessModeIndex = (int)configuration.AccessMode;
        BindAddress = configuration.BindAddress; Port = configuration.Port.ToString(); AllowRemoteClients = configuration.AllowRemoteClients;
        AllowedNetworks = string.Join('\n', configuration.AllowedNetworks); AllowedDomains = string.Join('\n', configuration.AllowedDomains);
        DeniedDomains = string.Join('\n', configuration.DeniedDomains); UpstreamHost = configuration.Upstream?.Host ?? "";
        UpstreamPort = (configuration.Upstream?.Port ?? 7890).ToString();
        _initialized = true;
        Revalidate();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>Assembles the draft. Structural validity is published as <see cref="IsConfigurationValid"/>.</summary>
    public SquidConfiguration BuildConfiguration()
    {
        if (!int.TryParse(Port, out var port) || (!string.IsNullOrWhiteSpace(UpstreamHost) && !int.TryParse(UpstreamPort, out _)))
            throw new InvalidOperationException(L("invalid_port").Resolve());
        var configuration = new SquidConfiguration
        {
            AccessMode = (SquidAccessMode)AccessModeIndex,
            BindAddress = BindAddress.Trim(), Port = port, AllowRemoteClients = AllowRemoteClients,
            AllowedNetworks = Lines(AllowedNetworks), AllowedDomains = Lines(AllowedDomains), DeniedDomains = Lines(DeniedDomains),
            Upstream = string.IsNullOrWhiteSpace(UpstreamHost) ? null : new(UpstreamHost.Trim(), int.Parse(UpstreamPort)),
        };
        var preview = SquidConfigurationRenderer.Preview(configuration);
        if (!preview.Valid) throw new InvalidOperationException(string.Join('\n', preview.Errors));
        return configuration;
    }

    private static string[] Lines(string value) =>
        value.Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewAsync() => !CanPreview ? Task.CompletedTask : WorkAsync(async () =>
    {
        var preview = await _client.PreviewAsync(BuildConfiguration(), _lifetime.Token);
        PreviewText = preview.Configuration ?? "";
        _previewGenerated = preview.Valid;
        IsPreviewStale = false;
        DetailText = preview.Valid ? L("preview_ready") : Joined(L("invalid"), preview.Errors);
    });

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private Task ValidateAsync() => !CanValidate ? Task.CompletedTask : WorkAsync(async () =>
    {
        var result = await _client.ValidateAsync(BuildConfiguration(), _lifetime.Token);
        DetailText = result.Valid ? L("validation_ok") : Joined(L("invalid"), [result.ProblemCode ?? "", result.Diagnostics]);
    });

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => !CanApply ? Task.CompletedTask : WorkAsync(async () =>
    {
        var configuration = BuildConfiguration();
        PreviewText = SquidConfigurationRenderer.Preview(configuration).Configuration!;
        _previewGenerated = true;
        IsPreviewStale = false;
        if (ConfirmApplyAsync is null || !await ConfirmApplyAsync()) return;
        var request = new ApplySquidConfigurationRequest(configuration, AdoptExistingConfiguration);
        Begin((key, ct) => _client.ApplyAsync(request, key, ct));
        await SubmitAndWatchAsync();
    });

    [RelayCommand(CanExecute = nameof(CanCreateAccount))]
    private Task CreateAccountAsync() => !CanCreateAccount ? Task.CompletedTask
        : SubmitAccountAsync(new(SquidAccountAction.Create, NewAccountName, AccountPassword));

    [RelayCommand(CanExecute = nameof(CanResetAccountPassword))]
    private Task ResetAccountPasswordAsync() => !CanResetAccountPassword ? Task.CompletedTask
        : SubmitAccountAsync(new(SquidAccountAction.SetPassword, SelectedAccount!.Username, AccountPassword));

    [RelayCommand(CanExecute = nameof(CanToggleAccount))]
    private Task ToggleAccountAsync() => !CanToggleAccount ? Task.CompletedTask
        : SubmitAccountAsync(new(SquidAccountAction.SetEnabled, SelectedAccount!.Username, Enabled: !SelectedAccount.Enabled));

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedAccount))]
    private async Task DeleteAccountAsync()
    {
        if (!CanRemoveSelectedAccount) return;
        var username = SelectedAccount!.Username;
        IsBusy = true;
        bool accepted;
        try { accepted = ConfirmDeleteAccountAsync is not null && await ConfirmDeleteAccountAsync(username); }
        finally { IsBusy = false; }
        if (accepted && CanRemoveSelectedAccount && SelectedAccount?.Username == username)
            await SubmitAccountAsync(new(SquidAccountAction.Delete, username));
    }

    private Task SubmitAccountAsync(SquidAccountMutation request) => !CanManageAccounts ? Task.CompletedTask : WorkAsync(async () =>
    {
        Begin((key, ct) => _client.ChangeAccountAsync(request, key, ct));
        // An uncertain request retains its original body only in this window's memory.
        AccountPassword = "";
        await SubmitAndWatchAsync();
    });


    [RelayCommand(CanExecute = nameof(CanStart))] private Task StartAsync() => LifecycleAsync(SquidServiceAction.Start);
    [RelayCommand(CanExecute = nameof(CanStopService))] private Task StopAsync() => LifecycleAsync(SquidServiceAction.Stop);
    [RelayCommand(CanExecute = nameof(CanRestartService))] private Task RestartAsync() => LifecycleAsync(SquidServiceAction.Restart);
    [RelayCommand(CanExecute = nameof(CanReloadService))] private Task ReloadAsync() => LifecycleAsync(SquidServiceAction.Reload);
    [RelayCommand(CanExecute = nameof(CanEnableService))] private Task EnableAsync() => LifecycleAsync(SquidServiceAction.Enable);
    [RelayCommand(CanExecute = nameof(CanDisableService))] private Task DisableAsync() => LifecycleAsync(SquidServiceAction.Disable);

    /// <summary>The single boot control always states what the next click will do.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleBoot))]
    private Task ToggleBootAsync() => LifecycleAsync(IsEnabled ? SquidServiceAction.Disable : SquidServiceAction.Enable);

    /// <summary>Re-checks the gate here as well: CanExecute alone must never authorize a host mutation.</summary>
    private bool Allows(SquidServiceAction action) => action switch
    {
        SquidServiceAction.Start => CanStart,
        SquidServiceAction.Stop => CanStopService,
        SquidServiceAction.Restart => CanRestartService,
        SquidServiceAction.Reload => CanReloadService,
        SquidServiceAction.Enable => CanEnableService,
        SquidServiceAction.Disable => CanDisableService,
        _ => false,
    };

    private Task LifecycleAsync(SquidServiceAction action) => !Allows(action) ? Task.CompletedTask : WorkAsync(async () =>
    {
        var request = new SquidLifecycleRequest(action);
        Begin((key, ct) => _client.RunLifecycleAsync(request, key, ct));
        await SubmitAndWatchAsync();
    });

    private void Begin(Func<string, CancellationToken, Task<ProxyOperationAcceptedDto>> submit)
    {
        _pendingSubmission = submit;
        _pendingKey = Guid.NewGuid().ToString("N");
        PendingReference = _pendingKey;
        OutcomeText = default; OutcomeFailed = false;
        NotifyActions();
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => CanResume ? WorkAsync(SubmitAndWatchAsync) : Task.CompletedTask;

    private async Task SubmitAndWatchAsync()
    {
        if (_operation is null)
        {
            try
            {
                var accepted = await _pendingSubmission!(_pendingKey!, _lifetime.Token);
                _operation = accepted.OperationId;
                PendingReference = accepted.OperationId.ToString();
                NotifyActions();
            }
            catch (HttpRequestException exception) when (exception.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
            {
                // A refused request provably created nothing, so the pending key may be dropped.
                ClearPending(); throw;
            }
        }
        for (var attempt = 0; attempt < 1500; attempt++)
        {
            var operation = await _client.OperationAsync(_operation.Value, _lifetime.Token);
            if (operation.OperationId != _operation || !operation.Kind.StartsWith("squid.", StringComparison.Ordinal))
                throw new InvalidDataException(L("invalid").Resolve());
            ActivityText = L("activity.operation", operation.OperationId.ToString(), L("operation_" + operation.State.ToString().ToLowerInvariant()));
            if (operation.State is not (ProxyOperationState.Queued or ProxyOperationState.Running))
            {
                var failed = operation.State is not ProxyOperationState.Succeeded;
                OutcomeFailed = failed;
                OutcomeText = failed ? L("outcome.failed", ProblemCodeText(operation.ProblemCode)) : L("outcome.succeeded");
                ClearPending();
                AdoptExistingConfiguration = false;
                // The outcome is already decided. A failed re-read must not overwrite it with a
                // transport error, so the facts are retired and the refresh command takes over.
                try { await LoadAsync(); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    DetailText = exception.Message;
                    Snapshot = null;
                    IsUnavailable = CanRead;
                    StatusText = L("unavailable");
                }
                return;
            }
            await Task.Delay(1000, _lifetime.Token);
        }
        DetailText = L("pending");
    }

    private void ClearPending()
    {
        _operation = null; _pendingKey = null; _pendingSubmission = null;
        PendingReference = "";
        NotifyActions();
    }

    private async Task WorkAsync(Func<Task> action, bool clearFactsOnFailure = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        ActivityText = L("activity.busy");
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DetailText = exception.Message;
            OutcomeText = L("outcome.error", exception.Message);
            OutcomeFailed = true;
            // Only a failed read invalidates the facts we show; a refused mutation leaves them standing.
            if (clearFactsOnFailure) { Snapshot = null; IsUnavailable = CanRead; StatusText = L("unavailable"); }
        }
        finally
        {
            IsBusy = false;
            ActivityText = HasPending ? L("activity.unconfirmed") : default;
        }
    }

    /// <summary>
    /// A host refusal is only useful when it says what to change. Known codes become one sentence
    /// that names the address or port involved; an unknown code keeps its raw form so it can be
    /// searched in the server log instead of being swallowed by a generic failure line.
    /// </summary>
    private SquidMessage ProblemCodeText(string? code) => code switch
    {
        SquidFailureAddressUnavailable => L("code.address_unavailable", BindAddress.Trim(), Port),
        SquidFailurePortInUse => L("code.port_in_use", Port),
        "squid.process.failed" => L("code.process_failed"),
        "squid.configuration.parse_failed" => L("code.parse_failed"),
        "squid.configuration.adoption_required" => L("code.adoption_required"),
        "squid.recovery.required" => L("code.recovery_required"),
        "squid.runtime.unavailable" => L("code.runtime_unavailable"),
        null or "" => L("code.unknown"),
        _ => SquidMessage.Literal(code)
    };
    // The two diagnoses the Helper can prove from the unit journal; shared with the Server contract.
    private const string SquidFailureAddressUnavailable = RelaxKonOS.Protocol.GatewayProxy.SquidFailureClassifier.AddressUnavailable;
    private const string SquidFailurePortInUse = RelaxKonOS.Protocol.GatewayProxy.SquidFailureClassifier.PortInUse;

    private SquidMessage L(string key) => new(() => _localize("squid." + key, "squid." + key));
    private SquidMessage L(string key, params object?[] arguments) =>
        new(() => string.Format(CultureInfo.CurrentCulture, _localize("squid." + key, "squid." + key), arguments));
    private static SquidMessage Joined(SquidMessage headline, IEnumerable<string> lines) =>
        SquidMessage.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(SquidMessage.Literal).Prepend(headline));
    private const string DividerText = "—";
    private static SquidMessage Divider() => SquidMessage.Literal(DividerText);

    public void InvalidateSession()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        _pendingSubmission = null; _pendingKey = null; _operation = null; PendingReference = "";
        CanRead = false; CanManage = false; Snapshot = null; IsUnavailable = false;
        AccountPassword = ""; NewAccountName = ""; SelectedAccount = null; Accounts.Clear();
        BindAddress = ""; AllowedNetworks = ""; AllowedDomains = ""; DeniedDomains = ""; UpstreamHost = "";
        PreviewText = ""; DetailText = ""; ActivityText = default; OutcomeText = default; OutcomeFailed = false;
        IsPreviewStale = false; _previewGenerated = false;
        ServiceStateText = Divider(); BootStateText = Divider(); RevisionText = Divider(); DiskStateText = Divider();
        StatusText = L("unavailable");
        ListenerProblems.Clear(); SourceProblems.Clear(); DomainProblems.Clear(); UpstreamProblems.Clear();
    }

    public void Dispose() { if (_disposed) return; InvalidateSession(); _disposed = true; _lifetime.Dispose(); }
}

/// <summary>One renderer problem, localized and attributed to the card that can fix it.</summary>
public sealed record SquidProblem(SquidMessage Message);
