using RelaxKonOS.App.Git;
using Avalonia.Controls;

namespace RelaxKonOS.App.Git.Views;

public partial class GitConflictResolutionView : UserControl
{
    public GitConflictResolutionView() => InitializeComponent();
    public GitConflictResolutionView(GitClientViewModel vm) : this() => DataContext = vm;
}
