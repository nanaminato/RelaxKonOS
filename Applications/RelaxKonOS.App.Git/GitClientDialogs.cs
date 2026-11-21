using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Git;

public sealed partial class GitClientDialogs(IGitDialogPresenter presenter, Func<string, string> localize)
{
    private string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, localize(key), arguments);

    public Task<GitCommitRequest?> ShowCommitDialogAsync(ManagedWindow owner, GitClientViewModel vm) =>
        presenter.ShowDialogAsync<GitCommitRequest?>(owner, localize("git.dialog.commit.title"),
            dialog => BuildCommitDialog(vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(470, 380));

    public Task<GitBranchCreateRequest?> ShowCreateBranchDialogAsync(ManagedWindow owner,
        GitClientViewModel vm, GitBranchDto? sourceBranch = null) =>
        presenter.ShowDialogAsync<GitBranchCreateRequest?>(owner,
            sourceBranch is null
                ? localize("git.dialog.new_branch.title")
                : Format("git.dialog.new_branch.from_title_format", sourceBranch.Name),
            dialog => BuildCreateBranchDialog(dialog, sourceBranch),
            new RelaxKonOS.Core.Primitives.Size(430, 205));

    public Task<GitPullRequest?> ShowPullDialogAsync(ManagedWindow owner, GitClientViewModel vm) =>
        presenter.ShowDialogAsync<GitPullRequest?>(owner, localize("git.dialog.pull.title"),
            dialog => BuildPullDialog(dialog),
            new RelaxKonOS.Core.Primitives.Size(420, 200));

    /// <summary>Prompts for the integration strategy after a non-fast-forward push rejection.</summary>
    public Task<GitPullRequest?> ShowPushRejectedDialogAsync(ManagedWindow owner) =>
        presenter.ShowDialogAsync<GitPullRequest?>(owner, localize("git.dialog.push_rejected.title"),
            BuildPushRejectedDialog,
            new RelaxKonOS.Core.Primitives.Size(500, 250));

    /// <summary>Hosts the multi-file three-way resolver as a modal child of the Git window.</summary>
    public async Task ShowConflictResolutionDialogAsync(ManagedWindow owner, GitClientViewModel vm)
    {
        _ = await presenter.ShowDialogAsync<bool>(owner, localize("git.dialog.conflicts.title"),
            dialog => new Views.GitConflictResolutionDialog(vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(1120, 740));
    }

    public Task<GitRepositoryRegistration?> ShowRegisterRepositoryDialogAsync(ManagedWindow owner, GitClientViewModel vm) =>
        presenter.ShowDialogAsync<GitRepositoryRegistration?>(owner, localize("git.dialog.register.title"),
            dialog => BuildRegisterDialog(dialog),
            new RelaxKonOS.Core.Primitives.Size(500, 260));

    public async Task<bool> ShowConfirmAsync(ManagedWindow owner, string message)
    {
        var result = await presenter.ShowDialogAsync<bool?>(owner, localize("git.dialog.confirm"),
            dialog => BuildConfirmDialog(message, dialog),
            new RelaxKonOS.Core.Primitives.Size(400, 180));
        return result ?? false;
    }

    /// <summary>Single-button modal notice. Use this for validation/errors that must grab the user's
    /// attention (e.g. "commit message required", "commit failed: …") instead of writing to StatusText.</summary>
    public async Task ShowMessageAsync(ManagedWindow owner, string message)
    {
        _ = await presenter.ShowDialogAsync<bool?>(owner, localize("git.dialog.notice"),
            dialog => BuildMessageDialog(message, dialog),
            new RelaxKonOS.Core.Primitives.Size(420, 200));
    }

    /// <summary>Shows the "Git engine unavailable" dialog. Installation belongs to Component Center,
    /// so this only explains the state and offers a re-check or exit.</summary>
    public Task<bool> ShowGitUnavailableAsync(ManagedWindow owner, GitClientViewModel vm) =>
        presenter.ShowDialogAsync<bool>(owner, localize("git.dialog.git_unavailable.title"),
            dialog => BuildGitUnavailableDialog(vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(520, 300));

    // ── 项目选择 / 探测 / 远程管理 ──

    /// <summary>Confirms whether to initialize a Git repository at the supplied path.</summary>
    public async Task<bool> ShowInitConfirmAsync(ManagedWindow owner, string path)
    {
        var result = await presenter.ShowDialogAsync<bool?>(owner, localize("git.dialog.init.title"),
            dialog => BuildInitConfirmDialog(path, dialog),
            new RelaxKonOS.Core.Primitives.Size(440, 220));
        return result ?? false;
    }

    /// <summary>Prompts the user for a new/edited remote. <paramref name="existing"/> null = add; non-null = edit.</summary>
    public Task<GitRemoteRequest?> ShowRemoteDialogAsync(ManagedWindow owner, GitRemoteDto? existing)
        => presenter.ShowDialogAsync<GitRemoteRequest?>(owner,
            existing is null ? localize("git.dialog.add_remote.title")
                             : Format("git.dialog.edit_remote.title_format", existing.Name),
            dialog => BuildRemoteDialog(existing, dialog),
            new RelaxKonOS.Core.Primitives.Size(480, 240));

    /// <summary>Prompts for a new branch name when renaming. Returns null on cancel.</summary>
    public Task<string?> ShowRenameBranchDialogAsync(ManagedWindow owner, GitBranchDto branch)
        => presenter.ShowDialogAsync<string?>(owner, Format("git.dialog.rename_branch.title_format", branch.Name),
            dialog => BuildRenameBranchDialog(branch, dialog),
            new RelaxKonOS.Core.Primitives.Size(420, 180));

    /// <summary>Prompts for merge strategy (merge/no-ff/ff-only/squash) + optional message. Returns null on cancel.</summary>
    public Task<GitMergeRequest?> ShowMergeDialogAsync(ManagedWindow owner, GitBranchDto sourceBranch, GitClientViewModel vm)
        => presenter.ShowDialogAsync<GitMergeRequest?>(owner, Format("git.dialog.merge.title_format", sourceBranch.Name),
            dialog => BuildMergeDialog(sourceBranch, vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(460, 340));

    /// <summary>Prompts for an upstream (remote/branch) to track, or choose "Unset". Returns null on cancel.</summary>
    public Task<GitBranchTrackingRequest?> ShowSetTrackingDialogAsync(ManagedWindow owner, GitBranchDto localBranch, GitClientViewModel vm)
        => presenter.ShowDialogAsync<GitBranchTrackingRequest?>(owner, Format("git.dialog.set_tracking.title_format", localBranch.Name),
            dialog => BuildSetTrackingDialog(localBranch, vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(460, 260));

    public Task<string[]?> ShowWorkspaceInputAsync(ManagedWindow owner, string titleKey, IReadOnlyList<GitInputField> fields)
        => presenter.ShowDialogAsync<string[]?>(owner, localize(titleKey), dialog =>
        {
            var panel = new StackPanel { Margin = new(20), Spacing = 10 };
            var boxes = new List<TextBox>();
            foreach (var field in fields)
            {
                panel.Children.Add(new TextBlock { Text = localize(field.LabelKey), TextWrapping = TextWrapping.Wrap });
                var box = new TextBox { Text = field.Value };
                boxes.Add(box); panel.Children.Add(box);
            }
            var ok = new Button { Content = localize("common.ok"), HorizontalAlignment = HorizontalAlignment.Right };
            void Validate() => ok.IsEnabled = fields.Select((field, index) => !field.Required || !string.IsNullOrWhiteSpace(boxes[index].Text)).All(valid => valid);
            foreach (var box in boxes) box.TextChanged += (_, _) => Validate();
            ok.Click += (_, _) => dialog.Close(boxes.Select(box => box.Text?.Trim() ?? "").ToArray());
            var cancel = new Button { Content = localize("common.cancel") };
            cancel.Click += (_, _) => dialog.Cancel();
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } });
            Validate();
            panel.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => boxes.FirstOrDefault()?.Focus());
            return panel;
        }, new RelaxKonOS.Core.Primitives.Size(480, 140 + fields.Count * 78));

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private Control BuildInitConfirmDialog(string path, ModalDialog<bool?> dialog)
    {
        var msg = new TextBlock
        {
            Text = Format("git.dialog.init.message_format", path),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        var initBtn = new Button { Content = localize("git.dialog.init.confirm"), Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"), Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"), Padding = new(14, 6) };
        initBtn.Click += (_, _) => dialog.Close(true);
        var cancelBtn = new Button { Content = localize("git.dialog.init.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Close(false);
        return new StackPanel
        {
            Margin = new(20),
            Spacing = 16,
            Children =
            {
                msg,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { initBtn, cancelBtn },
                },
            },
        };
    }

    private Control BuildPushRejectedDialog(ModalDialog<GitPullRequest?> dialog)
    {
        var message = new TextBlock
        {
            Text = localize("git.dialog.push_rejected.message"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };
        var detail = new TextBlock
        {
            Text = localize("git.dialog.push_rejected.detail"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextSecondaryBrush"),
        };
        var merge = new Button
        {
            Content = localize("git.dialog.push_rejected.merge"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(14, 6),
        };
        merge.Click += (_, _) => dialog.Close(new GitPullRequest("merge"));
        var rebase = new Button { Content = localize("git.dialog.push_rejected.rebase"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        rebase.Click += (_, _) => dialog.Close(new GitPullRequest("rebase"));
        var cancel = new Button { Content = localize("git.dialog.push_rejected.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancel.Click += (_, _) => dialog.Cancel();
        return new StackPanel
        {
            Margin = new(20),
            Spacing = 14,
            Children =
            {
                message,
                detail,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { merge, rebase, cancel },
                },
            },
        };
    }

    private Control BuildRemoteDialog(GitRemoteDto? existing, ModalDialog<GitRemoteRequest?> dialog)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.remote.name_placeholder"),
            Text = existing?.Name ?? string.Empty,
        };
        var urlBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.remote.url_placeholder"),
            Text = existing?.FetchUrl ?? string.Empty,
        };
        var pushBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.remote.push_placeholder"),
            Text = existing?.PushUrl ?? string.Empty,
        };

        var saveBtn = new Button
        {
            Content = existing is null ? localize("git.dialog.remote.add") : localize("git.dialog.remote.save"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(14, 6),
        };
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(urlBox.Text)) return;
            dialog.Close(new GitRemoteRequest(
                nameBox.Text!.Trim(),
                urlBox.Text!.Trim(),
                string.IsNullOrWhiteSpace(pushBox.Text) ? null : pushBox.Text!.Trim()));
        };

        var cancelBtn = new Button { Content = localize("git.dialog.remote.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = localize("git.dialog.remote.name_label"), FontSize = 13 },
                nameBox,
                new TextBlock { Text = localize("git.dialog.remote.fetch_url_label"), FontSize = 13 },
                urlBox,
                new TextBlock { Text = localize("git.dialog.remote.push_url_label"), FontSize = 13 },
                pushBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { saveBtn, cancelBtn },
                },
            },
        };
    }

    // ── Dialog builders (programmatic — no separate AXAML files needed) ──

    private Control BuildCommitDialog(GitClientViewModel vm, ModalDialog<GitCommitRequest?> dialog)
    {
        var messageBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.commit.message_placeholder"),
            MinHeight = 60,
            AcceptsReturn = true,
            Text = vm.CommitMessage
        };
        // Sync edits back to ViewModel
        messageBox.TextChanged += (_, _) => vm.CommitMessage = messageBox.Text ?? string.Empty;

        var amendCheck = new CheckBox { Content = localize("git.dialog.commit.amend") };

        var fileListTitle = new TextBlock
        {
            Text = Format("git.dialog.commit.files_to_commit_format", vm.SelectedCount),
            FontSize = 12,
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextSecondaryBrush"),
            Margin = new(0, 8, 0, 4)
        };

        var fileListBox = new ListBox
        {
            Height = 100,
            ItemsSource = vm.SelectedFilePaths,
        };
        fileListBox.SelectionChanged += async (_, _) =>
        {
            if (fileListBox.SelectedItem is not string path) return;
            var file = vm.Changes.FirstOrDefault(change => string.Equals(change.Path, path, StringComparison.Ordinal))?.File;
            if (file is not null)
                await vm.ShowWorkingTreeFileDiffAsync(file, dialog.Window);
            // A file name is an action here, allowing another click to reopen its diff.
            fileListBox.SelectedItem = null;
        };

        var commitBtn = new Button
        {
            Content = localize("git.dialog.commit.commit_btn"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        commitBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(messageBox.Text))
                return;
            var paths = vm.SelectedFilePaths.ToArray();
            if (paths.Length == 0)
                return;
            dialog.Close(new GitCommitRequest(messageBox.Text!, paths, amendCheck.IsChecked == true));
        };

        var cancelBtn = new Button { Content = localize("git.dialog.commit.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 8,
            Children =
            {
                messageBox,
                amendCheck,
                fileListTitle,
                fileListBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { commitBtn, cancelBtn }
                }
            }
        };
    }

    private Control BuildCreateBranchDialog(ModalDialog<GitBranchCreateRequest?> dialog, GitBranchDto? sourceBranch)
    {
        var suggestedName = sourceBranch is null
            ? string.Empty
            : sourceBranch.IsRemote && sourceBranch.Name.Contains('/')
                ? sourceBranch.Name[(sourceBranch.Name.IndexOf('/') + 1)..]
                : sourceBranch.Name;
        var nameBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.new_branch.name_placeholder"),
            Text = suggestedName,
        };
        // The suggested source name is meant to be overwritten, just like IDEA's
        // branch dialog.  SelectAll also keeps keyboard input fast for a new name.
        nameBox.SelectAll();
        var checkoutCheck = new CheckBox
        {
            Content = localize("git.dialog.new_branch.checkout"),
            IsChecked = true,
        };
        var resetExistingCheck = new CheckBox
        {
            Content = localize("git.dialog.new_branch.reset_existing"),
            IsChecked = false,
            Margin = new Thickness(14, 0, 0, 0),
        };

        var createBtn = new Button { Content = localize("git.dialog.new_branch.create"), HorizontalAlignment = HorizontalAlignment.Right };
        createBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            dialog.Close(new GitBranchCreateRequest(
                nameBox.Text!.Trim(),
                sourceBranch?.Name,
                Track: sourceBranch?.IsRemote == true,
                Checkout: checkoutCheck.IsChecked == true,
                ResetExisting: resetExistingCheck.IsChecked == true));
        };

        var cancelBtn = new Button { Content = localize("git.dialog.new_branch.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = localize("git.dialog.new_branch.name_label"), FontSize = 13 },
                nameBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { checkoutCheck, resetExistingCheck }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { createBtn, cancelBtn }
                }
            }
        };
    }

    private Control BuildPullDialog(ModalDialog<GitPullRequest?> dialog)
    {
        var mergeRadio = new RadioButton { Content = localize("git.dialog.pull.merge"), IsChecked = true, GroupName = "strategy" };
        var rebaseRadio = new RadioButton { Content = localize("git.dialog.pull.rebase"), GroupName = "strategy" };

        var pullBtn = new Button { Content = localize("git.dialog.pull.pull_btn"), HorizontalAlignment = HorizontalAlignment.Right };
        pullBtn.Click += (_, _) =>
            dialog.Close(new GitPullRequest(rebaseRadio.IsChecked == true ? "rebase" : "merge"));

        var cancelBtn = new Button { Content = localize("git.dialog.pull.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = localize("git.dialog.pull.strategy"), FontSize = 13 },
                new StackPanel { Spacing = 8, Children = { mergeRadio, rebaseRadio } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { pullBtn, cancelBtn }
                }
            }
        };
    }

    private Control BuildRegisterDialog(ModalDialog<GitRepositoryRegistration?> dialog)
    {
        var nameBox = new TextBox { PlaceholderText = localize("git.dialog.register.name_placeholder") };
        var pathBox = new TextBox { PlaceholderText = localize("git.dialog.register.path_placeholder") };

        var registerBtn = new Button { Content = localize("git.dialog.register.register_btn"), HorizontalAlignment = HorizontalAlignment.Right };
        registerBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(pathBox.Text)) return;
            dialog.Close(new GitRepositoryRegistration(nameBox.Text!, pathBox.Text!));
        };

        var cancelBtn = new Button { Content = localize("git.dialog.register.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = localize("git.dialog.register.name_label"), FontSize = 13 },
                nameBox,
                new TextBlock { Text = localize("git.dialog.register.path_label"), FontSize = 13 },
                pathBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { registerBtn, cancelBtn }
                }
            }
        };
    }

    private Control BuildConfirmDialog(string message, ModalDialog<bool?> dialog)
    {
        var yesBtn = new Button { Content = localize("git.dialog.confirm.yes"), HorizontalAlignment = HorizontalAlignment.Right };
        yesBtn.Click += (_, _) => dialog.Close(true);

        var noBtn = new Button { Content = localize("git.dialog.confirm.no"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(8, 0, 0, 0) };
        noBtn.Click += (_, _) => dialog.Close(false);

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yesBtn, noBtn }
                }
            }
        };
    }

    private Control BuildMessageDialog(string message, ModalDialog<bool?> dialog)
    {
        var okBtn = new Button
        {
            Content = localize("git.dialog.notice.ok"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(20, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Click += (_, _) => dialog.Close(true);

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { okBtn },
                },
            },
        };
    }

    private Control BuildGitUnavailableDialog(GitClientViewModel vm, ModalDialog<bool> dialog)
    {
        var header = new TextBlock
        {
            Text = localize("git.dialog.unavailable.message"),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 14,
        };

        var refreshBtn = new Button { Content = localize("git.dialog.unavailable.refresh") };
        refreshBtn.Click += async (_, _) =>
        {
            await vm.RefreshEngineStatusCommand.ExecuteAsync(null);
            if (vm.IsGitAvailable) dialog.Close(true);
        };

        var cancelBtn = new Button { Content = localize("git.dialog.unavailable.exit"), Margin = new Thickness(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Close(false);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { refreshBtn, cancelBtn },
            Spacing = 8,
        };
        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children = { header, footer }
        };
    }

    // ── 分支管理对话框构建器 ──

    private Control BuildRenameBranchDialog(GitBranchDto branch, ModalDialog<string?> dialog)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.rename.placeholder"),
            Text = branch.Name,
        };
        nameBox.SelectAll();

        var renameBtn = new Button
        {
            Content = localize("git.dialog.rename.rename_btn"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(14, 6),
        };
        renameBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) return;
            var newName = nameBox.Text!.Trim();
            if (newName == branch.Name) return;
            dialog.Close(newName);
        };

        var cancelBtn = new Button { Content = localize("git.dialog.rename.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        var currentSuffix = branch.IsCurrent ? localize("git.dialog.rename.current_suffix") : string.Empty;

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = Format("git.dialog.rename.current_branch_format", branch.Name, currentSuffix),
                    FontSize = 13,
                    Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextSecondaryBrush"),
                },
                new TextBlock { Text = localize("git.dialog.rename.new_name_label"), FontSize = 13 },
                nameBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { renameBtn, cancelBtn },
                },
            },
        };
    }

    private Control BuildMergeDialog(GitBranchDto sourceBranch, GitClientViewModel vm, ModalDialog<GitMergeRequest?> dialog)
    {
        var currentBranchName = vm.Status?.Branch ?? localize("git.dialog.merge.unknown_branch");

        var mergeRadio = new RadioButton { Content = localize("git.dialog.merge.merge_default"), GroupName = "strategy", IsChecked = true };
        var noFfRadio = new RadioButton { Content = localize("git.dialog.merge.no_ff"), GroupName = "strategy" };
        var ffOnlyRadio = new RadioButton { Content = localize("git.dialog.merge.ff_only"), GroupName = "strategy" };
        var squashRadio = new RadioButton { Content = localize("git.dialog.merge.squash"), GroupName = "strategy" };

        var noCommitCheck = new CheckBox
        {
            Content = localize("git.dialog.merge.no_commit"),
        };

        var messageBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.merge.message_placeholder"),
            MinHeight = 70,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };

        var mergeBtn = new Button
        {
            Content = localize("git.dialog.merge.merge_btn"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(14, 6),
        };
        mergeBtn.Click += (_, _) =>
        {
            string strategy;
            bool noCommit;
            if (noFfRadio.IsChecked == true) { strategy = "no-ff"; noCommit = noCommitCheck.IsChecked == true; }
            else if (ffOnlyRadio.IsChecked == true) { strategy = "ff-only"; noCommit = false; }
            else if (squashRadio.IsChecked == true) { strategy = "squash"; noCommit = true; }
            else { strategy = "merge"; noCommit = noCommitCheck.IsChecked == true; }

            dialog.Close(new GitMergeRequest(
                Source: sourceBranch.Name,
                Strategy: strategy,
                NoCommit: noCommit,
                Message: string.IsNullOrWhiteSpace(messageBox.Text) ? null : messageBox.Text!.Trim()));
        };

        var cancelBtn = new Button { Content = localize("git.dialog.merge.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = Format("git.dialog.merge.title_format_v2", sourceBranch.Name, currentBranchName),
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock { Text = localize("git.dialog.merge.strategy_label"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { mergeRadio, noFfRadio, ffOnlyRadio, squashRadio },
                },
                noCommitCheck,
                new TextBlock { Text = localize("git.dialog.merge.message_label"), FontSize = 13 },
                messageBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { mergeBtn, cancelBtn },
                },
            },
        };
    }

    private Control BuildSetTrackingDialog(GitBranchDto localBranch, GitClientViewModel vm, ModalDialog<GitBranchTrackingRequest?> dialog)
    {
        var currentTracking = localBranch.Tracking ?? localize("git.dialog.tracking.not_set");

        var unsetRadio = new RadioButton { Content = localize("git.dialog.tracking.unset"), GroupName = "tracking" };
        var autoRadio = new RadioButton { Content = Format("git.dialog.tracking.auto_format", localBranch.Name), GroupName = "tracking", IsChecked = true };
        var customRadio = new RadioButton { Content = localize("git.dialog.tracking.custom"), GroupName = "tracking" };

        var remotes = vm.Remotes.ToList();
        var remoteBox = new ComboBox
        {
            PlaceholderText = localize("git.dialog.tracking.remote_placeholder"),
            ItemsSource = remotes.Select(r => r.Name).ToList(),
        };
        if (remotes.Count > 0) remoteBox.SelectedIndex = 0;

        var branchBox = new TextBox
        {
            PlaceholderText = localize("git.dialog.tracking.branch_placeholder"),
            Text = localBranch.Name,
        };

        // 自定义区域容器：选中 custom 时启用
        var customPanel = new StackPanel
        {
            IsEnabled = false,
            Spacing = 8,
        };
        var label1 = new TextBlock { Text = localize("git.dialog.tracking.remote_label"), FontSize = 13 };
        var label2 = new TextBlock { Text = localize("git.dialog.tracking.branch_label"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        customPanel.Children.Add(label1);
        customPanel.Children.Add(remoteBox);
        customPanel.Children.Add(label2);
        customPanel.Children.Add(branchBox);

        // 点击 any Radio 时刷新 customPanel 启用状态
        void RefreshEnabled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            customPanel.IsEnabled = customRadio.IsChecked == true;
        }
        unsetRadio.Click += RefreshEnabled;
        autoRadio.Click += RefreshEnabled;
        customRadio.Click += RefreshEnabled;

        var setBtn = new Button
        {
            Content = localize("git.dialog.tracking.set_btn"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(14, 6),
        };
        setBtn.Click += (_, _) =>
        {
            if (unsetRadio.IsChecked == true)
            {
                dialog.Close(new GitBranchTrackingRequest(Upstream: null, Remote: null, Branch: null));
                return;
            }
            if (autoRadio.IsChecked == true)
            {
                dialog.Close(new GitBranchTrackingRequest(Remote: "origin", Branch: localBranch.Name));
                return;
            }
            // 自定义
            var remote = remoteBox.SelectedItem as string;
            var branch = string.IsNullOrWhiteSpace(branchBox.Text) ? null : branchBox.Text!.Trim();
            if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(branch)) return;
            dialog.Close(new GitBranchTrackingRequest(Remote: remote, Branch: branch));
        };

        var cancelBtn = new Button { Content = localize("git.dialog.tracking.cancel"), Padding = new(14, 6), Margin = new(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => dialog.Cancel();

        var currentSuffix = localBranch.IsCurrent ? localize("git.dialog.tracking.current_suffix") : string.Empty;

        return new StackPanel
        {
            Margin = new(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = Format("git.dialog.tracking.local_branch_format", localBranch.Name, currentSuffix),
                    FontSize = 13,
                    Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextSecondaryBrush"),
                },
                new TextBlock
                {
                    Text = Format("git.dialog.tracking.current_tracking_format", currentTracking),
                    FontSize = 13,
                    Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextSecondaryBrush"),
                },
                new Separator(),
                new StackPanel
                {
                    Spacing = 6,
                    Children = { unsetRadio, autoRadio, customRadio },
                },
                customPanel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { setBtn, cancelBtn },
                },
            },
        };
    }

    // ── Push dialog ──

    public Task<bool> ShowPushDialogAsync(ManagedWindow owner, GitClientViewModel vm)
        => presenter.ShowDialogAsync<bool>(owner,
            Format("git.dialog.push.title", vm.SelectedRepository?.Name ?? string.Empty),
            dialog => new Views.GitPushDialog(vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(820, 580));

    /// <summary>Shows a native, read-only unified diff for a file selected from workspace, history, or push preview.</summary>
    public Task<bool> ShowFileDiffAsync(ManagedWindow owner, GitDiffDto diff, GitClientViewModel vm)
        => presenter.ShowDialogAsync<bool>(owner,
            Format("git.dialog.diff.title_format", diff.Path),
            dialog => new Views.GitDiffDialog(diff, vm, dialog),
            new RelaxKonOS.Core.Primitives.Size(980, 680));

    /// <summary>Prompts for a HTTPS username and personal access token as a child of the push preview dialog.
    /// The dialog returns the secret to the active request only; it is never stored in the client.</summary>
    public Task<GitCredentialRequest?> ShowGitCredentialsDialogAsync(ManagedWindow owner, string? host)
        => presenter.ShowDialogAsync<GitCredentialRequest?>(owner,
            localize("git.dialog.credentials.title"),
            dialog => BuildGitCredentialsDialog(dialog, host),
            new RelaxKonOS.Core.Primitives.Size(480, 380));

    private Control BuildGitCredentialsDialog(ModalDialog<GitCredentialRequest?> dialog, string? host)
    {
        var notice = new TextBlock
        {
            Text = (string.IsNullOrEmpty(host) ? "" : host + "\n") + localize("git.dialog.credentials.notice"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextTertiaryBrush"),
        };
        var username = new TextBox { PlaceholderText = localize("git.dialog.credentials.username_placeholder") };
        var password = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = localize("git.dialog.credentials.token_placeholder"),
        };
        var remember = new CheckBox
        {
            Content = localize("git.dialog.credentials.remember"),
            IsChecked = true,
        };
        var save = new Button
        {
            Content = localize("git.dialog.credentials.confirm"),
            Background = RelaxKonOS.UI.Themes.ThemeResources.Brush("AccentBrush"),
            Foreground = RelaxKonOS.UI.Themes.ThemeResources.Brush("TextOnAccentBrush"),
            Padding = new(14, 6),
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(username.Text) || string.IsNullOrWhiteSpace(password.Text)) return;
            dialog.Close(new GitCredentialRequest(username.Text.Trim(), password.Text, remember.IsChecked == true));
        };
        var cancel = new Button
        {
            Content = localize("git.dialog.credentials.cancel"),
            Padding = new(14, 6),
            Margin = new(8, 0, 0, 0),
        };
        cancel.Click += (_, _) => dialog.Cancel();

        return new StackPanel
        {
            Margin = new(18),
            Spacing = 10,
            Children =
            {
                notice,
                new TextBlock { Text = localize("git.dialog.credentials.username"), FontSize = 13 },
                username,
                new TextBlock { Text = localize("git.dialog.credentials.token"), FontSize = 13 },
                password,
                remember,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { save, cancel },
                },
            },
        };
    }

    // ── Remote / Branch picker dialog ──

    public Task<(string Remote, string Branch)?> ShowRemoteBranchPickerDialogAsync(
        ManagedWindow owner, string? currentRemote, string? currentBranch, GitClientViewModel vm)
        => presenter.ShowDialogAsync<(string Remote, string Branch)?>(owner,
            Format("git.dialog.push.dialog_title_format", currentRemote ?? localize("git.dialog.push.not_selected")),
            dialog => new Views.GitRemoteBranchPickerDialog(vm, dialog, currentRemote, currentBranch),
            new RelaxKonOS.Core.Primitives.Size(460, 320));
}
