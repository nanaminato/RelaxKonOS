using RelaxKonOS.App.Git;
using Avalonia.Controls;

namespace RelaxKonOS.App.Git.Views;

public partial class GitOverviewView : UserControl
{
    public GitOverviewView() => InitializeComponent();
    public GitOverviewView(GitClientViewModel vm) : this() => DataContext = vm;
}
