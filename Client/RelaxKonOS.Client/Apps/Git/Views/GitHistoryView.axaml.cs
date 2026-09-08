using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitHistoryView : UserControl
{
    public GitHistoryView() => InitializeComponent();
    public GitHistoryView(GitClientViewModel vm) : this() => DataContext = vm;
}
