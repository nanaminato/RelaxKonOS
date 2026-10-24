using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Shared by all Explorer windows; all entry points run on the UI synchronization context.</summary>
public sealed partial class ExplorerOperationCenter(IExplorerClient client) : ObservableObject
{
    public ObservableCollection<ExplorerOperationCard> Jobs { get; } = [];
    public Func<string?>? SessionKey { get; set; }
    public Func<FileOperationIssue, FileOperationKind, Task<bool>>? ElevateAsync { get; set; }
    public Func<bool, CancellationToken, Task<string?>>? RequestArchivePasswordAsync { get; set; }
    public Action? ShowRequested { get; set; }
    public Action? CloseRequested { get; set; }
    private int _pendingRequests;
    private readonly Dictionary<Guid, CancellationTokenSource> _localTransfers = [];
    public event Action<FileOperationDto>? Completed;
    private readonly Dictionary<Guid, Action<FileOperationDto>> _callbacks = [];
    private readonly HashSet<Guid> _dismissed = [];
    private string? _session;
    private bool _polling;
    private CancellationTokenSource _sessionCancellation = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private string? CurrentSession => SessionKey?.Invoke() ?? (SessionKey is null ? "test" : null);
    private bool IsCurrent(string? session) => session is not null && session == CurrentSession;
    private void BindSession()
    {
        var current = CurrentSession;
        if (_session == current) return;
        _sessionCancellation.Cancel();
        _sessionCancellation = new();
        Jobs.Clear();
        _callbacks.Clear();
        _dismissed.Clear();
        _session = current;
        Error = string.Empty;
    }
    public void SessionChanged() => BindSession();

    public void QueueUpload(IReadOnlyList<FileOperationItem> items, long totalBytes,
        Func<Action<string, long, int>, CancellationToken, Task> upload) => QueueTransfer(items, totalBytes, upload, false);

    public void QueueDownload(IReadOnlyList<FileOperationItem> items, long totalBytes,
        Func<Action<string, long, int>, CancellationToken, Task> download) => QueueTransfer(items, totalBytes, download, true);

