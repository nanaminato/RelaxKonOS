using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Hubs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;

/// <summary>Which of the three wizard intents is being carried out.</summary>
public enum DeploymentWizardIntent
{
    /// <summary>Define a new application, and optionally publish its first revision.</summary>
    Create,
    /// <summary>Change the stored definition only. No revision is published.</summary>
    EditDefinition,
    /// <summary>Publish a new revision of an existing application from a new source.</summary>
    DeployNewRevision,
}

/// <summary>
/// One selectable enum value with its localized caption. The caption is resolved when the list is
/// built, which is correct for a wizard: a wizard is short-lived and is rebuilt in the current
/// language the next time it opens.
/// </summary>
public sealed record DeploymentOption<TValue>(TValue Value, string Label) where TValue : struct;

/// <summary>
/// A locally selected deployment archive. The picker owns the storage handle, so upload reads its
/// stream directly instead of requiring an optional local filesystem path.
/// </summary>
public sealed record LocalDeploymentArchive(string FileName, Func<Task<Stream>> OpenReadAsync);

/// <summary>The ordered wizard steps, matching the order the design fixes for the deployment flow.</summary>
public enum DeploymentWizardStep
{
    Source,
    Entry,
    Runtime,
    Configuration,
    Proxy,
    Preview,
    Progress,
}

