using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>
/// State for the application-deployment built-in app. It observes the durable operation after every
/// long action instead of waiting on the HTTP request that queued it, so closing the window or losing
/// the connection never loses the outcome.
///
/// It derives from <see cref="LocalizedObservableObject"/> because status, error, and problem text are
/// held as resource keys and must re-resolve when the display language changes.
/// </summary>
public sealed partial class ApplicationDeploymentsViewModel : LocalizedObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private readonly IRemoteApplicationDeploymentClient client;
    private readonly IAppPermissionScope permissions;
    private CancellationTokenSource? polling;
    /// <summary>Set while this view model rewrites the selection itself, so the change handler does not reload twice.</summary>
    private bool suppressSelectionReload;
    /// <summary>The application whose snapshot is on screen, so a refresh of the same row does not
    /// blank the detail before the new snapshot arrives.</summary>
    private Guid? loadedApplicationId;

    public ApplicationDeploymentsViewModel(IRemoteApplicationDeploymentClient client, IAppPermissionScope permissions)
    {
        this.client = client;
        this.permissions = permissions;
        // The empty states and counts are computed from the collections, so they have to follow them.
        Applications.CollectionChanged += (_, _) => RefreshCollections();
        Revisions.CollectionChanged += (_, _) => RefreshCollections();
        Operations.CollectionChanged += (_, _) => RefreshCollections();
    }

    public ObservableCollection<ApplicationRowViewModel> Applications { get; } = [];
    public ObservableCollection<RevisionRowViewModel> Revisions { get; } = [];
    public ObservableCollection<OperationRowViewModel> Operations { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    public IReadOnlyList<ApplicationDeploymentTemplateDto> Templates { get; private set; } = [];

    [ObservableProperty] private ApplicationRowViewModel? _selectedApplication;
    [ObservableProperty] private RevisionRowViewModel? _selectedRevision;
    [ObservableProperty] private OperationRowViewModel? _activeOperation;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private LocalizedStatus _errorText;
    [ObservableProperty] private LocalizedStatus _operationText;
    [ObservableProperty] private bool _isOperationLogExpanded;
    [ObservableProperty] private string _operationLog = string.Empty;
    partial void OnOperationTextChanged(LocalizedStatus value)
    {
        var text = value.Resolve();
        if (string.IsNullOrWhiteSpace(text) || text == _lastOperationMessage) return;
        _lastOperationMessage = text;
        OperationLog = string.Join(Environment.NewLine,
            OperationLog.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Append($"[{DateTime.Now:HH:mm:ss}] {text}").TakeLast(200));
    }
    private string _lastOperationMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLogTruncated;
    [ObservableProperty] private string _logTailText = "200";

    /// <summary>Assigned by the app shell to confirm a destructive action before it reaches the server.</summary>
    public Func<LocalizedStatus, Task<bool>>? ConfirmAsync { get; set; }
    /// <summary>Assigned by the app shell to run the wizard and resolve when it closes.</summary>
    public Func<DeploymentWizardViewModel, Task>? ShowWizardAsync { get; set; }
    public Func<IReadOnlyList<ApplicationCatalogTemplateDto>, Task<CatalogApplicationInstallDto?>>? ShowCatalogAsync { get; set; }
    public Func<ApplicationDto, IReadOnlyList<ApplicationCatalogTemplateDto>, Task<DeploymentOperationDto?>>? ShowCatalogUpgradeAsync { get; set; }
    /// <summary>Assigned by the app shell to pick a server-side archive path for the wizard.</summary>
    public Func<Task<string?>>? PickServerArchiveAsync { get; set; }
    /// <summary>Assigned by the app shell to pick a local archive path for upload.</summary>
    public Func<Task<LocalDeploymentArchive?>>? PickLocalArchiveAsync { get; set; }
    /// <summary>Assigned by the app shell to put the access address on the system clipboard.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }
    /// <summary>Assigned by the app shell to hand a URL to the default browser.</summary>
    public Func<string, Task>? OpenInBrowserAsync { get; set; }

    public bool CanRead => permissions.IsGranted(AppPermissions.ServerApplicationDeploymentsRead);
    public bool CanManage => permissions.IsGranted(AppPermissions.ServerApplicationDeploymentsManage);
    public bool HasSelection => SelectedApplication is not null;
    public bool HasActiveOperation => ActiveOperation is not null;
    public bool HasLog => LogLines.Count > 0;
    public bool HasError => !ErrorText.IsEmpty;
    public bool HasStatus => !StatusText.IsEmpty;

    /// <summary>
    /// A write is never offered while another one is in flight: the server queues durable operations,
    /// but two clicks would still queue two of them, and the second would replace an instance the
    /// first is still building.
    /// </summary>
    public bool CanAct => CanManage && !IsBusy && !IsLoading && !IsOperationRunning;
    public bool CanActOnSelection => CanAct && SelectedApplication is not null && !ShowReinstallAction;
    public bool ShowReinstallAction => SelectedApplication?.Model.DesiredState == ApplicationDesiredState.Retained;
    public bool ShowCatalogUpgradeAction => SelectedApplication?.Model is { CatalogTemplateId: not null, CurrentRevisionId: not null }
        && !ShowReinstallAction;
    public bool ShowManualDeployAction => SelectedApplication?.Model.CatalogTemplateId is null;
    public string RemoveActionText => LocalizedText.Get(DeploymentText.Prefix +
        (SelectedApplication?.Model.CatalogTemplateId is null ? ".delete" : ".catalog.uninstall"));
    public bool IsWorking => IsBusy || IsLoading;

    /// <summary>The bar stays visible after an operation finishes, so "is it running" is asked of the
    /// operation itself rather than of the bar's presence.</summary>
    public bool IsOperationRunning => ActiveOperation?.IsActive is true;

    public bool HasApplications => Applications.Count > 0;
    public bool HasRevisions => Revisions.Count > 0;
    public bool HasOperations => Operations.Count > 0;
    public string ApplicationCountText => LocalizedText.Format(DeploymentText.Prefix + ".application_count", Applications.Count);

    /// <summary>Rollback is reached from the revision it returns to, not from a menu that only works
    /// after selecting a row in a tab the operator may not have opened.</summary>
    public bool HasRevisionSelection => SelectedRevision is not null;

    /// <summary>Selecting the live revision must not read as a rollback that is merely disabled.</summary>
    public bool SelectedRevisionIsCurrent => SelectedRevision is not null
        && SelectedRevision.Id == SelectedApplication?.Model.CurrentRevisionId;
    public bool ShowRollbackHint => HasRevisions && !HasRevisionSelection;
    /// <summary>The rollback line only names a target when one was chosen and it is not the live one.</summary>
    public bool ShowRollbackTarget => HasRevisionSelection && !SelectedRevisionIsCurrent;
    public string RollbackActionText => LocalizedText.Format(DeploymentText.Prefix + ".rollback_to",
        SelectedRevision?.NumberText ?? string.Empty);
    public bool CanCancelActiveOperation => CanManage && !IsLoading && ActiveOperation is { CanCancel: true };

    /// <summary>Start, stop, and restart only make sense once a revision has been published.</summary>
    public bool CanControlLifecycle => CanManage && SelectedApplication?.Model.CurrentRevisionId is not null && !ShowReinstallAction;
    public bool CanRollback => CanActOnSelection && ShowManualDeployAction && SelectedRevision is not null
        && SelectedApplication is not null && SelectedRevision.Id != SelectedApplication.Model.CurrentRevisionId;

    public string CurrentRevisionText => SelectedApplication?.Model.CurrentRevisionNumber is { } number
        ? "#" + number.ToString(System.Globalization.CultureInfo.CurrentCulture)
        : LocalizedText.Get(DeploymentText.Prefix + ".never_deployed");

    public string ContainerText => SelectedApplication?.Model.ContainerName ?? "—";
    public string DomainText => string.IsNullOrWhiteSpace(SelectedApplication?.Model.Domain)
        ? LocalizedText.Get(DeploymentText.Prefix + ".not_configured")
        : SelectedApplication!.Model.Domain;

    /// <summary>
    /// The line under the title. It answers "which build is this?" without opening the revisions tab,
    /// and it names the artifact kind because that decides what every other field below it means.
    /// </summary>
    public string HeaderMetaText => SelectedApplication is { } row
        ? $"{row.SourceText} · {LocalizedText.Get(DeploymentText.Prefix + ".field.revision")} {row.RevisionText}"
        : string.Empty;

    /// <summary>
    /// The observed state itself rather than a word for it, so the badge derives its colour and glyph
    /// from the same table the list dot uses and the two can never disagree.
    /// </summary>
    public ApplicationActualState? SelectedActualState => SelectedApplication?.Model.ActualState;

    /// <summary>True while the instance is starting or stopping, which is what the spinning glyph marks.</summary>
    public bool IsTransitioning => SelectedApplication?.IsTransitioning is true;

    public bool HasSelectedDrift => SelectedApplication?.HasDrift is true;

    public LocalizedStatus SelectedDriftText =>
        SelectedApplication?.DriftText ?? LocalizedStatus.Literal(string.Empty);

    /// <summary>
    /// The readiness verdict. "Ready" means the observed state is running, which is the only claim
    /// reconciliation can support — the configured probe depth is an intention, not a result.
    /// </summary>
    public bool IsReadinessHealthy =>
        SelectedApplication?.Model.ActualState is ApplicationActualState.Running;

    /// <summary>The probe that will be run, upgraded to a verdict once the instance is actually up.</summary>
    public string ReadinessText
    {
        get
        {
            var key = SelectedApplication?.Model.ReadinessLevel switch
            {
                ApplicationReadinessLevel.Process => IsReadinessHealthy ? ".readiness.healthy_process" : ".readiness.process",
                _ => IsReadinessHealthy ? ".readiness.healthy_http" : ".readiness.http",
            };
            return LocalizedText.Get(DeploymentText.Prefix + key);
        }
    }

    /// <summary>
    /// The address an operator can actually dial, or null when the workload publishes no host port.
    /// A wildcard bind is reported on the loopback address, because "0.0.0.0" is not something anyone
    /// can paste into a browser.
    /// </summary>
    public string? EndpointAddress
    {
        get
        {
            if (SelectedApplication?.Model.Endpoint is not { HostPort: { } port } endpoint) return null;
            return $"{DialableHost(endpoint.BindAddress)}:{port.ToString(System.Globalization.CultureInfo.CurrentCulture)}";
        }
    }

    public bool HasEndpointAddress => EndpointAddress is not null;

    /// <summary>
    /// The address as a URL, or null when there is nothing to open. Only an HTTP-probed workload
    /// qualifies: the server's own readiness choice is the statement that this instance serves HTTP,
    /// which is a stronger signal than the workload kind.
    /// </summary>
    public string? EndpointUri => HasEndpointAddress
        && SelectedApplication?.Model.ReadinessLevel is ApplicationReadinessLevel.Http
            ? "http://" + EndpointAddress
            : null;

    public bool CanOpenEndpoint => EndpointUri is not null;

    public string ContainerPortText =>
        SelectedApplication?.Model.ContainerPort.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "—";

    /// <summary>
    /// A lifecycle action is meaningless before the first revision is published.
    /// </summary>
    private bool HasPublishedRevision => SelectedApplication?.Model.CurrentRevisionId is not null;

    /// <summary>The instance is up, or on its way up.</summary>
    private bool IsUpOrStarting => SelectedApplication?.Model.ActualState
        is ApplicationActualState.Running or ApplicationActualState.Starting;

    /// <summary>
    /// Which lifecycle buttons apply is decided here rather than left to the operator to work out.
    /// Start is offered whenever the instance is not up, so an externally stopped container can be
    /// brought back without reasoning about intent versus observation; stop and restart are offered
    /// while the instance is up. A missing permission still leaves the button in place but disabled,
    /// so it reads as "not allowed" rather than "missing".
    /// </summary>
    public bool ShowStartAction => HasPublishedRevision && !ShowReinstallAction && !IsUpOrStarting;
    public bool ShowStopAction => HasPublishedRevision && !ShowReinstallAction && IsUpOrStarting;
    public bool ShowRestartAction => HasPublishedRevision && !ShowReinstallAction && IsUpOrStarting;

    /// <summary>
    /// 0.0.0.0 and :: mean "every interface", which is not an address anyone can dial, so the
    /// loopback address stands in — it is where the published port is reachable from this machine.
    /// </summary>
    private static string DialableHost(string bindAddress) =>
        bindAddress is "0.0.0.0" or "::" or "[::]" or "" ? "127.0.0.1" : bindAddress;

    /// <summary>
    /// The log pane is a single read-only text surface, so the lines are joined here. The server
    /// already sanitized and length-limited each line and the list is bounded by the requested tail.
    /// </summary>
    public string LogText => string.Join(Environment.NewLine, LogLines);

    public async Task StartAsync()
    {
        if (!CanRead)
        {
            StatusText = LocalizedStatus.Key(DeploymentText.Prefix + ".status.no_permission");
            return;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync(Guid? queuedApplicationId = null)
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorText = LocalizedStatus.Literal(string.Empty);
        try
        {
            StatusText = LocalizedStatus.Key(DeploymentText.Prefix + ".status.loading");
            var templatesTask = client.ListTemplatesAsync();
            var applicationsTask = client.ListApplicationsAsync();
            Templates = await templatesTask;
            Replace(await applicationsTask, queuedApplicationId ?? SelectedApplication?.Id);

            // Reattaching to an operation that is still running is what makes the window safe to reopen
            // in the middle of a deployment.
            if (SelectedApplication is { } selected) await LoadSnapshotAsync(selected.Id, observeOperation: queuedApplicationId is null);

            StatusText = LocalizedStatus.Format(DeploymentText.Prefix + ".status.loaded", Applications.Count);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
            StatusText = exception is ApplicationDeploymentClientException { StatusCode: 404 }
                ? LocalizedStatus.Key(DeploymentText.Prefix + ".status.endpoint_unavailable")
                : LocalizedStatus.Key(DeploymentText.Prefix + ".status.failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadSnapshotAsync(Guid applicationId, bool observeOperation = true)
    {
        var snapshot = await client.GetSnapshotAsync(applicationId);
        if (snapshot is null) return;

        Revisions.Clear();
        foreach (var revision in snapshot.Revisions)
            Revisions.Add(new RevisionRowViewModel(revision, snapshot.Application.CurrentRevisionId));
        Operations.Clear();
        foreach (var operation in snapshot.Operations) Operations.Add(Row(operation));

        if (snapshot.ActiveOperation is { } active)
        {
            ActiveOperation = new OperationRowViewModel(active);
            if (observeOperation) _ = PollAsync(applicationId, active);
        }
        else
        {
            ActiveOperation = null;
        }
        RefreshDerived();
    }

    [RelayCommand]
    private async Task NewDeploymentAsync() => await RunWizardAsync(DeploymentWizardIntent.Create, null);

    [RelayCommand]
    private async Task OpenCatalogAsync()
    {
        if (!CanAct || ShowCatalogAsync is null) return;
        IsBusy = true;
        ErrorText = LocalizedStatus.Literal(string.Empty);
        try
        {
            var receipt = await ShowCatalogAsync(await client.ListCatalogAsync());
            if (receipt is null) return;

            await LoadAsync(receipt.Application.Id);
            ActiveOperation = Row(receipt.Operation);
            await PollAsync(receipt.Application.Id, receipt.Operation);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UpgradeCatalogAsync()
    {
        if (!CanAct || !ShowCatalogUpgradeAction || ShowCatalogUpgradeAsync is null || SelectedApplication is not { } application) return;
        IsBusy = true;
        ErrorText = LocalizedStatus.Literal(string.Empty);
        try
        {
            var releases = await client.ListCatalogAsync();
            var candidates = releases.Where(release => release is { Trusted: true, Withdrawn: false, SchemaVersion: "2", Source: "built-in" }
                && release.Id == application.Model.CatalogTemplateId && release.Version != application.Model.CatalogTemplateVersion).ToArray();
            if (await ShowCatalogUpgradeAsync(application.Model, candidates) is { } operation)
                await PollAsync(application.Id, operation);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task EditDefinitionAsync() => await RunWizardAsync(DeploymentWizardIntent.EditDefinition, SelectedApplication?.Model);

    [RelayCommand]
    private async Task DeployRevisionAsync()
    {
        if (ShowManualDeployAction)
            await RunWizardAsync(DeploymentWizardIntent.DeployNewRevision, SelectedApplication?.Model);
    }

    private async Task RunWizardAsync(DeploymentWizardIntent intent, ApplicationDto? existing)
    {
        if (!CanManage || ShowWizardAsync is null) return;
        var wizard = new DeploymentWizardViewModel(client, intent, Templates, existing)
        {
            PickLocalArchiveAsync = PickLocalArchiveAsync,
            PickServerArchiveAsync = PickServerArchiveAsync,
        };
        await ShowWizardAsync(wizard);
        // A failed submission stays visible: the wizard closes with its own error already reported.
        if (!wizard.ErrorText.IsEmpty) ErrorText = wizard.ErrorText;

        var queued = wizard.Operation;
        suppressSelectionReload = true;
        try { await LoadAsync(); }
        finally { suppressSelectionReload = false; }

        if (queued is not null && SelectedApplication is { } selected)
        {
            ActiveOperation = Row(queued);
            _ = PollAsync(selected.Id, queued);
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (!CanRollback || ConfirmAsync is null || SelectedApplication is null || SelectedRevision is null) return;
        var application = SelectedApplication;
        var revision = SelectedRevision;
        if (!await ConfirmAsync(LocalizedStatus.Format(DeploymentText.Prefix + ".confirm_rollback",
                application.Name, revision.NumberText))) return;

        await QueueAsync(application.Id, key => client.RollbackAsync(application.Id,
            new RollbackApplicationRequest(revision.Id, Confirmed: true), key));
    }

    [RelayCommand]
    private async Task StartApplicationAsync() => await ApplyLifecycleAsync("start", new ApplicationLifecycleRequest());

    [RelayCommand]
    private async Task StopApplicationAsync()
    {
        if (SelectedApplication is not { } application || ConfirmAsync is null) return;
        if (!await ConfirmAsync(LocalizedStatus.Format(DeploymentText.Prefix + ".confirm_stop", application.Name))) return;
        await ApplyLifecycleAsync("stop", new ApplicationLifecycleRequest(Confirmed: true));
    }

    [RelayCommand]
    private async Task RestartApplicationAsync()
    {
        if (SelectedApplication is not { } application || ConfirmAsync is null) return;
        if (!await ConfirmAsync(LocalizedStatus.Format(DeploymentText.Prefix + ".confirm_restart", application.Name))) return;
        await ApplyLifecycleAsync("restart", new ApplicationLifecycleRequest(Confirmed: true));
    }

    private async Task ApplyLifecycleAsync(string action, ApplicationLifecycleRequest request)
    {
        if (!CanManage || SelectedApplication is not { } application) return;
        await QueueAsync(application.Id, key => client.LifecycleAsync(application.Id, action, request, key));
    }

    /// <summary>
    /// Deletes the application record and its managed containers. Named volumes are deliberately kept:
    /// deleting application data is a separate, explicitly authorized action, and the stage-1 UI does not
    /// offer it. The API supports it for a flow that confirms it deliberately.
    /// </summary>
    [RelayCommand]
    private async Task DeleteApplicationAsync()
    {
        if (!CanActOnSelection || ConfirmAsync is null || SelectedApplication is not { } application) return;
        var retained = application.Model.CatalogTemplateId is not null && application.Model.CurrentRevisionId is not null;
        if (!await ConfirmAsync(LocalizedStatus.Format(DeploymentText.Prefix +
                (retained ? ".catalog.confirm_uninstall" : ".confirm_delete"), application.Name))) return;

        await QueueAsync(application.Id,
            key => client.DeleteAsync(application.Id, new DeleteApplicationRequest(DeleteVolumes: false, Confirmed: true), key),
            deleting: !retained);
    }

    [RelayCommand]
    private async Task ReinstallCatalogAsync()
    {
        if (!CanAct || !ShowReinstallAction || ConfirmAsync is null || SelectedApplication is not { } application
            || application.Model.CurrentRevisionId is not { } revisionId) return;
        if (!await ConfirmAsync(LocalizedStatus.Format(DeploymentText.Prefix + ".catalog.confirm_reinstall", application.Name))) return;
        await QueueAsync(application.Id, key => client.ReinstallCatalogAsync(application.Id,
            new ReinstallCatalogApplicationRequest(revisionId, Confirmed: true), key));
    }

    /// <summary>Queues one long action and immediately starts observing its durable record.</summary>
    private async Task QueueAsync(Guid applicationId, Func<string, Task<DeploymentOperationDto>> enqueue, bool deleting = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = LocalizedStatus.Literal(string.Empty);
        try
        {
            var operation = await enqueue(Guid.NewGuid().ToString("N"));
            ActiveOperation = new OperationRowViewModel(operation);
            await PollAsync(applicationId, operation, deleting);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CancelOperationAsync()
    {
        if (!CanManage || ActiveOperation is not { CanCancel: true } operation) return;
        try
        {
            var cancelled = await client.CancelOperationAsync(operation.Id, Guid.NewGuid().ToString("N"));
            ActiveOperation = Row(cancelled);
            StatusText = LocalizedStatus.Key(DeploymentText.Prefix + ".status.cancel_requested");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
    }

    /// <summary>
    /// Follows a durable operation to a terminal state. Cancellation is offered by the server only
    /// before its critical section, so a cancel can legitimately arrive after the operation finished.
    /// </summary>
    private async Task PollAsync(Guid applicationId, DeploymentOperationDto operation, bool deleting = false)
    {
        polling?.Cancel();
        polling?.Dispose();
        polling = new CancellationTokenSource();
        var token = polling.Token;
        var current = operation;
        while (current.State is DeploymentOperationState.Queued or DeploymentOperationState.Running)
        {
            ActiveOperation = new OperationRowViewModel(current);
            OperationText = LocalizedStatus.Key(DeploymentText.Enum(DeploymentText.StagePrefix, current.Stage));
            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { return; }
            try
            {
                var refreshed = await client.GetOperationAsync(current.OperationId, token);
                if (refreshed is null) break;
                current = refreshed;
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                ErrorText = Describe(exception);
                return;
            }
        }

        ActiveOperation = new OperationRowViewModel(current);
        OperationText = LocalizedStatus.Key(DeploymentText.Enum(DeploymentText.StatePrefix, current.State));
        if (current.ProblemCode is { Length: > 0 } problem) ErrorText = DeploymentText.Problem(problem);

        // A deleted application no longer exists, so the selection is dropped before reloading.
        if (deleting && current.State == DeploymentOperationState.Succeeded)
        {
            suppressSelectionReload = true;
            try { SelectedApplication = null; }
            finally { suppressSelectionReload = false; }
            Revisions.Clear();
            Operations.Clear();
            LogLines.Clear();
            ActiveOperation = null;
        }
        await LoadAsync();
    }

    /// <summary>
    /// Every operation row is built through the client, so the output of a failed step can be opened
    /// from the history list too — not only from the wizard, whose error disappears when it closes.
    /// </summary>
    private OperationRowViewModel Row(DeploymentOperationDto operation) =>
        new(operation, client.GetOperationDiagnosticsAsync);

    /// <summary>
    /// The address is the one field an operator routinely needs elsewhere (a browser, a curl, a log
    /// line), so copying it is a first-class action rather than a text selection exercise.
    /// </summary>
    [RelayCommand]
    private async Task CopyEndpointAsync()
    {
        if (EndpointAddress is not { } address || CopyToClipboardAsync is null) return;
        try
        {
            await CopyToClipboardAsync(address);
            StatusText = LocalizedStatus.Key(DeploymentText.Prefix + ".status.address_copied");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
    }

    [RelayCommand]
    private async Task OpenEndpointAsync()
    {
        if (EndpointUri is not { } uri || OpenInBrowserAsync is null) return;
        try
        {
            await OpenInBrowserAsync(uri);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
    }

    [RelayCommand]
    private async Task LoadLogsAsync()
    {
        if (!CanRead || SelectedApplication is not { } application) return;
        if (!int.TryParse(LogTailText, out var tail)) tail = 200;
        tail = Math.Clamp(tail, 1, 1000);
        LogTailText = tail.ToString(System.Globalization.CultureInfo.CurrentCulture);
        try
        {
            var logs = await client.GetLogsAsync(application.Id, tail);
            LogLines.Clear();
            // The server already sanitized and length-limited every line; the client adds no framing.
            foreach (var line in logs.Lines) LogLines.Add(line);
            IsLogTruncated = logs.Truncated;
            OnPropertyChanged(nameof(HasLog));
            OnPropertyChanged(nameof(LogText));
            StatusText = LocalizedStatus.Format(DeploymentText.Prefix + ".status.logs_loaded", LogLines.Count);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
    }

    partial void OnSelectedApplicationChanged(ApplicationRowViewModel? value)
    {
        // A refresh rebuilds the rows, so the selection is a new instance with the same id. Clearing
        // the detail then would replace loaded revisions with "none yet" until the snapshot returns —
        // and a refused snapshot would leave that false statement on screen.
        var sameApplication = value is not null && loadedApplicationId == value.Id;
        loadedApplicationId = value?.Id;
        SelectedRevision = null;
        if (!sameApplication)
        {
            Revisions.Clear();
            Operations.Clear();
            LogLines.Clear();
            IsLogTruncated = false;
            ActiveOperation = null;
        }
        RefreshDerived();
        if (value is null || suppressSelectionReload || sameApplication) return;
        _ = SelectAsync(value.Id);
    }

    partial void OnSelectedRevisionChanged(RevisionRowViewModel? value)
    {
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(HasRevisionSelection));
        OnPropertyChanged(nameof(ShowRollbackHint));
        OnPropertyChanged(nameof(RollbackActionText));
        OnPropertyChanged(nameof(SelectedRevisionIsCurrent));
        OnPropertyChanged(nameof(ShowRollbackTarget));
    }

    // Both busy flags gate every write, so they invalidate the same derived set.
    partial void OnIsBusyChanged(bool value) => RefreshWorkState();
    partial void OnIsLoadingChanged(bool value) => RefreshWorkState();

    partial void OnActiveOperationChanged(OperationRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasActiveOperation));
        OnPropertyChanged(nameof(IsOperationRunning));
        OnPropertyChanged(nameof(CanCancelActiveOperation));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanActOnSelection));
    }

    partial void OnErrorTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasStatus));

    private async Task SelectAsync(Guid applicationId)
    {
        try
        {
            ErrorText = LocalizedStatus.Literal(string.Empty);
            await LoadSnapshotAsync(applicationId);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
        }
    }

    private void Replace(IReadOnlyList<ApplicationDto> applications, Guid? preferred)
    {
        suppressSelectionReload = true;
        try
        {
            Applications.Clear();
            foreach (var application in applications) Applications.Add(new ApplicationRowViewModel(application));
            SelectedApplication = preferred is { } id
                ? Applications.FirstOrDefault(row => row.Id == id) ?? Applications.FirstOrDefault()
                : Applications.FirstOrDefault();
        }
        finally
        {
            suppressSelectionReload = false;
        }
    }

    private void RefreshWorkState()
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsOperationRunning));
        OnPropertyChanged(nameof(CanControlLifecycle));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanCancelActiveOperation));
    }

    /// <summary>Counts and empty states derive from the collections, not from a property change.</summary>
    private void RefreshCollections()
    {
        OnPropertyChanged(nameof(HasApplications));
        OnPropertyChanged(nameof(HasRevisions));
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(ApplicationCountText));
        OnPropertyChanged(nameof(ShowRollbackHint));
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanControlLifecycle));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CurrentRevisionText));
        OnPropertyChanged(nameof(ContainerText));
        OnPropertyChanged(nameof(DomainText));
        OnPropertyChanged(nameof(HasLog));
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HeaderMetaText));
        OnPropertyChanged(nameof(SelectedActualState));
        OnPropertyChanged(nameof(IsTransitioning));
        OnPropertyChanged(nameof(HasSelectedDrift));
        OnPropertyChanged(nameof(SelectedDriftText));
        OnPropertyChanged(nameof(IsReadinessHealthy));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(EndpointAddress));
        OnPropertyChanged(nameof(HasEndpointAddress));
        OnPropertyChanged(nameof(EndpointUri));
        OnPropertyChanged(nameof(CanOpenEndpoint));
        OnPropertyChanged(nameof(ContainerPortText));
        OnPropertyChanged(nameof(ShowStartAction));
        OnPropertyChanged(nameof(ShowReinstallAction));
        OnPropertyChanged(nameof(ShowCatalogUpgradeAction));
        OnPropertyChanged(nameof(ShowManualDeployAction));
        OnPropertyChanged(nameof(RemoveActionText));
        OnPropertyChanged(nameof(ShowStopAction));
        OnPropertyChanged(nameof(ShowRestartAction));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanCancelActiveOperation));
        OnPropertyChanged(nameof(HasRevisionSelection));
        OnPropertyChanged(nameof(ShowRollbackHint));
        OnPropertyChanged(nameof(RollbackActionText));
    }

    private static bool IsExpected(Exception exception) => exception
        is ApplicationDeploymentClientException or HttpRequestException or InvalidOperationException
        or IOException or UnauthorizedAccessException or TaskCanceledException
        // Launching the default browser is the one place this view model starts a process, and a
        // machine with no registered handler fails the shell call rather than the HTTP call.
        or System.ComponentModel.Win32Exception;

    private static LocalizedStatus Describe(Exception exception) => exception switch
    {
        ApplicationDeploymentClientException failure => DeploymentText.Problem(failure.ProblemCode),
        // Handing the address to the default browser is the one action here that leaves the client
        // entirely, so a failure is the host having no registered browser rather than a lost
        // connection, and it must not be reported as one.
        System.ComponentModel.Win32Exception =>
            LocalizedStatus.Key(DeploymentText.Prefix + ".error.open_address_failed"),
        _ => LocalizedStatus.Key(DeploymentText.Prefix + ".error.transport"),
    };
}
