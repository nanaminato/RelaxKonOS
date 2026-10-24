using System.Collections.ObjectModel;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

public sealed partial class GitClientViewModel
{
    public Action<GitClientViewModel>? ConfigureLogSession { get; set; }
    private int _graphRequestVersion;
    public ObservableCollection<GitGraphCommitDto> GraphCommits { get; } = [];
    public Func<string, Task>? OpenFileHistoryTabAsync { get; set; }
    internal Task<IReadOnlyList<GitConsoleEntryDto>> ReadConsoleAsync(long after, CancellationToken ct)
        => SelectedRepository is { } repo ? client.GetConsoleAsync(repo.Id, after, ct) : Task.FromResult<IReadOnlyList<GitConsoleEntryDto>>([]);

    internal void CloseLogSession()
    {
        Stop(); CloseInlineDiffCommand.Execute(null); SelectedRepository = null;
    }

    internal GitClientViewModel CreateLogSession()
    {
        var session = new GitClientViewModel(client)
        {
            IsStarting = false, IsPickerMode = false, HasLoadedRepositories = true,
            RepositoryPathComparison = RepositoryPathComparison,
            PrepareEditorOperationAsync = PrepareEditorOperationAsync,
            OpenEditorFileAsync = OpenEditorFileAsync, OpenEditorDiffAsync = OpenEditorDiffAsync,
            OpenFileHistoryTabAsync = OpenFileHistoryTabAsync, OpenPatchAsync = OpenPatchAsync,
            ShowCommitDialogAsync = ShowCommitDialogAsync, ShowCreateBranchDialogAsync = ShowCreateBranchDialogAsync,
            ShowPullDialogAsync = ShowPullDialogAsync, ShowConflictResolutionDialogAsync = ShowConflictResolutionDialogAsync,
            ShowPushRejectedDialogAsync = ShowPushRejectedDialogAsync, ShowConfirmAsync = ShowConfirmAsync,
            ShowMessageAsync = ShowMessageAsync, ShowRenameBranchDialogAsync = ShowRenameBranchDialogAsync,
            ShowMergeDialogAsync = ShowMergeDialogAsync, ShowSetTrackingDialogAsync = ShowSetTrackingDialogAsync,
            ShowWorkspaceInputAsync = ShowWorkspaceInputAsync, ShowGitCredentialsDialogAsync = ShowGitCredentialsDialogAsync,
            ShowFileDiffAsync = ShowFileDiffAsync,
        };
        session.SelectedRepository = SelectedRepository;
        session.Status = Status;
        foreach (var branch in Branches) session.Branches.Add(branch);
        foreach (var tag in Tags) session.Tags.Add(tag);
        foreach (var remote in Remotes) session.Remotes.Add(remote);
        session.IsBusy = IsBusy;
        ConfigureLogSession?.Invoke(session);
        session.RefreshEditorLocalization();
        return session;
    }
    internal void CopyLogFiltersFrom(GitClientViewModel source)
    {
        _updatingLogFilterOptions = true;
        try
        {
            LogAuthorOptions.Clear(); foreach (var option in source.LogAuthorOptions) LogAuthorOptions.Add(option);
            SelectedLogBranch = source.SelectedLogBranch; SelectedLogAuthor = source.SelectedLogAuthor; SelectedLogDate = source.SelectedLogDate;
            SelectedCommit = source.SelectedCommit;
            CommitSearchText = source.CommitSearchText; BranchSearchText = source.BranchSearchText; LogPathFilter = source.LogPathFilter;
            IsLogCaseSensitive = source.IsLogCaseSensitive; IsLogRegex = source.IsLogRegex;
        }
        finally { _updatingLogFilterOptions = false; }
    }
    private async Task RefreshLogMetadataAsync(string repository)
    {
        var request = Interlocked.Increment(ref _graphRequestVersion);
        var reference = SelectedLogBranch?.Value;
        var graphTask = client.GetGraphAsync(repository, reference);
        var tagsTask = client.ListTagsAsync(repository);
        await Task.WhenAll(graphTask, tagsTask);
        if (SelectedRepository?.Id != repository || SelectedLogBranch?.Value != reference || request != Volatile.Read(ref _graphRequestVersion)) return;
        GraphCommits.Clear(); foreach (var item in await graphTask) GraphCommits.Add(item);
        Tags.Clear(); foreach (var item in await tagsTask) Tags.Add(item);
        RebuildLogBranchOptions();
    }
}