    /// <summary>Observes Shell-driven reads without moving network callbacks onto the UI thread.</summary>
    public ClipboardFileTransfer BeginClipboardDownload(string path, long size, Action<Action> dispatch)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        Action<long>? progress = null;
        dispatch(() =>
        {
            try
            {
                QueueDownload([new(path)], size, async (report, cancel) =>
                {
                    progress = bytes => report(path, bytes, 0);
                    using var registration = cancel.Register(() => cancellation.Cancel());
                    try { await completion.Task; report(path, size, 1); }
                    finally { cancellation.Dispose(); }
                });
            }
            catch (Exception ex) { cancellation.Cancel(); completion.TrySetException(ex); }
        });
        return new(bytes => dispatch(() => progress?.Invoke(bytes)), error =>
        {
            if (error is OperationCanceledException) completion.TrySetCanceled();
            else if (error is not null) completion.TrySetException(error);
            else completion.TrySetResult();
        }, token);
    }

    private void QueueTransfer(IReadOnlyList<FileOperationItem> items, long totalBytes,
        Func<Action<string, long, int>, CancellationToken, Task> upload, bool downloading)
    {
        BindSession();
        var session = _session;
        if (!IsCurrent(session)) throw new InvalidOperationException(LocalizedText.Get("explorer.error.not_signed_in"));
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token);
        var dto = new FileOperationDto(Guid.NewGuid(), FileOperationKind.Copy, FileOperationState.Running,
            items, null, 0, totalBytes, 0, 0, [], null, [], false, DateTimeOffset.UtcNow);
        _localTransfers.Add(dto.Id, cancellation);
        Jobs.Add(new(this, dto) { IsUpload = !downloading, IsDownload = downloading });
        ShowRequested?.Invoke();
        _ = RunUploadAsync();

        async Task RunUploadAsync()
        {
            try
            {
                await upload((path, bytes, count) =>
                {
                    if (!IsCurrent(session) || dto.IsTerminal || cancellation.IsCancellationRequested
                        || count < dto.ProcessedItems) return;
                    dto = dto with { CurrentPath = path, CurrentBytes = bytes, ProcessedItems = count, Revision = dto.Revision + 1 };
                    Update(dto);
                }, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                dto = dto with { State = FileOperationState.Completed };
            }
            catch (OperationCanceledException)
            {
                dto = dto with { State = FileOperationState.Cancelled };
            }
            catch (Exception ex)
            {
                dto = dto with { State = FileOperationState.Failed };
                if (IsCurrent(session)) Error = LocalizedText.Format(downloading ? "explorer.status.download_failed" : "explorer.status.upload_failed", ex.Message);
            }
            finally
            {
                _localTransfers.Remove(dto.Id);
                cancellation.Dispose();
                if (IsCurrent(session)) Update(dto with { Revision = dto.Revision + 1 });
            }
        }
    }

    public async Task SubmitAsync(StartFileOperationRequest request, Action<FileOperationDto> completed)
    {
        BindSession();
        var session = _session;
        if (!IsCurrent(session)) throw new InvalidOperationException(LocalizedText.Get("explorer.error.not_signed_in"));
        ShowRequested?.Invoke();
        _pendingRequests++;
        _callbacks[request.RequestId] = completed;
        try
        {
            var dto = await client.StartOperationAsync(request, _sessionCancellation.Token);
            if (!IsCurrent(session)) return;
            Update(dto);
            EnsurePolling();
        }
        catch (OperationCanceledException) when (!IsCurrent(session)) { }
        catch (RelaxKonOS.Client.Services.Auth.RelaxKonOSAuthException ex) when (ex.Status is >= 400 and < 500)
        {
            _callbacks.Remove(request.RequestId);
            Error = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            if (!IsCurrent(session)) return;
            // A lost submission response does not prove the server rejected the job.
            await RestoreAsync();
            Error = LocalizedText.Format("explorer.operations.submit_unknown", ex.Message);
            throw;
        }
        finally { _pendingRequests--; CloseIfFinished(); }
    }
    private void CloseIfFinished()
    {
        if (_pendingRequests == 0 && Jobs.Count == 0 && string.IsNullOrEmpty(Error)) CloseRequested?.Invoke();
    }
    public async Task RestoreAsync()
    {
        BindSession();
        var session = _session;
        if (!IsCurrent(session)) return;
        _pendingRequests++;
        try
        {
            var jobs = await client.ListOperationsAsync(_sessionCancellation.Token);
            if (!IsCurrent(session)) return;
            foreach (var dto in jobs) Update(dto);
            Error = string.Empty;
            EnsurePolling();
        }
        catch (Exception ex) { if (IsCurrent(session)) Error = ex.Message; }
        finally { _pendingRequests--; CloseIfFinished(); }
    }
    public void Show() { BindSession(); ShowRequested?.Invoke(); _ = RestoreAsync(); }
    [RelayCommand] private void ClearCompleted()
    {
        foreach (var job in Jobs.Where(j => j.Snapshot.IsTerminal || j.Missing).ToArray())
        {
            _dismissed.Add(job.Snapshot.Id);
            _callbacks.Remove(job.Snapshot.RequestId);
            Jobs.Remove(job);
        }
    }
    private void Update(FileOperationDto dto)
    {
        if (_dismissed.Contains(dto.Id)) return;
        var card = Jobs.FirstOrDefault(j => j.Snapshot.Id == dto.Id);
        if (card is not null && dto.Revision < card.Snapshot.Revision) return;
        var wasTerminal = card?.Snapshot.IsTerminal == true;
        if (card is null) { card = new(this, dto); Jobs.Add(card); }
        else card.Snapshot = dto;
        card.Error = string.Empty;
        if (dto.IsTerminal)
        {
            if (_callbacks.Remove(dto.RequestId, out var callback)) callback(dto);
            if (!wasTerminal) Completed?.Invoke(dto);
            _dismissed.Add(dto.Id);
            Jobs.Remove(card);
            CloseIfFinished();
        }
    }
    private void EnsurePolling()
    {
        if (_polling) return;
        _ = PollAsync();
    }
    private async Task PollAsync()
    {
        _polling = true;
        var session = _session;
        try
        {
            while (IsCurrent(session))
            {
                var active = Jobs.Where(j => !j.Snapshot.IsTerminal && !j.Missing && !j.IsLocalTransfer).ToArray();
                if (active.Length == 0) break;
                foreach (var job in active)
                {
                    if (!IsCurrent(session)) return;
                    try
                    {
                        var dto = await client.GetOperationAsync(job.Snapshot.Id, _sessionCancellation.Token);
                        if (!IsCurrent(session)) return;
                        Update(dto);
                    }
                    catch (Exception ex)
                    {
                        if (!IsCurrent(session)) return;
                        job.Error = LocalizedText.Format("explorer.operations.connection_error", ex.Message);
                        if (ex is RelaxKonOS.Client.Services.Auth.RelaxKonOSAuthException { Status: 404 })
                        {
                            job.Missing = true;
                            _callbacks.Remove(job.Snapshot.RequestId);
                        }
                    }
                }
                await Task.Delay(500);
            }
        }
        finally
        {
            _polling = false;
            if (_session != session && Jobs.Any(j => !j.Snapshot.IsTerminal && !j.Missing && !j.IsLocalTransfer)) EnsurePolling();
        }
    }
    internal async Task ActAsync(ExplorerOperationCard card, FileOperationDecision? action)
    {
        var session = _session;
        if (!IsCurrent(session)) { BindSession(); return; }
        var snapshot = card.Snapshot;
        if (_localTransfers.TryGetValue(snapshot.Id, out var transferCancellation))
        {
            if (action is null)
            {
                card.Snapshot = snapshot with { State = FileOperationState.Cancelling };
                transferCancellation.Cancel();
            }
            return;
        }
        try
        {
            FileOperationDto dto;
            if (action is null) dto = await client.CancelOperationAsync(snapshot.Id, _sessionCancellation.Token);
            else
            {
                if (snapshot.Issue is not { } issue) return;
                if (action == FileOperationDecision.Retry && issue.Code == "access-denied"
                    && snapshot.Kind is not (FileOperationKind.Compress or FileOperationKind.Extract) && ElevateAsync is not null)
                {
                    if (!await ElevateAsync(issue, snapshot.Kind)) return;
                    if (!IsCurrent(session)) return;
                }
                ArchiveCredentials? credentials = null;
                var passwordIssue = issue.Code is "archive-password-required" or "archive-password-invalid";
                if (passwordIssue && action == FileOperationDecision.Retry)
                {
                    var password = await (RequestArchivePasswordAsync?.Invoke(issue.Code == "archive-password-invalid", _sessionCancellation.Token) ?? Task.FromResult<string?>(null));
                    if (password is null || !IsCurrent(session)) return;
                    credentials = new(password); credentials.Validate();
                }
                dto = await client.DecideOperationAsync(snapshot.Id, new(snapshot.Issue.Id, action.Value,
                    passwordIssue ? false : card.ApplyToAll, credentials), _sessionCancellation.Token);
            }
            if (IsCurrent(session)) Update(dto);
        }
        catch (Exception ex) { if (IsCurrent(session)) card.Error = ex.Message; }
    }
}

