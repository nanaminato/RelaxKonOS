using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Client.Apps.Squid;

/// <summary>
/// Window-local Squid console. Three things are deliberately kept apart: the facts read from the
/// server, the local draft, and a submitted operation whose outcome is not yet confirmed. A draft
/// edit never rewrites a fact, an unconfirmed operation blocks every new mutation, and an action the
/// host would refuse (unmanaged configuration, missing package, interrupted transaction) is disabled
/// with an explanation instead of being offered and failing afterwards.
/// </summary>
public partial class SquidViewModel : ObservableObject, IDisposable
{
    private readonly ISquidClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private bool _disposed;
    private bool _previewGenerated;
    private string? _pendingRoute;
    private object? _pendingBody;
    private string? _pendingKey;
    private Guid? _operation;

    public SquidViewModel(ISquidClient client) => _client = client;

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

    [ObservableProperty] private SquidConfigurationSnapshot? _snapshot;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canRead;
    [ObservableProperty] private bool _canManage;
    [ObservableProperty] private bool _isUnavailable;
    [ObservableProperty] private bool _isConfigurationValid = true;
    [ObservableProperty] private bool _isPreviewStale;
    [ObservableProperty] private bool _outcomeFailed;

    /// <summary>Single-sentence host summary for the status bar and tooltips.</summary>
    [ObservableProperty] private LocalizedStatus _statusText;
    /// <summary>What the window is doing right now; empty when idle.</summary>
    [ObservableProperty] private LocalizedStatus _activityText;
    /// <summary>Native diagnostics or the last transport error. Never a claim about the runtime.</summary>
    [ObservableProperty] private LocalizedStatus _detailText;
    [ObservableProperty] private LocalizedStatus _previewText;
    [ObservableProperty] private LocalizedStatus _serviceStateText;
    [ObservableProperty] private LocalizedStatus _bootStateText;
    [ObservableProperty] private LocalizedStatus _revisionText;
    [ObservableProperty] private LocalizedStatus _diskStateText;
    [ObservableProperty] private LocalizedStatus _outcomeText;
    [ObservableProperty] private string _pendingReference = "";

    /// <summary>Renderer problems, grouped by the card that can fix them.</summary>
    public ObservableCollection<SquidProblem> ListenerProblems { get; } = [];
    public ObservableCollection<SquidProblem> SourceProblems { get; } = [];
    public ObservableCollection<SquidProblem> DomainProblems { get; } = [];
    public ObservableCollection<SquidProblem> UpstreamProblems { get; } = [];

    public Func<bool, Task<bool>>? ConfirmPackageAsync { get; set; }
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
    /// <summary>Only a file carrying the RelaxKonOS header may be started, reloaded or enabled.</summary>
    public bool IsConfigurationAdopted => Snapshot?.Status is { Managed: true };
    public bool ShowUnsupported => Snapshot?.Status is { Supported: false };
    public bool ShowInstallRequired => Snapshot?.Status is { Supported: true, Installed: false };
    public bool ShowRecoveryRequired => Snapshot?.Status is { RecoveryRequired: true };
    public bool ShowAdoptionRequired => Snapshot?.Status is { Supported: true, Installed: true, Managed: false };
    /// <summary>The unsupported notice already explains a host that cannot run Squid.</summary>
    public bool ShowReadOnly => CanRead && !CanManage && Snapshot?.Status is { Supported: true };
    /// <summary>Replacing an unmanaged file requires the explicit ownership opt-in.</summary>
    public bool NeedsAdoption => Snapshot?.Status is { Installed: true, Managed: false } && !AdoptExistingConfiguration;

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
    public bool CanApply => CanMutate && IsConfigurationValid && !NeedsAdoption;
    public bool CanInstall => CanEdit && Snapshot?.Status is { Supported: true, Installed: false, RecoveryRequired: false, PackageManagementSupported: true };
    public bool CanUninstall => CanMutate && Snapshot?.Status.PackageManagementSupported == true;
    public bool ShowPackageUnsupported => Snapshot?.Status is { Supported: true, PackageManagementSupported: false };
    public bool CanStart => CanMutate && !IsRunning && IsConfigurationAdopted;
    public bool CanStopService => CanControlService && IsRunning;
    public bool CanRestartService => CanMutate && IsRunning && IsConfigurationAdopted;
    public bool CanReloadService => CanMutate && IsRunning && IsConfigurationAdopted;
    public bool CanEnableService => CanMutate && !IsEnabled && IsConfigurationAdopted;
    public bool CanDisableService => CanControlService && IsEnabled;
    /// <summary>One boot control instead of two mutually exclusive buttons.</summary>
    public bool CanToggleBoot => CanEnableService || CanDisableService;
    public LocalizedStatus BootActionText => L(IsEnabled ? "disable" : "enable");

