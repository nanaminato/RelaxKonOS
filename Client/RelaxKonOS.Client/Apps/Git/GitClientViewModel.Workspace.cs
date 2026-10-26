using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

public sealed record GitInputField(string LabelKey, string Value = "", bool Required = true);

public sealed partial class GitClientViewModel
{
    public ObservableCollection<GitTagDto> Tags { get; } = [];
    public ObservableCollection<GitStashDto> Stashes { get; } = [];
    public ObservableCollection<GitWorktreeDto> Worktrees { get; } = [];
    public Func<string, IReadOnlyList<GitInputField>, Task<string[]?>>? ShowWorkspaceInputAsync { get; set; }
    public Func<Uri, Task>? RequestOpenHostingUriAsync { get; set; }
    public Func<string, Task>? CreatePlainProjectAsync { get; set; }
    public Func<string, Task>? OpenPatchAsync { get; set; }

    partial void OnSelectedRepositoryChanged(GitRepositoryDto? value)
    {
        Interlocked.Increment(ref _graphRequestVersion);
        GraphCommits.Clear(); Tags.Clear(); Stashes.Clear(); Worktrees.Clear(); SelectedBranch = null;
        Status = null; Branches.Clear(); Remotes.Clear(); SelectedRemote = null;
        SelectedCommit = null; Commits.Clear(); CommitChangedFiles.Clear();
        StagedFiles.Clear(); UnstagedFiles.Clear(); UntrackedFiles.Clear(); ConflictFiles.Clear();
        HasConflicts = false; ConflictOperation = null; ConflictPaths.Clear();
        RebuildChangesList();
        Interlocked.Increment(ref _logFilterRequestVersion);
        _logFilterTimer?.Stop();
        _updatingLogFilterOptions = true;
        try
        {
            LogPathFilter = ""; CommitSearchText = "";
            SelectedLogBranch = null; SelectedLogAuthor = null; SelectedLogDate = LogDateOptions.FirstOrDefault();
        }
        finally { _updatingLogFilterOptions = false; }
    }

    private Task<string[]?> InputAsync(string key, params GitInputField[] fields)
        => ShowWorkspaceInputAsync?.Invoke(key, fields) ?? Task.FromResult<string[]?>(null);

