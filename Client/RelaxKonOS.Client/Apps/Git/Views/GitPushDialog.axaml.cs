using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitPushDialog : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public GitPushDialog(GitClientViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GitClientViewModel.PushHasSelectedCommit))
                UpdatePreviewLayout();
        };
        UpdatePreviewLayout();
    }

    private void UpdatePreviewLayout()
    {
        var hasSelectedCommit = _viewModel.PushHasSelectedCommit;
        PushPreviewGrid.RowSpacing = hasSelectedCommit ? 4 : 0;
        PushPreviewGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        PushPreviewGrid.RowDefinitions[2].Height = new GridLength(
            hasSelectedCommit ? 5 : 0,
            GridUnitType.Pixel);
        PushPreviewGrid.RowDefinitions[3].Height = hasSelectedCommit
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0, GridUnitType.Pixel);
    }

    private async void BranchLine_PointerPressed(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectPushRemoteBranchCommand.IsRunning) return;
        await _viewModel.SelectPushRemoteBranchCommand.ExecuteAsync(_dialog.Window);
    }

    private async void AllCommits_Click(object? sender, RoutedEventArgs e)
    {
        CommitList.SelectedIndex = -1;
        await _viewModel.SelectAllPushCommitsCommand.ExecuteAsync(null);
    }

    private async void CommitList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: GitCommitDto commit })
            await _viewModel.SelectPushCommitCommand.ExecuteAsync(commit);
    }

    private async void Push_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.PushIsLoading) return;
        _viewModel.PushIsLoading = true;
        try
        {
            if (await _viewModel.ExecutePushFromDialogAsync(_dialog.Window))
                _dialog.Close(true);
        }
        finally
        {
            _viewModel.PushIsLoading = false;
        }
    }

    private async void PushFileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PushFileTreeView.SelectedItem is not PushFileTreeNode { File: { } file } node) return;
        await _viewModel.ShowPushFileDiffAsync(file, _dialog.Window, node.CommitSha);
        PushFileTreeView.SelectedItem = null;
        e.Handled = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
}
