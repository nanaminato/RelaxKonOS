using RelaxKonOS.App.Git;
using Avalonia.Controls;

namespace RelaxKonOS.App.Git.Views;

public partial class GitHistoryView : UserControl
{
    public GitHistoryView() => InitializeComponent();
    public GitHistoryView(GitClientViewModel vm) : this() => DataContext = vm;
}