    public async Task RefreshWorkspaceReferencesAsync()
    {
        if (SelectedRepository is not { } repo) return;
        try
        {
            var tags = client.ListTagsAsync(repo.Id);
            var stashes = client.ListStashesAsync(repo.Id);
            var trees = client.ListWorktreesAsync(repo.Id);
            await Task.WhenAll(tags, stashes, trees);
            if (SelectedRepository?.Id != repo.Id) return;
            Tags.Clear(); foreach (var tag in await tags) Tags.Add(tag);
            Stashes.Clear(); foreach (var stash in await stashes) Stashes.Add(stash);
            Worktrees.Clear(); foreach (var tree in await trees) Worktrees.Add(tree);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
    }

    public async Task RefreshBranchReferencesAsync()
    {
        if (SelectedRepository is not { } repo) return;
        try
        {
            var branches = client.ListBranchesAsync(repo.Id);
            var tags = client.ListTagsAsync(repo.Id);
            await Task.WhenAll(branches, tags);
            if (!ReferenceEquals(SelectedRepository, repo)) return;
            var selected = SelectedBranch;
            Branches.Clear(); foreach (var branch in await branches) Branches.Add(branch);
            SelectedBranch = Branches.FirstOrDefault(branch => branch.Name == selected?.Name && branch.IsRemote == selected.IsRemote)
                ?? Branches.FirstOrDefault(branch => branch.IsCurrent);
            Tags.Clear(); foreach (var tag in await tags) Tags.Add(tag);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedRepository, repo))
                await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message));
        }
    }

    private async Task ExecuteWorkspaceOperationAsync(GitRepositoryDto? repo, Func<string, Task<GitOperationResult>> action, bool prepare = true)
    {
        if (repo is null || SelectedRepository?.Id != repo.Id || !CanManage) return;
        if (prepare && !await PrepareEditorAsync()) return;
        if (SelectedRepository?.Id != repo.Id || !CanManage) return;
        IsBusy = true;
        try
        {
            var result = await action(repo.Id);
            if (!result.Success) await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Message));
            else StatusText = LocalizedText.Ref("git.workspace.completed");
            await RefreshAllAsync();
            await RefreshWorkspaceReferencesAsync();
            if (result.Conflicts is { Count: > 0 }) await PresentConflictResolutionAsync();
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task CheckoutReferenceAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.checkout_revision", new GitInputField("git.workspace.reference", "HEAD"));
        if (values is not null) await ExecuteWorkspaceOperationAsync(repo, id => client.CheckoutAsync(id, new(values[0])));
    }
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task OpenWorktreeAsync(GitWorktreeDto? tree)
    {
        if (tree is not null && await PrepareEditorAsync()) await ProbeAndOpenAsync(tree.Path);
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task NewTagAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.new_tag", new GitInputField("git.workspace.name"), new GitInputField("git.workspace.reference", "HEAD"));
        if (values is not null) await ExecuteWorkspaceOperationAsync(repo, id => client.CreateTagAsync(id, new(values[0], values[1])), false);
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RebaseOntoAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.rebase", new GitInputField("git.workspace.reference", SelectedBranch?.Name ?? ""));
        if (values is not null) await RebaseReferenceAsync(repo, values[0]);
    }
    private async Task RebaseReferenceAsync(GitRepositoryDto? repo, string reference)
    {
        if (ShowConfirmAsync is null || !await ShowConfirmAsync(LocalizedText.Format("git.workspace.rebase_confirm", reference))) return;
        await ExecuteWorkspaceOperationAsync(repo, id => client.RebaseAsync(id, new(reference)));
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task MergeIntoHeadAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.merge", new GitInputField("git.workspace.reference", SelectedBranch?.Name ?? ""));
        if (values is null || SelectedRepository?.Id != repo?.Id || !CanManage) return;
        await MergeBranchAsync(new GitBranchDto(values[0], ""));
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ResetHeadAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.reset", new GitInputField("git.workspace.reference", "HEAD"), new GitInputField("git.workspace.reset_mode", "mixed"));
        if (values is null || values[1] is not ("soft" or "mixed")) return;
        if (ShowConfirmAsync is null || !await ShowConfirmAsync(LocalizedText.Format("git.vm.reset_confirm_format", values[0]))) return;
        await ExecuteWorkspaceOperationAsync(repo, id => client.ResetAsync(id, new(values[0], values[1])));
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task NewWorktreeAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.new_worktree", new GitInputField("git.workspace.destination"), new GitInputField("git.workspace.reference", "HEAD"), new GitInputField("git.workspace.new_branch_optional", "", false));
        if (values is not null) await ExecuteWorkspaceOperationAsync(repo, id => client.CreateWorktreeAsync(id, new(values[0], values[1], string.IsNullOrWhiteSpace(values[2]) ? null : values[2])), false);
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task RemoveWorktreeAsync(GitWorktreeDto? tree)
    {
        var repo = SelectedRepository;
        if (tree is null || ShowConfirmAsync is null || !await ShowConfirmAsync(LocalizedText.Format("git.workspace.remove_worktree_confirm", tree.Path))) return;
        await ExecuteWorkspaceOperationAsync(repo, id => client.RemoveWorktreeAsync(id, new(tree.Path)), false);
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task SaveStashAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.stash_save", new GitInputField("git.workspace.message", "", false));
        if (values is not null) await ExecuteWorkspaceOperationAsync(repo, id => client.StashAsync(id, new("save", Message: values[0])));
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task ApplyStashAsync(GitStashDto? stash) => ChangeStashAsync(stash, "apply");
    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task PopStashAsync(GitStashDto? stash) => ChangeStashAsync(stash, "pop");
    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task DropStashAsync(GitStashDto? stash) => ChangeStashAsync(stash, "drop");
    private async Task ChangeStashAsync(GitStashDto? stash, string action)
    {
        var repo = SelectedRepository;
        if (stash is null) return;
        if (action == "drop" && (ShowConfirmAsync is null || !await ShowConfirmAsync(LocalizedText.Format("git.workspace.stash_drop_confirm", stash.Message)))) return;
        await ExecuteWorkspaceOperationAsync(repo, id => client.StashAsync(id, new(action, stash.Sha)), action != "drop");
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ApplyPatchFileAsync()
    {
        var repo = SelectedRepository;
        var values = await InputAsync("git.workspace.patch_apply", new GitInputField("git.workspace.patch_server_path"));
        if (values is not null) await ExecuteWorkspaceOperationAsync(repo, id => client.ApplyPatchAsync(id, new(values[0])));
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ExportWorkingPatchAsync(bool staged)
        => await ExportPatchAsync(new(Staged: staged));
    private async Task ExportPatchAsync(GitPatchRequest request)
    {
        if (SelectedRepository is not { } repo || OpenPatchAsync is null || !CanManage) return;
        if (!await PrepareEditorAsync()) return;
        IsBusy = true;
        try
        {
            var patch = await client.ExportPatchAsync(repo.Id, request);
            if (string.IsNullOrEmpty(patch.Patch)) { await NotifyAsync(LocalizedText.Get("git.workspace.no_patch")); return; }
            await OpenPatchAsync(patch.Patch);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task NewProjectAsync()
    {
        if (CreatePlainProjectAsync is null) return;
        var values = await InputAsync("git.workspace.new_project", new GitInputField("git.workspace.destination"));
        if (values is null || !CanOpenProject || !await PrepareEditorAsync()) return;
        IsBusy = true;
        try { await CreatePlainProjectAsync(values[0]); }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private async Task CloneRepositoryAsync()
    {
        var values = await InputAsync("git.workspace.clone", new GitInputField("git.workspace.url"), new GitInputField("git.workspace.destination"));
        if (values is null || !CanOpenProject || !await PrepareEditorAsync()) return;
        IsBusy = true;
        GitRepositoryDto? repo = null;
        try
        {
            var result = await client.CloneAsync(new(values[0], values[1], ResolveCredentialForUrl(values[0])));
            if (result.Result.RequiresCredentials && ShowGitCredentialsDialogAsync is not null)
            {
                var credentials = await ShowGitCredentialsDialogAsync(null);
                if (credentials is null) return;
                result = await client.CloneAsync(new(values[0], values[1], credentials));
                if (result.Result.Success) RememberCredentialForUrl(values[0], credentials);
            }
            if (!result.Result.Success || result.Repository is null)
            {
                await NotifyAsync(LocalizedText.Format("git.vm.failed_format", result.Result.Message));
                return;
            }
            repo = result.Repository;
            if (!Repositories.Any(item => item.Id == repo.Id)) Repositories.Add(repo);
        }
        catch (Exception ex) { await NotifyAsync(LocalizedText.Format("git.status.error_format", ex.Message)); }
        finally { IsBusy = false; }
        if (repo is not null) await OpenProjectAsync(repo);
    }
}
