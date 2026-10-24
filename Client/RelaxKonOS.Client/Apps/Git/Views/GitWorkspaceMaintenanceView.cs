using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal sealed class GitWorkspaceMaintenanceView : UserControl
{
    public GitWorkspaceMaintenanceView(GitClientViewModel vm, string kind)
    {
        var stash = kind == "stashes";
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new(10) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 8) };
        var refresh = new Button { Content = LocalizedText.Get("git.refresh") };
        refresh.Click += async (_, _) => await vm.RefreshWorkspaceReferencesAsync();
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(new Button { Content = LocalizedText.Get(stash ? "git.workspace.stash_save" : "git.workspace.new_worktree"), Command = stash ? vm.SaveStashCommand : vm.NewWorktreeCommand });
        root.Children.Add(toolbar);
        var list = new ListBox();
        Button Action(string key, ICommand command, object parameter) => new() { Content = LocalizedText.Get(key), Command = command, CommandParameter = parameter, Padding = new(8, 3) };
        if (stash)
        {
            list.ItemsSource = vm.Stashes;
            list.ItemTemplate = new FuncDataTemplate<GitStashDto>((item, _) =>
            {
                if (item is null) return new TextBlock();
                return new StackPanel { Spacing = 6, Margin = new(5), Children =
                {
                    new TextBlock { Text = item.Reference + " · " + item.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
                    {
                        Action("git.workspace.stash_apply", vm.ApplyStashCommand, item),
                        Action("git.workspace.stash_pop", vm.PopStashCommand, item),
                        Action("git.workspace.stash_drop", vm.DropStashCommand, item),
                    } },
                } };
            });
        }
        else
        {
            list.ItemsSource = vm.Worktrees;
            list.ItemTemplate = new FuncDataTemplate<GitWorktreeDto>((item, _) =>
            {
                if (item is null) return new TextBlock();
                var remove = Action("git.workspace.worktree_remove", vm.RemoveWorktreeCommand, item);
                remove.IsEnabled = !item.IsLocked && !string.Equals(item.Path, vm.SelectedRepository?.Path, vm.RepositoryPathComparison);
                return new StackPanel { Spacing = 6, Margin = new(5), Children =
                {
                    new TextBlock { Text = item.Path, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = item.Branch ?? "HEAD", FontSize = 11 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Action("common.open_ellipsis", vm.OpenWorktreeCommand, item), remove } },
                } };
            });
        }
        Grid.SetRow(list, 1); root.Children.Add(list);
        Content = root;
    }
}