    private static readonly string[] DerivedState =
    [
        nameof(ShowPackageUnsupported), nameof(CanInstall), nameof(CanUninstall), nameof(CanEdit), nameof(CanMutate), nameof(CanControlService), nameof(CanRefresh), nameof(CanResume),
        nameof(CanLoadStored), nameof(CanPreview), nameof(CanValidate), nameof(CanApply), nameof(CanStart),
        nameof(CanStopService), nameof(CanRestartService), nameof(CanReloadService), nameof(CanEnableService),
        nameof(CanDisableService), nameof(CanToggleBoot), nameof(BootActionText),
        nameof(HasPending), nameof(HasUnsavedChanges),
        nameof(IsRunning), nameof(IsEnabled), nameof(IsConfigurationAdopted), nameof(ShowUnsupported),
        nameof(ShowInstallRequired), nameof(ShowRecoveryRequired), nameof(ShowAdoptionRequired),
        nameof(ShowReadOnly), nameof(NeedsAdoption)
    ];

    partial void OnIsBusyChanged(bool value) => NotifyActions();
    partial void OnCanReadChanged(bool value) => NotifyActions();
    partial void OnCanManageChanged(bool value) => NotifyActions();
    partial void OnSnapshotChanged(SquidConfigurationSnapshot? value) => NotifyActions();
    partial void OnIsConfigurationValidChanged(bool value) => NotifyActions();
    partial void OnIsPreviewStaleChanged(bool value) => NotifyPreviewState();
    partial void OnAdoptExistingConfigurationChanged(bool value) => NotifyActions();
    partial void OnDetailTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasDetail));
    partial void OnPreviewTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasPreview));
    partial void OnOutcomeTextChanged(LocalizedStatus value) => NotifyOutcome();
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
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
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
    }

    private static void Add(ObservableCollection<SquidProblem> problems, LocalizedStatus message) =>
        problems.Add(new SquidProblem(message));

    /// <summary>Maps a renderer problem code to a sentence; unknown codes keep the code visible.</summary>
    private static LocalizedStatus Problem(string code) =>
        LocalizedText.RefWithFallback("squid.problem." + code, "Configuration rejected: {0}", code);

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
        Begin(GatewayProxyRoutes.Apply, new ApplySquidConfigurationRequest(configuration, AdoptExistingConfiguration));
        await SubmitAndWatchAsync();
    });

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => PackageAsync(false);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallAsync() => PackageAsync(true);

    private async Task PackageAsync(bool uninstall)
    {
        if (!(uninstall ? CanUninstall : CanInstall)) return;
        // Lock submission while the confirmation is open, then re-check permissions/facts.
        IsBusy = true;
        bool accepted;
        try { accepted = ConfirmPackageAsync is not null && await ConfirmPackageAsync(uninstall); }
        finally { IsBusy = false; }
        if (accepted) await LifecycleAsync(uninstall ? SquidServiceAction.Uninstall : SquidServiceAction.Install);
    }

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
        SquidServiceAction.Install => CanInstall,
        SquidServiceAction.Uninstall => CanUninstall,
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
        Begin(GatewayProxyRoutes.Lifecycle, new SquidLifecycleRequest(action));
        await SubmitAndWatchAsync();
    });

    private void Begin(string route, object body)
    {
        _pendingRoute = route; _pendingBody = body; _pendingKey = Guid.NewGuid().ToString("N");
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
                var accepted = await _client.SubmitAsync(_pendingRoute!, _pendingBody!, _pendingKey!, _lifetime.Token);
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
                OutcomeText = failed ? L("outcome.failed", operation.ProblemCode ?? "") : L("outcome.succeeded");
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
        _operation = null; _pendingKey = null; _pendingBody = null; _pendingRoute = null;
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

    private static LocalizedStatus L(string key) => LocalizedText.Ref("squid." + key);
    private static LocalizedStatus L(string key, params object?[] arguments) => LocalizedText.Ref("squid." + key, arguments);
    private static LocalizedStatus Joined(LocalizedStatus headline, IEnumerable<string> lines) =>
        LocalizedStatus.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(LocalizedStatus.Literal).Prepend(headline));
    private const string DividerText = "—";
    private static LocalizedStatus Divider() => LocalizedStatus.Literal(DividerText);

    public void InvalidateSession()
    {
        if (_disposed) return;
        _lifetime.Cancel();
        _pendingBody = null; _pendingKey = null; _pendingRoute = null; _operation = null; PendingReference = "";
        CanRead = false; CanManage = false; Snapshot = null; IsUnavailable = false;
        BindAddress = ""; AllowedNetworks = ""; AllowedDomains = ""; DeniedDomains = ""; UpstreamHost = "";
        PreviewText = ""; DetailText = ""; ActivityText = default; OutcomeText = default; OutcomeFailed = false;
        IsPreviewStale = false; _previewGenerated = false;
        ServiceStateText = Divider(); BootStateText = Divider(); RevisionText = Divider(); DiskStateText = Divider();
        StatusText = L("unavailable");
        ListenerProblems.Clear(); SourceProblems.Clear(); DomainProblems.Clear(); UpstreamProblems.Clear();
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}

/// <summary>One renderer problem, localized and attributed to the card that can fix it.</summary>
public sealed record SquidProblem(LocalizedStatus Message);
