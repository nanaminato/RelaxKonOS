using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

public sealed partial class GitClientViewModel
{
    public System.Collections.ObjectModel.ObservableCollection<GitConflictBlock> ConflictBlocks { get; } = [];
    [ObservableProperty] private GitConflictBlock? _selectedConflictBlock;
    private readonly Dictionary<(string Repo, string Path), (GitConflictFileDto Detail, string Text)> _conflictDrafts = [];
    private string? _conflictDetailRepository;
    public bool ShowConflictPage => HasConflicts || ConflictOperation is not null;
    partial void OnConflictResultChanged(string value)
    {
        var index = SelectedConflictBlock is null ? 0 : ConflictBlocks.IndexOf(SelectedConflictBlock);
        ConflictBlocks.Clear();
        foreach (var block in GitConflictBlock.Parse(value)) ConflictBlocks.Add(block);
        SelectedConflictBlock = ConflictBlocks.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, ConflictBlocks.Count - 1)));
    }
    [RelayCommand(CanExecute = nameof(CanEditConflict))]
    private void ApplyConflictBlock(string choice)
    {
        var block = SelectedConflictBlock;
        if (block is null || !ConflictBlocks.Contains(block)) return;
        var replacement = choice switch { "ours" => block.Ours, "theirs" => block.Theirs, "both" => block.Ours + block.Theirs, _ => null };
        if (replacement is not null) ConflictResult = ConflictResult.Remove(block.Start, block.Length).Insert(block.Start, replacement);
    }
    public System.Collections.ObjectModel.ObservableCollection<string> ConflictPaths { get; } = [];
    [ObservableProperty] private string? _selectedConflictPath;
    [ObservableProperty] private GitConflictFileDto? _conflictDetail;
    [ObservableProperty] private string _conflictResult = "";
    [ObservableProperty] private string? _conflictOperation;
    [ObservableProperty] private bool _conflictBusy;
    public bool CanEditConflict => ConflictDetail?.CanEdit == true && !ConflictBusy && !IsBusy;
    public bool CanResolveConflict => ConflictDetail is not null && !ConflictBusy && !IsBusy;
    public bool CanContinueConflict => ConflictOperation is not null && ConflictPaths.Count == 0 && !ConflictBusy && !IsBusy;
    public bool CanAbortConflict => ConflictOperation is not null && !ConflictBusy && !IsBusy;
    public string ConflictOperationLabel => ConflictOperation is null ? LocalizedText.Get("git.conflicts.no_operation") :
        LocalizedText.Format("git.conflicts.operation", ConflictOperation);
    private int _conflictLoad;

    partial void OnConflictBusyChanged(bool value) => UpdateConflictCommands();
    partial void OnIsBusyChanged(bool value) => UpdateConflictCommands();
    partial void OnConflictDetailChanged(GitConflictFileDto? value) => UpdateConflictCommands();
    partial void OnConflictOperationChanged(string? value)
    {
        OnPropertyChanged(nameof(ConflictOperationLabel));
        UpdateConflictCommands();
    }
    private void UpdateConflictCommands()
    {
        OnPropertyChanged(nameof(CanEditConflict));
        OnPropertyChanged(nameof(ShowConflictPage));
        ApplyConflictBlockCommand.NotifyCanExecuteChanged();
        ResolveConflictCommand.NotifyCanExecuteChanged();
        SaveConflictCommand.NotifyCanExecuteChanged();
        ContinueConflictCommand.NotifyCanExecuteChanged();
        AbortConflictCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedConflictPathChanged(string? value) => _ = LoadConflictAsync(value);

    private async Task LoadConflictAsync(string? path)
    {
        var generation = ++_conflictLoad;
        var repo = SelectedRepository?.Id;
        if (ConflictDetail is not null && _conflictDetailRepository is not null)
            _conflictDrafts[(_conflictDetailRepository, ConflictDetail.Path)] = (ConflictDetail, ConflictResult);
        ConflictDetail = null;
        ConflictResult = "";
        if (repo is null || path is null) return;
        if (_conflictDrafts.TryGetValue((repo, path), out var draft))
        {
            _conflictDetailRepository = repo;
            ConflictDetail = draft.Detail;
            ConflictResult = draft.Text;
            return;
        }
        try
        {
            var detail = await client.GetConflictAsync(repo, path);
            if (generation != _conflictLoad || repo != SelectedRepository?.Id) return;
            _conflictDetailRepository = repo;
            ConflictDetail = detail;
            ConflictResult = detail.Result ?? "";
        }
        catch (Exception ex) { if (generation == _conflictLoad) StatusText = ex.Message; }
    }

    private async Task RefreshConflictStateAsync()
    {
        if (SelectedRepository is null) return;
        var repo = SelectedRepository.Id;
        var state = await client.GetConflictStateAsync(repo);
        if (repo != SelectedRepository?.Id) return;
        if (_conflictDetailRepository is not null && _conflictDetailRepository != repo)
        {
            SelectedConflictPath = null;
            ConflictPaths.Clear();
        }
        ConflictOperation = state.Operation;
        // Preserve selection and unsaved editor content during periodic status refresh.
        if (!ConflictPaths.SequenceEqual(state.Paths))
        {
            var selected = SelectedConflictPath;
            ConflictPaths.Clear();
            foreach (var path in state.Paths) ConflictPaths.Add(path);
            SelectedConflictPath = selected is not null && state.Paths.Contains(selected) ? selected : state.Paths.FirstOrDefault();
        }
        if (ConflictDetail is null && SelectedConflictPath is not null) await LoadConflictAsync(SelectedConflictPath);
        HasConflicts = state.Paths.Count > 0;
        UpdateConflictCommands();
    }

    [RelayCommand]
    private async Task ReloadConflictAsync()
    {
        if (ConflictBusy || IsBusy) return;
        if (ConflictDetail is not null && ConflictResult != (ConflictDetail.Result ?? "") &&
            (ShowConfirmAsync is null || !await ShowConfirmAsync(LocalizedText.Get("git.conflicts.discard")))) return;
        if (SelectedRepository is not null && SelectedConflictPath is not null)
            _conflictDrafts.Remove((SelectedRepository.Id, SelectedConflictPath));
        ConflictDetail = null;
        await LoadConflictAsync(SelectedConflictPath);
    }

    [RelayCommand(CanExecute = nameof(CanEditConflict))]
    private Task SaveConflictAsync() => ResolveConflictAsync("edited");

    [RelayCommand(CanExecute = nameof(CanResolveConflict))]
    private async Task ResolveConflictAsync(string choice)
    {
        if (ConflictDetail is null || SelectedRepository is null || ConflictBusy || IsBusy) return;
        var detail = ConflictDetail;
        var repo = SelectedRepository.Id;
        if (choice != "edited" && (ShowConfirmAsync is null ||
            !await ShowConfirmAsync(LocalizedText.Get("git.conflicts.confirm_replace")))) return;
        ConflictBusy = true;
        IsBusy = true;
        try
        {
            var result = await client.ResolveConflictsAsync(repo, new(detail.Path, detail.Revision, choice, choice == "edited" ? ConflictResult : null));
            if (result.Success)
            {
                _conflictDrafts.Remove((repo, detail.Path));
                ConflictDetail = null;
                ConflictResult = "";
            }
            await RefreshAllAsync();
            if (!result.Success) await NotifyAsync(result.Message ?? LocalizedText.Get("git.conflicts.failed"));
        }
        catch (Exception ex) { await NotifyAsync(ex.Message); }
        finally { IsBusy = false; ConflictBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanContinueConflict))]
    private Task ContinueConflictAsync() => RunConflictOperationAsync("continue");
    [RelayCommand(CanExecute = nameof(CanAbortConflict))]
    private Task AbortConflictAsync() => RunConflictOperationAsync("abort");
    private async Task RunConflictOperationAsync(string action)
    {
        if (ConflictOperation is null || SelectedRepository is null || ConflictBusy || IsBusy) return;
        var repo = SelectedRepository.Id;
        var operation = ConflictOperation;
        if (action == "abort" && (ShowConfirmAsync is null ||
            !await ShowConfirmAsync(LocalizedText.Get("git.conflicts.confirm_abort")))) return;
        ConflictBusy = true;
        IsBusy = true;
        try
        {
            var result = await client.ConflictOperationAsync(repo, new(operation, action));
            if (result.Success)
            {
                _conflictDrafts.Clear();
                ConflictDetail = null;
                ConflictResult = "";
            }
            await RefreshAllAsync();
            if (!result.Success) await NotifyAsync(result.Message ?? LocalizedText.Get("git.conflicts.failed"));
            else if (!HasConflicts && ConflictOperation is null) ActivePage = GitClientPage.Workspace;
        }
        catch (Exception ex) { await NotifyAsync(ex.Message); }
        finally { IsBusy = false; ConflictBusy = false; }
    }
}
