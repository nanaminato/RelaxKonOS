using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git;

/// <summary>
/// The log page shows a file's diff beside the commit list instead of opening a window, so the
/// review loop (pick a commit, walk its files, read the change) stays in one place.
/// </summary>
public sealed partial class GitClientViewModel
{
    /// <summary>Guards against a slow response from a file the user already moved away from.</summary>
    private int _inlineDiffRequest;

    [ObservableProperty] private GitDiffPresentation? _inlineDiff;
    [ObservableProperty] private bool _isInlineDiffOpen;
    [ObservableProperty] private bool _isInlineDiffLoading;

    [RelayCommand]
    private void CloseInlineDiff()
    {
        _inlineDiffRequest++;
        IsInlineDiffOpen = false;
        IsInlineDiffLoading = false;
        InlineDiff = null;
    }

    /// <summary>
    /// Loads a commit file's diff into the pane. The pane opens before the fetch so the click has
    /// feedback, and clicking another file supersedes the pending request rather than racing it.
    /// </summary>
    private async Task ShowInlineCommitFileDiffAsync(GitFileChangeDto file)
    {
        if (SelectedRepository is null || SelectedCommit is null) return;
        if (OpenEditorDiffAsync is not null)
        {
            await OpenEditorDiffAsync(file, SelectedCommit.Sha);
            return;
        }

        var request = ++_inlineDiffRequest;
        InlineDiff = null;
        IsInlineDiffLoading = true;
        IsInlineDiffOpen = true;

        try
        {
            // ref=sha returns this commit relative to its parent, including root commits.
            var diff = await client.GetDiffAsync(SelectedRepository.Id, file.Path, staged: false, @ref: SelectedCommit.Sha);
            if (request != _inlineDiffRequest) return;
            FileDiff = diff;
            InlineDiff = new GitDiffPresentation(diff);
        }
        catch (Exception ex)
        {
            if (request != _inlineDiffRequest) return;
            CloseInlineDiff();
            await NotifyAsync(LocalizedText.Format("git.vm.diff_failed_format_v2", ex.Message));
            return;
        }
        finally
        {
            if (request == _inlineDiffRequest) IsInlineDiffLoading = false;
        }
    }
}
