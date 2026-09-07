using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Client.Localization;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.Models;

/// <summary>Shared by all Explorer windows; all entry points run on the UI synchronization context.</summary>
public sealed partial class ExplorerOperationCenter(IExplorerClient client) : ObservableObject
{
    public ObservableCollection<ExplorerOperationCard> Jobs { get; } = [];
    public Func<string?>? SessionKey { get; set; }
    public Func<FileOperationIssue, FileOperationKind, Task<bool>>? ElevateAsync { get; set; }
    public Action? ShowRequested { get; set; }
    public event Action<FileOperationDto>? Completed;
    private readonly Dictionary<Guid, Action<FileOperationDto>> _callbacks = [];
    private readonly HashSet<Guid> _dismissed = [];
    private string? _session;
    private bool _polling;
    private CancellationTokenSource _sessionCancellation = new();
    [ObservableProperty] private string _error = string.Empty;

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

    public async Task SubmitAsync(StartFileOperationRequest request, Action<FileOperationDto> completed)
    {
        BindSession();
        var session = _session;
        if (!IsCurrent(session)) throw new InvalidOperationException(LocalizedText.Get("explorer.error.not_signed_in"));
        ShowRequested?.Invoke();
        _callbacks[request.RequestId] = completed;
        try
        {
            var dto = await client.StartOperationAsync(request, _sessionCancellation.Token);
            if (!IsCurrent(session)) return;
            Update(dto);
            EnsurePolling();
        }
        catch (OperationCanceledException) when (!IsCurrent(session)) { }
        catch (Client.Services.Auth.RemoteOsAuthException ex) when (ex.Status is >= 400 and < 500)
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
    }
    public async Task RestoreAsync()
    {
        BindSession();
        var session = _session;
        if (!IsCurrent(session)) return;
        try
        {
            var jobs = await client.ListOperationsAsync(_sessionCancellation.Token);
            if (!IsCurrent(session)) return;
            foreach (var dto in jobs) Update(dto);
            Error = string.Empty;
            EnsurePolling();
        }
        catch (Exception ex) { if (IsCurrent(session)) Error = ex.Message; }
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
                var active = Jobs.Where(j => !j.Snapshot.IsTerminal && !j.Missing).ToArray();
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
                        if (ex is Client.Services.Auth.RemoteOsAuthException { Status: 404 })
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
            if (_session != session && Jobs.Any(j => !j.Snapshot.IsTerminal && !j.Missing)) EnsurePolling();
        }
    }
    internal async Task ActAsync(ExplorerOperationCard card, FileOperationDecision? action)
    {
        var session = _session;
        if (!IsCurrent(session)) { BindSession(); return; }
        var snapshot = card.Snapshot;
        try
        {
            FileOperationDto dto;
            if (action is null) dto = await client.CancelOperationAsync(snapshot.Id, _sessionCancellation.Token);
            else
            {
                if (snapshot.Issue is not { } issue) return;
                if (action == FileOperationDecision.Retry && issue.Code == "access-denied" && ElevateAsync is not null)
                {
                    if (!await ElevateAsync(issue, snapshot.Kind)) return;
                    if (!IsCurrent(session)) return;
                }
                dto = await client.DecideOperationAsync(snapshot.Id, new(snapshot.Issue.Id, action.Value, card.ApplyToAll), _sessionCancellation.Token);
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
    partial void OnMissingChanged(bool value) { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(Indeterminate)); }
    public string Title => LocalizedText.Get($"explorer.operations.kind.{Snapshot.Kind}");
    public string StateText => Missing ? LocalizedText.Get("explorer.operations.missing") : LocalizedText.Get($"explorer.operations.state.{Snapshot.State}");
    public string Locations => string.Join(Environment.NewLine, Snapshot.Items.Take(3).Select(i =>
        i.DestinationPath is null ? i.SourcePath : $"{i.SourcePath} → {i.DestinationPath}"))
        + (Snapshot.Items.Count > 3 ? Environment.NewLine + "…" : string.Empty);
    public string Counts => LocalizedText.Format("explorer.operations.counts", Snapshot.ProcessedItems,
        Snapshot.SkippedItems, Snapshot.CompletedSources.Count, Snapshot.Items.Count);
    public string Bytes => Snapshot.CurrentTotalBytes > 0 ? $"{Snapshot.CurrentBytes:N0} / {Snapshot.CurrentTotalBytes:N0} B" : string.Empty;
    public double Progress => Snapshot.CurrentTotalBytes > 0 ? Math.Clamp(Snapshot.CurrentBytes * 100d / Snapshot.CurrentTotalBytes, 0, 100) : 0;
    public bool Indeterminate => !Missing && (Snapshot.State is FileOperationState.Queued or FileOperationState.Running)
        && Snapshot.CurrentTotalBytes == 0;
    public bool CanCancel => !Missing && !Snapshot.IsTerminal && Snapshot.State != FileOperationState.Cancelling;
    public bool HasIssue => Snapshot.Issue is not null;
    public bool CanReplace => Snapshot.Issue?.Choices.Contains(FileOperationDecision.Replace) == true;
    public bool CanKeepBoth => Snapshot.Issue?.Choices.Contains(FileOperationDecision.KeepBoth) == true;
    public bool CanRetry => Snapshot.Issue?.Choices.Contains(FileOperationDecision.Retry) == true;
    public string RetryLabel => LocalizedText.Get(Snapshot.Issue?.Code == "access-denied" ? "explorer.operations.retry_access" : "explorer.operations.retry");
    public string IssueText => Snapshot.Issue is { } issue ? $"{LocalizedText.Get("explorer.operations.issue." + issue.Code)}\n{issue.SourcePath}\n{issue.DestinationPath}\n{issue.Message}" : string.Empty;
    public string Details => string.Join(Environment.NewLine, Snapshot.Details.Select(d => $"{d.Path}: {LocalizedText.Get("explorer.operations.outcome." + d.Outcome)} {d.Message}"))
        + (Snapshot.DetailsTruncated ? Environment.NewLine + LocalizedText.Get("explorer.operations.truncated") : string.Empty);
    partial void OnSnapshotChanged(FileOperationDto value)
    {
        foreach (var name in new[] { nameof(Title), nameof(StateText), nameof(Locations), nameof(Counts), nameof(Bytes),
            nameof(Progress), nameof(Indeterminate), nameof(CanCancel), nameof(HasIssue), nameof(CanReplace), nameof(CanKeepBoth), nameof(CanRetry), nameof(IssueText), nameof(RetryLabel), nameof(Details) })
            OnPropertyChanged(name);
    }
    [RelayCommand] private Task CancelAsync() => center.ActAsync(this, null);
    [RelayCommand] private Task RetryAsync() => center.ActAsync(this, FileOperationDecision.Retry);
    [RelayCommand] private Task SkipAsync() => center.ActAsync(this, FileOperationDecision.Skip);
    [RelayCommand] private Task ReplaceAsync() => center.ActAsync(this, FileOperationDecision.Replace);
    [RelayCommand] private Task KeepBothAsync() => center.ActAsync(this, FileOperationDecision.KeepBoth);
}
