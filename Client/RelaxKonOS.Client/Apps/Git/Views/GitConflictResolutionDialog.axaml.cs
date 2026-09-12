using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>Modal host for a multi-file conflict session.  It closes after continue/abort completes.</summary>
internal partial class GitConflictResolutionDialog : UserControl
{
    private readonly GitClientViewModel _viewModel;
    private readonly ModalDialog<bool> _dialog;

    public GitConflictResolutionDialog(GitClientViewModel viewModel, ModalDialog<bool> dialog)
    {
        _viewModel = viewModel;
        _dialog = dialog;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DetachedFromVisualTree += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(GitClientViewModel.HasConflicts) ||
             e.PropertyName == nameof(GitClientViewModel.ConflictOperation)) &&
            !_viewModel.HasConflicts && _viewModel.ConflictOperation is null)
            _dialog.Close(true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _dialog.Close(false);
}
