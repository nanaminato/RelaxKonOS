using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>远程（remote）管理视图：列出所有 remote，支持添加/编辑/删除。</summary>
internal partial class GitRemotesView : UserControl
{
    private GitClientViewModel? _vm;

    public GitRemotesView() => InitializeComponent();
    public GitRemotesView(GitClientViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
    }

    private void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GitRemoteDto remote } && _vm is not null)
            _vm.EditRemoteCommand.Execute(remote);
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GitRemoteDto remote } && _vm is not null)
            _vm.RemoveRemoteCommand.Execute(remote);
    }
}
