using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitBranchesView : UserControl
{
    private GitClientViewModel? _vm;

    public GitBranchesView() => InitializeComponent();
    public GitBranchesView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void Checkout_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedBranch is not null)
            _vm.CheckoutCommand.Execute(_vm.SelectedBranch);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedBranch is not null)
            _vm.DeleteBranchCommand.Execute(_vm.SelectedBranch);
    }
}
