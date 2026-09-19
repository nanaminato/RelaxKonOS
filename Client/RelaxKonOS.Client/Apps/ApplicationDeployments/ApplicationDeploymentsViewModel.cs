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

    public ApplicationDeploymentsViewModel(IRemoteApplicationDeploymentClient client, IAppPermissionScope permissions)
    {
        this.client = client;
        this.permissions = permissions;
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
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLogTruncated;
    [ObservableProperty] private string _logTailText = "200";

    /// <summary>Assigned by the app shell to confirm a destructive action before it reaches the server.</summary>
    public Func<LocalizedStatus, Task<bool>>? ConfirmAsync { get; set; }
    /// <summary>Assigned by the app shell to run the wizard and resolve when it closes.</summary>
    public Func<DeploymentWizardViewModel, Task>? ShowWizardAsync { get; set; }
    /// <summary>Assigned by the app shell to pick a server-side archive path for the wizard.</summary>
    public Func<Task<string?>>? PickServerArchiveAsync { get; set; }
    /// <summary>Assigned by the app shell to pick a local archive path for upload.</summary>
    public Func<Task<string?>>? PickLocalArchiveAsync { get; set; }

    public bool CanRead => permissions.IsGranted(AppPermissions.ServerApplicationDeploymentsRead);
    public bool CanManage => permissions.IsGranted(AppPermissions.ServerApplicationDeploymentsManage);
    public bool HasSelection => SelectedApplication is not null;
    public bool HasActiveOperation => ActiveOperation is not null;
    public bool HasLog => LogLines.Count > 0;
    public bool HasError => !ErrorText.IsEmpty;
    public bool HasStatus => !StatusText.IsEmpty;

    /// <summary>Start, stop, and restart only make sense once a revision has been published.</summary>
    public bool CanControlLifecycle => CanManage && SelectedApplication?.Model.CurrentRevisionId is not null;
    public bool CanRollback => CanManage && SelectedRevision is not null
        && SelectedApplication is not null && SelectedRevision.Id != SelectedApplication.Model.CurrentRevisionId;

    public string CurrentRevisionText => SelectedApplication?.Model.CurrentRevisionNumber is { } number
        ? "#" + number.ToString(System.Globalization.CultureInfo.CurrentCulture)
        : LocalizedText.Get(DeploymentText.Prefix + ".never_deployed");

    public string ContainerText => SelectedApplication?.Model.ContainerName ?? "—";
    public string DomainText => string.IsNullOrWhiteSpace(SelectedApplication?.Model.Domain) ? "—" : SelectedApplication!.Model.Domain;

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

    private async Task LoadAsync()
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
            Replace(await applicationsTask, SelectedApplication?.Id);

            // Reattaching to an operation that is still running is what makes the window safe to reopen
            // in the middle of a deployment.
            if (SelectedApplication is { } selected) await LoadSnapshotAsync(selected.Id);

            StatusText = LocalizedStatus.Format(DeploymentText.Prefix + ".status.loaded", Applications.Count);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
            StatusText = LocalizedStatus.Key(DeploymentText.Prefix + ".status.failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadSnapshotAsync(Guid applicationId)
    {
        var snapshot = await client.GetSnapshotAsync(applicationId);
        if (snapshot is null) return;

        Revisions.Clear();
        foreach (var revision in snapshot.Revisions)
            Revisions.Add(new RevisionRowViewModel(revision, snapshot.Application.CurrentRevisionId));
        Operations.Clear();
        foreach (var operation in snapshot.Operations) Operations.Add(new OperationRowViewModel(operation));

        if (snapshot.ActiveOperation is { } active)
        {
            ActiveOperation = new OperationRowViewModel(active);
            _ = PollAsync(applicationId, active);
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
    private async Task EditDefinitionAsync() => await RunWizardAsync(DeploymentWizardIntent.EditDefinition, SelectedApplication?.Model);

    [RelayCommand]
    private async Task DeployRevisionAsync() => await RunWizardAsync(DeploymentWizardIntent.DeployNewRevision, SelectedApplication?.Model);

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
            ActiveOperation = new OperationRowViewModel(queued);
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
        if (!CanManage || ConfirmAsync is null || SelectedApplication is not { } application) return;
        if (!await ConfirmAsync(LocalizedStatus.Format(DeploymentText.Prefix + ".confirm_delete", application.Name))) return;

        await QueueAsync(application.Id,
            key => client.DeleteAsync(application.Id, new DeleteApplicationRequest(DeleteVolumes: false, Confirmed: true), key),
            deleting: true);
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
            ActiveOperation = new OperationRowViewModel(cancelled);
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
        SelectedRevision = null;
        Revisions.Clear();
        Operations.Clear();
        LogLines.Clear();
        IsLogTruncated = false;
        ActiveOperation = null;
        RefreshDerived();
        if (value is null || suppressSelectionReload) return;
        _ = SelectAsync(value.Id);
    }

    partial void OnSelectedRevisionChanged(RevisionRowViewModel? value) => OnPropertyChanged(nameof(CanRollback));

    partial void OnActiveOperationChanged(OperationRowViewModel? value) => OnPropertyChanged(nameof(HasActiveOperation));

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
    }

    private static bool IsExpected(Exception exception) => exception
        is ApplicationDeploymentClientException or HttpRequestException or InvalidOperationException
        or IOException or UnauthorizedAccessException or TaskCanceledException;

    private static LocalizedStatus Describe(Exception exception) => exception switch
    {
        ApplicationDeploymentClientException failure => DeploymentText.Problem(failure.ProblemCode),
        _ => LocalizedStatus.Key(DeploymentText.Prefix + ".error.transport"),
    };
}