/// <summary>
/// The deployment wizard. It owns the whole submission — create the definition, then queue the
/// deployment — because the final step is the operation's own progress; the wizard is what turns a
/// form into one durable operation and then observes it to a terminal state.
///
/// It derives from <see cref="LocalizedObservableObject"/> so validation and problem text held as
/// resource keys re-resolve when the display language changes while the wizard is open.
/// </summary>
public sealed partial class DeploymentWizardViewModel : LocalizedObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private readonly IRemoteApplicationDeploymentClient client;
    private readonly Guid? applicationId;
    private readonly ApplicationDto? existing;
    private readonly string definitionIdempotencyKey = Guid.NewGuid().ToString("N");
    private readonly string deploymentIdempotencyKey = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? polling;
    private CancellationTokenSource? upload;
    private long liveVersion = -1;

    public DeploymentWizardViewModel(
        IRemoteApplicationDeploymentClient client,
        DeploymentWizardIntent intent,
        IReadOnlyList<ApplicationDeploymentTemplateDto> templates,
        ApplicationDto? existing = null)
    {
        this.client = client;
        this.existing = existing;
        applicationId = existing?.Id;
        Intent = intent;
        foreach (var template in templates) Templates.Add(template);

        // A pick-list binds the whole option object, so the three selections are resolved here and the
        // enum each one carries stays a read-only projection. The scalar fields are filled afterwards so
        // the worker rule below cannot erase a value that was read back from the stored definition.
        initializing = true;
        try
        {
            SelectedSourceKind = Match(SourceKinds, existing?.SourceKind ?? ApplicationSourceKind.Image);
            SelectedWorkloadKind = Match(WorkloadKinds, existing?.WorkloadKind ?? ApplicationWorkloadKind.Web);
            SelectedReadinessLevel = Match(ReadinessLevels, existing?.ReadinessLevel ?? ApplicationReadinessLevel.Http);

            if (existing is not null)
            {
                Name = existing.Name;
                HealthCheckPath = existing.HealthCheckPath ?? string.Empty;
                ContainerPort = existing.ContainerPort.ToString(CultureInfo.CurrentCulture);
                HostPort = existing.HostPort?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
                BindAddress = existing.BindAddress;
                CpuCores = existing.Limits.CpuCores?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
                MemoryMegabytes = existing.Limits.MemoryBytes is { } bytes
                    ? (bytes / (1024 * 1024)).ToString(CultureInfo.CurrentCulture)
                    : string.Empty;
                PidsLimit = existing.Limits.PidsLimit?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
                SiteId = existing.SiteId ?? string.Empty;
                VolumesText = string.Join('\n', existing.Volumes.Select(volume =>
                    $"{volume.Name}:{volume.ContainerPath}{(volume.ReadOnly ? ":ro" : string.Empty)}"));
                // A secret is never echoed back; only its name and version travel to the client, so the
                // secret editor lists the names and a new value is required only when rotating.
                SecretConfigText = string.Join('\n', existing.Configuration.Where(entry => entry.IsSecret).Select(entry => entry.Name));
                ConfigText = string.Join('\n', existing.Configuration
                    .Where(entry => !entry.IsSecret && entry.Value is not null)
                    .Select(entry => $"{entry.Name}={entry.Value}"));
            }
        }
        finally
        {
            initializing = false;
        }

        if (intent == DeploymentWizardIntent.EditDefinition) DeployNow = false;
        ValidateCurrentStep();
    }

    private static DeploymentOption<TValue> Match<TValue>(IReadOnlyList<DeploymentOption<TValue>> options, TValue value)
        where TValue : struct =>
        options.FirstOrDefault(option => EqualityComparer<TValue>.Default.Equals(option.Value, value)) ?? options[0];

    public DeploymentWizardIntent Intent { get; }
    public ObservableCollection<ApplicationDeploymentTemplateDto> Templates { get; } = [];

    /// <summary>Localized pick-lists for the three enum choices the wizard presents.</summary>
    public IReadOnlyList<DeploymentOption<ApplicationSourceKind>> SourceKinds { get; } =
        [.. Enum.GetValues<ApplicationSourceKind>().Select(kind =>
            new DeploymentOption<ApplicationSourceKind>(kind, LocalizedText.Get(DeploymentText.Enum(DeploymentText.SourcePrefix, kind))))];

    public IReadOnlyList<DeploymentOption<ApplicationWorkloadKind>> WorkloadKinds { get; } =
        [.. Enum.GetValues<ApplicationWorkloadKind>().Select(kind =>
            new DeploymentOption<ApplicationWorkloadKind>(kind, LocalizedText.Get(DeploymentText.Enum(DeploymentText.WorkloadPrefix, kind))))];

    public IReadOnlyList<DeploymentOption<ApplicationReadinessLevel>> ReadinessLevels { get; } =
        [.. Enum.GetValues<ApplicationReadinessLevel>().Select(level =>
            new DeploymentOption<ApplicationReadinessLevel>(level, LocalizedText.Get(DeploymentText.Enum(DeploymentText.ReadinessPrefix, level))))];

    /// <summary>Assigned by the app shell to pick a local archive and return a readable stream for upload.</summary>
    public Func<Task<LocalDeploymentArchive?>>? PickLocalArchiveAsync { get; set; }
    /// <summary>Assigned by the app shell to pick an archive that already lives on the server.</summary>
    public Func<Task<string?>>? PickServerArchiveAsync { get; set; }
    /// <summary>Assigned by the app shell so the wizard can ask to be closed.</summary>
    public Action? CloseRequested { get; set; }

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private LocalizedStatus _errorText;
    [ObservableProperty] private bool _isBusy;

    // --- Definition ---------------------------------------------------------------------------
    [ObservableProperty] private string _name = string.Empty;
    // The bound surface is the option object; the enum is read back from it so a pick-list selection
    // can never disagree with what the form actually submits.
    [ObservableProperty] private DeploymentOption<ApplicationSourceKind>? _selectedSourceKind;
    [ObservableProperty] private DeploymentOption<ApplicationWorkloadKind>? _selectedWorkloadKind;
    [ObservableProperty] private DeploymentOption<ApplicationReadinessLevel>? _selectedReadinessLevel;

    /// <summary>Set while the constructor fills the form, so the worker rule does not run before the
    /// stored definition has been read back.</summary>
    private bool initializing;

    public ApplicationSourceKind SourceKind => SelectedSourceKind?.Value ?? ApplicationSourceKind.Image;
    public ApplicationWorkloadKind WorkloadKind => SelectedWorkloadKind?.Value ?? ApplicationWorkloadKind.Web;
    public ApplicationReadinessLevel ReadinessLevel => SelectedReadinessLevel?.Value ?? ApplicationReadinessLevel.Http;

    // --- Source -------------------------------------------------------------------------------
    [ObservableProperty] private string _imageReference = string.Empty;
    [ObservableProperty] private string _baseImage = string.Empty;
    [ObservableProperty] private string _runtimeVersion = string.Empty;
    [ObservableProperty] private string _programEntry = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private bool _selfContained;
    [ObservableProperty] private string _archiveReferenceId = string.Empty;
    [ObservableProperty] private string _archiveFileName = string.Empty;

    // --- Ports and network --------------------------------------------------------------------
    [ObservableProperty] private string _containerPort = "8080";
    [ObservableProperty] private string _hostPort = string.Empty;
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private string _healthCheckPath = "/";

    // --- Configuration, volumes and resources -------------------------------------------------
    [ObservableProperty] private string _configText = string.Empty;
    [ObservableProperty] private string _secretConfigText = string.Empty;
    [ObservableProperty] private string _volumesText = string.Empty;
    [ObservableProperty] private string _cpuCores = string.Empty;
    [ObservableProperty] private string _memoryMegabytes = string.Empty;
    [ObservableProperty] private string _pidsLimit = string.Empty;

    // --- Optional reverse proxy ---------------------------------------------------------------
    [ObservableProperty] private string _siteId = string.Empty;

    // --- Preview and progress -----------------------------------------------------------------
    [ObservableProperty] private bool _deployNow = true;
    [ObservableProperty] private LocalizedStatus _previewText;
    [ObservableProperty] private LocalizedStatus _operationText;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _liveLogText = string.Empty;
    [ObservableProperty] private LocalizedStatus _liveLogStatus;
    [ObservableProperty] private bool _isLiveLogTruncated;
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private double _uploadPercent;
    [ObservableProperty] private bool _uploadIndeterminate;
    [ObservableProperty] private string _uploadProgressText = string.Empty;
    public bool HasUploadProgress => UploadProgressText.Length > 0;
    partial void OnUploadProgressTextChanged(string value) => OnPropertyChanged(nameof(HasUploadProgress));
    /// <summary>What the step that failed actually printed. It is the difference between "构建镜像失败。"
    /// and an operator knowing which layer, which file, or which registry refused.</summary>
    [ObservableProperty] private string _diagnosticsText = string.Empty;
    [ObservableProperty] private bool _isDiagnosticsTruncated;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private bool _submitted;

    public int StepCount => 7;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsPreviewStep => StepIndex == (int)DeploymentWizardStep.Preview;
    public bool IsProgressStep => StepIndex == (int)DeploymentWizardStep.Progress;
    public bool CanGoBack => StepIndex > 0 && !IsProgressStep;
    public bool CanGoForward => !IsPreviewStep && !IsProgressStep;
    public bool HasError => !ErrorText.IsEmpty;
    public bool HasStatus => !StatusText.IsEmpty;
    /// <summary>True only once a failed step's own output has actually been loaded, so the pane is
    /// never revealed empty while the request is still in flight.</summary>
    public bool HasDiagnostics => DiagnosticsText.Length > 0;

    // One visibility flag per step. The wizard keeps every step in the same view and reveals the
    // current one, so a partially filled form survives stepping backwards without extra state.
    public bool ShowSourceStep => StepIndex == (int)DeploymentWizardStep.Source;
    public bool ShowEntryStep => StepIndex == (int)DeploymentWizardStep.Entry;
    public bool ShowRuntimeStep => StepIndex == (int)DeploymentWizardStep.Runtime;
    public bool ShowConfigurationStep => StepIndex == (int)DeploymentWizardStep.Configuration;
    public bool ShowProxyStep => StepIndex == (int)DeploymentWizardStep.Proxy;
    public bool ShowPreviewStep => IsPreviewStep;
    public bool ShowProgressStep => IsProgressStep;

    public bool IsWebWorkload => WorkloadKind == ApplicationWorkloadKind.Web;
    public bool IsReadinessHttp => ReadinessLevel == ApplicationReadinessLevel.Http;
    /// <summary>True once an archive has been staged, so the wizard can show its name instead of a hint.</summary>
    public bool HasArchive => ArchiveFileName.Length > 0;
    public bool RequiresArchive => SelectedTemplate?.RequiresArchive ?? SourceKind != ApplicationSourceKind.Image;
    public bool RequiresImageReference => SelectedTemplate?.RequiresImageReference ?? SourceKind == ApplicationSourceKind.Image;
    public bool SupportsSelfContained => SelectedTemplate?.SupportsSelfContained ?? SourceKind == ApplicationSourceKind.DotNetPublish;

    public ApplicationDeploymentTemplateDto? SelectedTemplate => Templates.FirstOrDefault(template => template.SourceKind == SourceKind);

    public string StepCounterText => string.Format(CultureInfo.CurrentCulture, "{0} / {1}",
        (StepIndex + 1).ToString(CultureInfo.CurrentCulture), StepCount.ToString(CultureInfo.CurrentCulture));

    /// <summary>The queued operation, exposed so the shell can select it after the wizard closes.</summary>
    public DeploymentOperationDto? Operation { get; private set; }

    /// <summary>
    /// True only when submitting will queue a revision. Editing a definition publishes nothing, which
    /// is what keeps a definition edit from silently replacing a running instance.
    /// </summary>
    public bool PublishesRevision => DeployNow && Intent != DeploymentWizardIntent.EditDefinition;

    /// <summary>A definition edit may not choose a different deployment source, so those steps are hidden.</summary>
    public bool IsEditingDefinition => Intent == DeploymentWizardIntent.EditDefinition;

    /// <summary>A step's output arrives after the failure it explains, so the pane's own visibility
    /// follows the text rather than the operation's state.</summary>
    partial void OnDiagnosticsTextChanged(string value) => OnPropertyChanged(nameof(HasDiagnostics));

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsPreviewStep));
        OnPropertyChanged(nameof(IsProgressStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ShowSourceStep));
        OnPropertyChanged(nameof(ShowEntryStep));
        OnPropertyChanged(nameof(ShowRuntimeStep));
        OnPropertyChanged(nameof(ShowConfigurationStep));
        OnPropertyChanged(nameof(ShowProxyStep));
        OnPropertyChanged(nameof(ShowPreviewStep));
        OnPropertyChanged(nameof(ShowProgressStep));
        OnPropertyChanged(nameof(StepCounterText));
        // The preview is composed from every earlier step, so it is rebuilt when it becomes visible.
        if (IsPreviewStep) RefreshPreview();
    }

    partial void OnSelectedWorkloadKindChanged(DeploymentOption<ApplicationWorkloadKind>? value)
    {
        OnPropertyChanged(nameof(WorkloadKind));
        OnPropertyChanged(nameof(IsWebWorkload));
        // A worker publishes no HTTP endpoint, so HTTP readiness and a host port stop applying.
        if (!initializing && !IsWebWorkload && IsReadinessHttp)
        {
            SelectedReadinessLevel = Match(ReadinessLevels, ApplicationReadinessLevel.Process);
            HealthCheckPath = string.Empty;
        }
        ValidateCurrentStep();
    }

    partial void OnSelectedReadinessLevelChanged(DeploymentOption<ApplicationReadinessLevel>? value)
    {
        OnPropertyChanged(nameof(ReadinessLevel));
        OnPropertyChanged(nameof(IsReadinessHttp));
        ValidateCurrentStep();
    }

    partial void OnSelectedSourceKindChanged(DeploymentOption<ApplicationSourceKind>? value)
    {
        OnPropertyChanged(nameof(SourceKind));
        OnPropertyChanged(nameof(SelectedTemplate));
        OnPropertyChanged(nameof(RequiresArchive));
        OnPropertyChanged(nameof(RequiresImageReference));
        OnPropertyChanged(nameof(SupportsSelfContained));
        if (!RequiresArchive)
        {
            ArchiveReferenceId = string.Empty;
            ArchiveFileName = string.Empty;
            StatusText = LocalizedStatus.Literal(string.Empty);
        }
        ValidateCurrentStep();
    }

    partial void OnArchiveFileNameChanged(string value) => OnPropertyChanged(nameof(HasArchive));

    [RelayCommand]
    private async Task ChooseLocalArchiveAsync()
    {
        if (IsBusy || PickLocalArchiveAsync is null) return;
        var archive = await PickLocalArchiveAsync();
        if (archive is null) return;
        using var cancellation = new CancellationTokenSource();
        upload = cancellation;
        IsUploading = true;
        UploadPercent = 0;
        UploadIndeterminate = true;
        UploadProgressText = LocalizedText.Get(DeploymentText.Prefix + ".upload_starting");
        try
        {
            await StageAsync(async () =>
            {
                await using var stream = await archive.OpenReadAsync();
                return await client.UploadArchiveAsync(archive.FileName, stream,
                    new Progress<DeploymentUploadProgress>(ReportUpload), cancellation.Token);
            });
        }
        finally { IsUploading = false; upload = null; }
    }

    private void ReportUpload(DeploymentUploadProgress progress)
    {
        if (!IsUploading) return;
        UploadIndeterminate = progress.TotalBytes is not > 0;
        UploadPercent = progress.TotalBytes is > 0 ? Math.Clamp(100d * progress.Bytes / progress.TotalBytes.Value, 0, 100) : 0;
        var speed = progress.Bytes / Math.Max(0.001, progress.Elapsed.TotalSeconds);
        UploadProgressText = progress.TotalBytes is > 0 && progress.Bytes >= progress.TotalBytes.Value
            ? LocalizedText.Get(DeploymentText.Prefix + ".upload_staging")
            : progress.TotalBytes is null ? LocalizedText.Format(DeploymentText.Prefix + ".upload_progress_bytes", FormatBytes(progress.Bytes), FormatBytes(speed))
            : LocalizedText.Format(DeploymentText.Prefix + ".upload_progress", FormatBytes(progress.Bytes),
                progress.TotalBytes is { } total ? FormatBytes(total) : "—", UploadPercent.ToString("F1", CultureInfo.CurrentCulture), FormatBytes(speed));
    }

    private static string FormatBytes(double bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024 * 1024):F1} MiB" : $"{bytes / 1024:F1} KiB";

    [RelayCommand] private void CancelUpload() => upload?.Cancel();

    public void StopObserving()
    {
        upload?.Cancel();
        polling?.Cancel();
    }

    [RelayCommand]
    private async Task ChooseServerArchiveAsync()
    {
        if (IsBusy || PickServerArchiveAsync is null) return;
        var path = await PickServerArchiveAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        await StageAsync(() => client.CreateFileReferenceAsync(path));
    }

    [RelayCommand]
    private void Next()
    {
        if (!TryValidateCurrentStep()) return;
        // The preview is the last step the operator can leave forward: submitting is what moves on.
        if (IsPreviewStep || IsProgressStep) return;
        // Moving the index re-runs the validation for the step that just became visible.
        StepIndex++;
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex <= 0) return;
        StepIndex--;
        ValidateCurrentStep();
    }

    /// <summary>
    /// Performs the submission: create the definition when starting from nothing, update it when
    /// editing, then queue the deployment. Each mutating call is idempotent, so a retry after a
    /// transport failure resolves to the same operation instead of publishing a second revision.
    /// </summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsBusy || Submitted) return;
        if (!ValidateAll()) return;
        IsBusy = true;
        try
        {
            var targetId = applicationId;
            if (Intent == DeploymentWizardIntent.Create)
            {
                var created = await client.CreateApplicationAsync(BuildCreateRequest(), definitionIdempotencyKey);
                targetId = created.Id;
            }
            else if (Intent == DeploymentWizardIntent.EditDefinition)
            {
                await client.UpdateApplicationAsync(applicationId!.Value, BuildUpdateRequest(), definitionIdempotencyKey);
            }

            StepIndex = (int)DeploymentWizardStep.Progress;
            if (!PublishesRevision)
            {
                Submitted = true;
                IsFinished = true;
                OperationText = LocalizedStatus.Key(Intent == DeploymentWizardIntent.EditDefinition
                    ? DeploymentText.Prefix + ".definition_saved"
                    : DeploymentText.Prefix + ".definition_created");
                return;
            }

            OperationText = LocalizedStatus.Key(DeploymentText.Prefix + ".queuing");
            var operation = await client.DeployAsync(targetId!.Value,
                new DeployApplicationRequest(BuildSource(), Confirmed: true), deploymentIdempotencyKey);
            Submitted = true;
            await PollAsync(operation);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
            StepIndex = (int)DeploymentWizardStep.Progress;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        StopObserving();
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Follows the durable operation to a terminal state. Polling the operation — rather than the HTTP
    /// request that created it — is what makes progress survive a client reconnect.
    /// </summary>
    private async Task PollAsync(DeploymentOperationDto operation)
    {
        polling?.Cancel();
        polling?.Dispose();
        polling = new CancellationTokenSource();
        var token = polling.Token;
        var current = operation;
        liveVersion = -1;
        LiveLogStatus = LocalizedStatus.Key(DeploymentText.Prefix + ".logs_connecting");
        await using var live = client.WatchLogs(operation.OperationId,
            snapshot => Dispatcher.UIThread.Post(() =>
            {
                if (snapshot.OperationId != operation.OperationId || snapshot.Version <= liveVersion) return;
                liveVersion = snapshot.Version;
                LiveLogText = string.Join(Environment.NewLine, snapshot.Lines.Select(line =>
                    $"{line.Timestamp.LocalDateTime:HH:mm:ss} {(line.Stage is { } stage ? LocalizedText.Get(DeploymentText.Enum(DeploymentText.StagePrefix, stage)) + " " : string.Empty)}{line.Message}"));
                IsLiveLogTruncated = snapshot.Truncated;
            }), connected => Dispatcher.UIThread.Post(() =>
            {
                if (!IsFinished) LiveLogStatus = LocalizedStatus.Key(DeploymentText.Prefix + (connected ? ".logs_live" : ".logs_connecting"));
            }));
        while (current.State is DeploymentOperationState.Queued or DeploymentOperationState.Running)
        {
            ReportStage(current);
            try { await Task.Delay(PollInterval, token); }
            catch (OperationCanceledException) { return; }
            try
            {
                var refreshed = await client.GetOperationAsync(current.OperationId, token);
                if (refreshed is null) break;
                current = refreshed;
                ErrorText = LocalizedStatus.Literal(string.Empty);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                // Keep the SignalR observer alive during transient REST failures too. The durable
                // operation continues on the server; only a new read can establish its outcome.
                ErrorText = Describe(exception);
                ProgressText = LocalizedText.Get(DeploymentText.Prefix + ".progress_unavailable");
                Operation = current;
                continue;
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                // Non-transient permission/protocol errors need operator attention.
                ErrorText = Describe(exception);
                ProgressText = LocalizedText.Get(DeploymentText.Prefix + ".progress_unavailable");
                Operation = current;
                return;
            }
        }

        Operation = current;
        IsFinished = true;
        LiveLogStatus = LocalizedStatus.Key(DeploymentText.Prefix + ".logs_finished");
        ReportStage(current);
        if (current.ProblemCode is { Length: > 0 } problem)
        {
            ErrorText = DeploymentText.Problem(problem);
            // The problem code names the failure; the step's own output is what explains it. Fetching it
            // here means the operator never has to know that a second request exists.
            await LoadDiagnosticsAsync(current.OperationId, token);
        }
        else if (current.RecoveryProblemCode is { Length: > 0 } recovery)
        {
            ErrorText = DeploymentText.Problem(recovery);
        }
    }

    /// <summary>
    /// Loads the output of the step that produced the failure. A load failure stays silent on purpose:
    /// the diagnosis is supplementary, so it must never replace the failure the operator already sees
    /// with a transport error of its own.
    /// </summary>
    private async Task LoadDiagnosticsAsync(Guid operationId, CancellationToken token)
    {
        try
        {
            var diagnostics = await client.GetOperationDiagnosticsAsync(operationId, token);
            if (diagnostics is not { Lines.Count: > 0 }) return;
            // The server already sanitized and length-limited every line; the client adds no framing.
            DiagnosticsText = string.Join(Environment.NewLine, diagnostics.Lines);
            IsDiagnosticsTruncated = diagnostics.Truncated;
        }
        catch (Exception exception) when (IsExpected(exception)) { }
    }

    private void ReportStage(DeploymentOperationDto operation)
    {
        ProgressText = operation.Progress is { } percent
            ? percent.ToString(CultureInfo.CurrentCulture) + "%"
            : string.Empty;
        if (operation.State is DeploymentOperationState.Queued or DeploymentOperationState.Running)
            OperationText = LocalizedStatus.Key(DeploymentText.Enum(DeploymentText.StagePrefix, operation.Stage));
        else
            OperationText = LocalizedStatus.Key(DeploymentText.Enum(DeploymentText.StatePrefix, operation.State));
    }

    private async Task StageAsync(Func<Task<DeploymentStagedFileDto>> stage)
    {
        IsBusy = true;
        ErrorText = LocalizedStatus.Literal(string.Empty);
        try
        {
            var staged = await stage();
            ArchiveReferenceId = staged.ReferenceId;
            ArchiveFileName = staged.FileName;
            StatusText = LocalizedStatus.Format(DeploymentText.Prefix + ".archive_staged", staged.FileName);
            if (IsUploading) UploadProgressText = LocalizedText.Format(DeploymentText.Prefix + ".archive_staged", staged.FileName);
            ValidateCurrentStep();
        }
        catch (OperationCanceledException) when (upload?.IsCancellationRequested == true)
        {
            ErrorText = LocalizedStatus.Literal(string.Empty);
            UploadProgressText = LocalizedText.Get(DeploymentText.Prefix + ".upload_cancelled");
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorText = Describe(exception);
            if (IsUploading) UploadProgressText = ErrorText.ToString();
        }
        finally
        {
            IsBusy = false;
            UploadIndeterminate = false;
        }
    }

    private CreateApplicationRequest BuildCreateRequest() => new(
        Name.Trim(),
        SourceKind,
        WorkloadKind,
        ReadinessLevel,
        IsReadinessHttp ? HealthCheckPath.Trim() : null,
        ParsePort(ContainerPort) ?? 8080,
        ParseOptionalPort(HostPort),
        BindAddress,
        BuildLimits(),
        BuildVolumes(),
        BuildConfiguration(),
        NullIfBlank(SiteId));

    private UpdateApplicationRequest BuildUpdateRequest() => new(
        Name.Trim(),
        WorkloadKind,
        ReadinessLevel,
        IsReadinessHttp ? HealthCheckPath.Trim() : null,
        ParsePort(ContainerPort) ?? 8080,
        ParseOptionalPort(HostPort),
        BindAddress,
        BuildLimits(),
        BuildVolumes(),
        BuildConfiguration(),
        NullIfBlank(SiteId));

    private DeploymentSourceInputDto BuildSource() => new(
        NullIfBlank(ImageReference),
        NullIfBlank(BaseImage),
        RequiresArchive ? NullIfBlank(ArchiveReferenceId) : null,
        NullIfBlank(RuntimeVersion),
        NullIfBlank(ProgramEntry),
        ParseArguments(),
        SelfContained);

    private ApplicationResourceLimitsDto? BuildLimits()
    {
        var cpu = ParseDouble(CpuCores);
        var memory = ParseLong(MemoryMegabytes);
        var pids = ParsePort(PidsLimit);
        return cpu is null && memory is null && pids is null
            ? null
            : new ApplicationResourceLimitsDto(cpu, memory is { } megabytes ? megabytes * 1024 * 1024 : null, pids);
    }

    private IReadOnlyList<ApplicationVolumeDto>? BuildVolumes()
    {
        var lines = SplitLines(VolumesText);
        if (lines.Length == 0) return null;
        var volumes = new List<ApplicationVolumeDto>(lines.Length);
        foreach (var line in lines)
        {
            var parts = line.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length is < 2 or > 3) return null;
            volumes.Add(new ApplicationVolumeDto(parts[0], parts[1], parts.Length == 3 && parts[2].Equals("ro", StringComparison.OrdinalIgnoreCase)));
        }
        return volumes;
    }

    /// <summary>
    /// Reads the plain and secret editors. A name listed in the secret editor without a value keeps the
    /// version the server already stores, which is what lets the operator edit a definition without
    /// re-typing a credential.
    /// </summary>
    private IReadOnlyList<ApplicationConfigEntryDto>? BuildConfiguration()
    {
        var entries = new List<ApplicationConfigEntryDto>();
        foreach (var line in SplitLines(ConfigText))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) return null;
            entries.Add(new ApplicationConfigEntryDto(line[..separator].Trim(), line[(separator + 1)..], false, null));
        }
        foreach (var line in SplitLines(SecretConfigText))
        {
            var separator = line.IndexOf('=');
            var name = separator <= 0 ? line.Trim() : line[..separator].Trim();
            var value = separator <= 0 ? null : line[(separator + 1)..];
            var version = existing?.Configuration
                .FirstOrDefault(entry => entry.IsSecret && string.Equals(entry.Name, name, StringComparison.Ordinal))?.SecretVersion;
            entries.Add(new ApplicationConfigEntryDto(name, string.IsNullOrEmpty(value) ? null : value, true, version));
        }
        return entries.Count == 0 ? null : entries;
    }

    private bool ValidateAll()
    {
        foreach (var step in Enum.GetValues<DeploymentWizardStep>())
        {
            if (step is DeploymentWizardStep.Preview or DeploymentWizardStep.Progress) continue;
            if (Validate(step) is { } problem)
            {
                ErrorText = LocalizedStatus.Key(problem);
                return false;
            }
        }
        if (PublishesRevision && ValidatePreviewReadiness() is { } previewProblem)
        {
            ErrorText = LocalizedStatus.Key(previewProblem);
            return false;
        }
        ErrorText = LocalizedStatus.Literal(string.Empty);
        return true;
    }

    private void ValidateCurrentStep() => TryValidateCurrentStep();

    /// <summary>
    /// Reports the current step's problem, if any. <see cref="Next"/> needs the verdict, not just the
    /// side effect, so the message and the boolean are produced in one place.
    /// </summary>
    private bool TryValidateCurrentStep()
    {
        if (Validate((DeploymentWizardStep)StepIndex) is { } problem)
        {
            ErrorText = LocalizedStatus.Key(problem);
            return false;
        }
        ErrorText = LocalizedStatus.Literal(string.Empty);
        return true;
    }

    /// <summary>
    /// Validates one step in isolation, so the operator is not blocked by a later step's requirement
    /// while an earlier step is still being filled in.
    /// </summary>
    private string? Validate(DeploymentWizardStep step) => step switch
    {
        DeploymentWizardStep.Source => ValidateSource(),
        DeploymentWizardStep.Entry => ValidateEntry(),
        DeploymentWizardStep.Runtime => ValidateRuntime(),
        DeploymentWizardStep.Configuration => ValidateConfiguration(),
        DeploymentWizardStep.Proxy => ValidateProxy(),
        DeploymentWizardStep.Preview => ValidatePreviewReadiness(),
        _ => null,
    };

    private string? ValidateSource()
    {
        if (Intent == DeploymentWizardIntent.DeployNewRevision) return null;
        var name = Name.Trim();
        if (name.Length == 0) return DeploymentText.Prefix + ".error.name_required";
        if (name.Length is < 3 or > 40 || !char.IsAsciiLetterOrDigit(name[0])
            || name.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character is not ('-' or '_')))
            return DeploymentText.Prefix + ".error.name_invalid";
        return null;
    }

    private string? ValidateEntry()
    {
        if (Intent == DeploymentWizardIntent.EditDefinition) return null;
        if (RequiresImageReference)
            return string.IsNullOrWhiteSpace(ImageReference) ? DeploymentText.Prefix + ".error.image_required" : null;
        if (string.IsNullOrWhiteSpace(ArchiveReferenceId)) return DeploymentText.Prefix + ".error.archive_required";
        if (SourceKind == ApplicationSourceKind.PythonProject && string.IsNullOrWhiteSpace(ProgramEntry))
            return DeploymentText.Prefix + ".error.entry_required";
        return null;
    }

    private string? ValidateRuntime()
    {
        if (ParsePort(ContainerPort) is null) return DeploymentText.Prefix + ".error.container_port_invalid";
        if (!string.IsNullOrWhiteSpace(HostPort) && ParsePort(HostPort) is null)
            return DeploymentText.Prefix + ".error.host_port_invalid";
        // HTTP readiness needs a loopback publish to probe, so a host port becomes mandatory.
        if (IsReadinessHttp && IsWebWorkload && string.IsNullOrWhiteSpace(HostPort))
            return DeploymentText.Prefix + ".error.host_port_required_for_http";
        if (IsReadinessHttp && !HealthCheckPath.TrimStart().StartsWith('/'))
            return DeploymentText.Prefix + ".error.health_path_invalid";
        if (RuntimeVersion is { Length: > 64 }) return DeploymentText.Prefix + ".error.runtime_version_invalid";
        return null;
    }

    private string? ValidateConfiguration()
    {
        // Volumes and configuration are optional. Their builders use null to express an omitted
        // optional value, so only ask them to validate after the operator has supplied a line.
        // Without this guard, opening this step with its placeholder-only editors reports the
        // example format as invalid even though nothing will be sent to the server.
        if (SplitLines(VolumesText).Length > 0 && BuildVolumes() is null)
            return DeploymentText.Prefix + ".error.volume_invalid";
        if ((SplitLines(ConfigText).Length > 0 || SplitLines(SecretConfigText).Length > 0)
            && BuildConfiguration() is null)
            return DeploymentText.Prefix + ".error.configuration_invalid";
        if (SplitLines(ArgumentsText).Any(argument => argument.Length > 4096))
            return DeploymentText.Prefix + ".error.argument_invalid";
        if (!string.IsNullOrWhiteSpace(CpuCores) && ParseDouble(CpuCores) is not (> 0 and <= 64))
            return DeploymentText.Prefix + ".error.cpu_invalid";
        if (!string.IsNullOrWhiteSpace(MemoryMegabytes) && ParseLong(MemoryMegabytes) is not (>= 16 and <= 65536))
            return DeploymentText.Prefix + ".error.memory_invalid";
        if (!string.IsNullOrWhiteSpace(PidsLimit) && ParsePort(PidsLimit) is null)
            return DeploymentText.Prefix + ".error.pids_invalid";
        return null;
    }

    private string? ValidateProxy()
    {
        if (string.IsNullOrWhiteSpace(SiteId)) return null;
        if (SiteId.Length > 128 || !SiteId.All(char.IsAsciiLetterOrDigit))
            return DeploymentText.Prefix + ".error.site_invalid";
        if (string.IsNullOrWhiteSpace(HostPort)) return DeploymentText.Prefix + ".error.site_requires_host_port";
        return null;
    }

    /// <summary>A deployment needs a source even when the source steps were skipped by an edit intent.</summary>
    private string? ValidatePreviewReadiness()
    {
        if (!PublishesRevision) return null;
        if (RequiresImageReference && string.IsNullOrWhiteSpace(ImageReference))
            return DeploymentText.Prefix + ".error.image_required";
        if (RequiresArchive && string.IsNullOrWhiteSpace(ArchiveReferenceId))
            return DeploymentText.Prefix + ".error.archive_required";
        return ValidateRuntime();
    }

    private void RefreshPreview()
    {
        var source = SourceKind == ApplicationSourceKind.Image
            ? ImageReference
            : ArchiveFileName.Length > 0 ? ArchiveFileName : LocalizedText.Get(DeploymentText.Prefix + ".preview_no_archive");
        var site = SiteId.Length > 0 ? SiteId : LocalizedText.Get(DeploymentText.Prefix + ".preview_site_none");
        PreviewText = LocalizedStatus.Join("\n",
        [
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_name", Name),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_source",
                LocalizedText.Get(DeploymentText.Enum(DeploymentText.SourcePrefix, SourceKind)), source),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_entry", ProgramEntry.Length > 0 ? ProgramEntry : "—"),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_endpoint", BindAddress,
                HostPort.Length > 0 ? HostPort : "—", ContainerPort),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_readiness",
                LocalizedText.Get(DeploymentText.Enum(DeploymentText.ReadinessPrefix, ReadinessLevel)),
                HealthCheckPath.Length > 0 ? HealthCheckPath : "—"),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_volumes", SplitLines(VolumesText).Length),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_configuration",
                SplitLines(ConfigText).Length, SplitLines(SecretConfigText).Length),
            LocalizedStatus.Format(DeploymentText.Prefix + ".preview_site", site),
            LocalizedStatus.Key(DeploymentText.Prefix + ".preview_replacement_warning"),
        ]);
    }

    partial void OnErrorTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusTextChanged(LocalizedStatus value) => OnPropertyChanged(nameof(HasStatus));

    private static bool IsExpected(Exception exception) => exception
        is ApplicationDeploymentClientException or HttpRequestException or InvalidOperationException
        or IOException or UnauthorizedAccessException or TaskCanceledException;

    private static LocalizedStatus Describe(Exception exception) => exception switch
    {
        ApplicationDeploymentClientException failure => DeploymentText.Problem(failure.ProblemCode),
        _ => LocalizedStatus.Key(DeploymentText.Prefix + ".error.transport"),
    };

    private static string[] SplitLines(string text) => string.IsNullOrWhiteSpace(text)
        ? []
        : [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>One argument per line. An empty editor means no arguments, not an empty argument.</summary>
    private IReadOnlyList<string>? ParseArguments()
    {
        var lines = SplitLines(ArgumentsText);
        return lines.Length == 0 ? null : lines;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParsePort(string value) => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
        && parsed is >= 1 and <= 65535 ? parsed : null;

    private static int? ParseOptionalPort(string value) => string.IsNullOrWhiteSpace(value) ? null : ParsePort(value);

    private static double? ParseDouble(string value) => double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ? parsed : null;

    private static long? ParseLong(string value) => long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed) ? parsed : null;
}
