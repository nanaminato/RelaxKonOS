using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitRemoteBranchPickerDialog : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private readonly ModalDialog<(string Remote, string Branch)?> _dialog;

    public GitRemoteBranchPickerDialog(GitClientViewModel viewModel, ModalDialog<(string Remote, string Branch)?> dialog,
        string? currentRemote, string? currentBranch)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;

        InitializeRemoteAndBranch(currentRemote, currentBranch);
        _ = LoadRemoteBranchesAsync();
    }

    private void InitializeRemoteAndBranch(string? currentRemote, string? currentBranch)
    {
        RemoteBox.ItemsSource = _viewModel.Remotes.Select(r => r.Name).ToList();

        if (!string.IsNullOrWhiteSpace(currentRemote))
        {
            var match = _viewModel.Remotes.FirstOrDefault(r => r.Name == currentRemote);
            if (match is not null)
                RemoteBox.SelectedItem = match.Name;
            else
                RemoteBox.Text = currentRemote;
        }
        else if (_viewModel.Remotes.Count > 0)
        {
            RemoteBox.SelectedIndex = 0;
        }

        BranchBox.Text = currentBranch ?? string.Empty;
    }

    private async System.Threading.Tasks.Task LoadRemoteBranchesAsync()
    {
        if (_viewModel.SelectedRepository is null) return;
        try
        {
            var branches = await _viewModel.GetRemoteBranchNamesAsync();
            if (branches.Count > 0)
            {
                BranchBox.ItemsSource = branches;
                StatusText.IsVisible = false;
            }
        }
        catch
        {
            StatusText.IsVisible = true;
        }
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var remote = RemoteBox.SelectedItem as string ?? RemoteBox.Text;
        var branch = BranchBox.Text;
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(branch)) return;
        _dialog.Close((remote.Trim(), branch.Trim()));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _dialog.Cancel();
}