public sealed partial class ExplorerOperationCard(ExplorerOperationCenter center, FileOperationDto snapshot) : ObservableObject
{
    [ObservableProperty] private FileOperationDto _snapshot = snapshot;
    [ObservableProperty] private bool _applyToAll;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _missing;
    private readonly FileTransferMetrics _metrics = new();
    [ObservableProperty] private bool _detailsExpanded = true;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool NeedsAttention => HasIssue || Missing || HasError;
    public bool ShowTransferDetails => DetailsExpanded && !HasIssue;
    public bool ShowSpeedGraph => ShowTransferDetails && HasMeasuredProgress && !NeedsAttention && Snapshot.State == FileOperationState.Running;
    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationLocation);
    public bool HasMeasuredProgress => Snapshot.CurrentTotalBytes > 0 && !Missing;
    public string ProgressText => Snapshot.Kind == FileOperationKind.Extract && Snapshot.State == FileOperationState.Running && !HasMeasuredProgress
        ? LocalizedText.Get("explorer.operations.extracting") : HasMeasuredProgress && Snapshot.State == FileOperationState.Running
        ? LocalizedText.Format(Snapshot.Kind == FileOperationKind.Extract ? "explorer.operations.extracted_percent" : IsLocalTransfer ? "explorer.operations.percent_complete"
            : "explorer.operations.current_file_percent", (int)Math.Floor(Progress)) : StateText;
    public string ProgressScope => !HasMeasuredProgress ? string.Empty : LocalizedText.Get(IsLocalTransfer || Snapshot.Kind == FileOperationKind.Extract
        ? "explorer.operations.entire_operation" : "explorer.operations.current_file_short");
    public string Summary => LocalizedText.Format("explorer.operations.processing_items", Title, Snapshot.Items.Count);
    public string CurrentFileName => string.IsNullOrWhiteSpace(Snapshot.CurrentPath)
        ? StateText : FileName(Snapshot.CurrentPath);
    public string SourceLocation => LocationsFor(false);
    public string DestinationLocation => LocationsFor(true);
    public IReadOnlyList<double> SpeedHistory => _metrics.History;
    private bool CanShowSpeed => _metrics.IsReady && Snapshot.State == FileOperationState.Running && !Missing && !HasError;
    public string SpeedText => CanShowSpeed ? FileTransferMetrics.FormatBytes(_metrics.BytesPerSecond) + "/s"
        : LocalizedText.Get("explorer.operations.calculating");
    public string TransferSize => HasMeasuredProgress
        ? LocalizedText.Format("explorer.operations.transferred_size", FileTransferMetrics.FormatBytes(Snapshot.CurrentBytes), FileTransferMetrics.FormatBytes(Snapshot.CurrentTotalBytes))
        : LocalizedText.Get("explorer.operations.calculating");
    public string RemainingTime
    {
        get
        {
            // Server tasks report a current-file counter, not a recursively measured job total.
            if (!IsLocalTransfer || !CanShowSpeed || _metrics.BytesPerSecond <= 0 || !HasMeasuredProgress)
                return LocalizedText.Get("explorer.operations.calculating");
            var seconds = Math.Max(0, Snapshot.CurrentTotalBytes - Snapshot.CurrentBytes) / _metrics.BytesPerSecond;
            if (seconds >= 3600) return LocalizedText.Format("explorer.operations.remaining_hours", Math.Ceiling(seconds / 3600));
            if (seconds >= 60) return LocalizedText.Format("explorer.operations.remaining_minutes", Math.Ceiling(seconds / 60));
            return LocalizedText.Format("explorer.operations.remaining_seconds", Math.Ceiling(seconds));
        }
    }
    public string DetailsToggleLabel => LocalizedText.Get(DetailsExpanded ? "explorer.operations.fewer_details" : "explorer.operations.more_details");
    private static string FileName(string path)
    {
        var normalized = ExplorerPath.Normalize(path)!;
        var name = normalized[(normalized.LastIndexOf(ExplorerPath.IsWindows(normalized) ? '\\' : '/') + 1)..];
        return string.IsNullOrEmpty(name) ? path : name;
    }
    private string LocationsFor(bool destination)
    {
        var paths = Snapshot.Items.Select(item => destination ? item.DestinationPath : item.SourcePath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => destination && Snapshot.Kind == FileOperationKind.Extract
                ? path! : ExplorerPath.Parent(path!) ?? path!)
            .Distinct(StringComparer.Ordinal).ToArray();
        return paths.Length switch { 0 => string.Empty, 1 => paths[0], _ => LocalizedText.Get("explorer.operations.multiple_locations") };
    }
    partial void OnDetailsExpandedChanged(bool value) { OnPropertyChanged(nameof(DetailsToggleLabel)); OnPropertyChanged(nameof(ShowTransferDetails)); OnPropertyChanged(nameof(ShowSpeedGraph)); }
    partial void OnMissingChanged(bool value) => RefreshPresentation();
    partial void OnErrorChanged(string value) => RefreshPresentation();
    public bool IsUpload { get; init; }
    public bool IsDownload { get; init; }
    public bool IsLocalTransfer => IsUpload || IsDownload;
    public string Title => LocalizedText.Get($"explorer.operations.kind.{Snapshot.Kind}");
    public string StateText => Missing ? LocalizedText.Get("explorer.operations.missing") : LocalizedText.Get($"explorer.operations.state.{Snapshot.State}");
    public string Locations => string.Join(Environment.NewLine, Snapshot.Items.Take(3).Select(i =>
        i.DestinationPath is null ? i.SourcePath : $"{i.SourcePath} → {i.DestinationPath}"))
        + (Snapshot.Items.Count > 3 ? Environment.NewLine + "…" : string.Empty);
    public string Counts => LocalizedText.Format("explorer.operations.items_processed", Snapshot.ProcessedItems, Snapshot.SkippedItems);
    public string Bytes => Snapshot.CurrentTotalBytes > 0 ? $"{Snapshot.CurrentBytes:N0} / {Snapshot.CurrentTotalBytes:N0} B" : string.Empty;
    public double Progress => Snapshot.CurrentTotalBytes > 0 ? Math.Clamp(Snapshot.CurrentBytes * 100d / Snapshot.CurrentTotalBytes, 0, 100) : 0;
    public bool Indeterminate => !Missing && (Snapshot.State is FileOperationState.Queued or FileOperationState.Running)
        && Snapshot.CurrentTotalBytes == 0;
    public bool CanCancel => !Missing && !Snapshot.IsTerminal && Snapshot.State != FileOperationState.Cancelling;
    public bool HasIssue => Snapshot.Issue is not null;
    public bool CanReplace => Snapshot.Issue?.Choices.Contains(FileOperationDecision.Replace) == true;
    public bool CanKeepBoth => Snapshot.Issue?.Choices.Contains(FileOperationDecision.KeepBoth) == true;
    public bool CanRetry => Snapshot.Issue?.Choices.Contains(FileOperationDecision.Retry) == true;
    public bool CanApplyToAll => Snapshot.Issue?.Code is not ("archive-password-required" or "archive-password-invalid");
    public string RetryLabel => LocalizedText.Get(Snapshot.Issue?.Code is "archive-password-required" or "archive-password-invalid"
        ? "explorer.archive.password_retry" : Snapshot.Issue?.Code == "access-denied" && Snapshot.Kind is not (FileOperationKind.Compress or FileOperationKind.Extract)
        ? "explorer.operations.retry_access" : "explorer.operations.retry");
    public string IssueText => Snapshot.Issue is { } issue ? $"{LocalizedText.Get("explorer.operations.issue." + issue.Code)}\n{issue.SourcePath}\n{issue.DestinationPath}\n{issue.Message}" : string.Empty;
    public string Details => string.Join(Environment.NewLine, Snapshot.Details.Select(d => $"{d.Path}: {LocalizedText.Get("explorer.operations.outcome." + d.Outcome)} {d.Message}"))
        + (Snapshot.DetailsTruncated ? Environment.NewLine + LocalizedText.Get("explorer.operations.truncated") : string.Empty);
    partial void OnSnapshotChanged(FileOperationDto value) => RefreshMeasurements();
    public void RefreshMeasurements()
    {
        _metrics.Observe(Snapshot.CurrentBytes, IsLocalTransfer ? null : Snapshot.CurrentPath,
            Snapshot.State == FileOperationState.Running && !Missing && !HasError,
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
        RefreshPresentation();
    }
    private void RefreshPresentation()
    {
        foreach (var name in new[] { nameof(Title), nameof(StateText), nameof(Locations), nameof(Counts), nameof(Bytes),
            nameof(DetailsToggleLabel), nameof(ShowTransferDetails), nameof(ShowSpeedGraph), nameof(HasError), nameof(NeedsAttention), nameof(HasDestination), nameof(HasMeasuredProgress), nameof(ProgressText), nameof(ProgressScope),
            nameof(Summary), nameof(CurrentFileName), nameof(SourceLocation), nameof(DestinationLocation), nameof(SpeedHistory), nameof(SpeedText),
            nameof(TransferSize), nameof(RemainingTime), nameof(Progress), nameof(Indeterminate), nameof(CanCancel), nameof(HasIssue), nameof(CanReplace), nameof(CanKeepBoth), nameof(CanRetry), nameof(CanApplyToAll), nameof(IssueText), nameof(RetryLabel), nameof(Details) })
            OnPropertyChanged(name);
    }
    [RelayCommand] private void ToggleDetails() => DetailsExpanded = !DetailsExpanded;
    [RelayCommand] private Task CancelAsync() => center.ActAsync(this, null);
    [RelayCommand] private Task RetryAsync() => center.ActAsync(this, FileOperationDecision.Retry);
    [RelayCommand] private Task SkipAsync() => center.ActAsync(this, FileOperationDecision.Skip);
    [RelayCommand] private Task ReplaceAsync() => center.ActAsync(this, FileOperationDecision.Replace);
    [RelayCommand] private Task KeepBothAsync() => center.ActAsync(this, FileOperationDecision.KeepBoth);
}
