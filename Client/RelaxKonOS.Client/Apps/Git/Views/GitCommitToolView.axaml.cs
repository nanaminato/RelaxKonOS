using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitCommitToolView : UserControl
{
    public GitCommitToolView() => InitializeComponent();
    private GitClientViewModel? Model => DataContext as GitClientViewModel;
    private async void Diff_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: GitFileChangeDto file } && Model is { } model)
            await model.ShowWorkingTreeFileDiffAsync(file, null);
    }
    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: GitFileChangeDto file } && Model is { } model)
            await model.OpenFileInEditorCommand.ExecuteAsync(file);
    }
    private async void Stage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: GitFileChangeDto file } && !file.Staged && Model is { CanManage: true } model)
            await model.StageFileContextCommand.ExecuteAsync(file);
    }
    private async void Unstage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: GitFileChangeDto file } && file.Staged && Model is { CanManage: true } model)
            await model.UnstageFileCommand.ExecuteAsync(file);
    }
    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: GitFileChangeDto file } && Model is { CanManage: true } model)
            await model.RevertFileChangeCommand.ExecuteAsync(file);
    }
}
