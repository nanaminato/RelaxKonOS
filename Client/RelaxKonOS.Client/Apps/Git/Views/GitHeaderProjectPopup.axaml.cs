using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Controls;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal sealed record GitHeaderProjectEntry(string Name, string Path, GitRepositoryDto? Repository, string? Branch);

internal partial class GitHeaderProjectPopup : UserControl
{
    private GitClientViewModel? _vm;
    private ProjectEditorViewModel? _project;
    private Action? _close;
    private readonly List<GitHeaderProjectEntry> _recent = [];
    public IReadOnlyList<GitHeaderProjectEntry> RecentProjects => _recent;
    public GitHeaderProjectPopup() => InitializeComponent();
    public void Configure(GitClientViewModel vm, ProjectEditorViewModel project, Action close)
    {
        _vm = vm; _project = project; _close = close;
        NewProjectAction.Command = vm.NewProjectCommand;
        OpenProjectAction.Command = vm.OpenFolderCommand;
        CloneProjectAction.Command = vm.CloneRepositoryCommand;
        NewProjectAction.Click += (_, _) => close(); OpenProjectAction.Click += (_, _) => close(); CloneProjectAction.Click += (_, _) => close();
    }
    public void RecordCurrentProject()
    {
        if (_vm is null || _project?.ProjectPath is not { } path) return;
        var repo = _vm.SelectedRepository is { } selected && string.Equals(selected.Path, path, _vm.RepositoryPathComparison) ? selected : null;
        _recent.RemoveAll(item => string.Equals(item.Path, path, _vm.RepositoryPathComparison));
        _recent.Insert(0, new(_project.ProjectName ?? path, path, repo, repo is null ? null : _vm.Status?.Branch));
        if (_recent.Count > 12) _recent.RemoveRange(12, _recent.Count - 12);
    }
    public void Rebuild()
    {
        if (_vm is null || _project is null) return;
        RecordCurrentProject(); ProjectEntries.Children.Clear();
        void Heading(string key) => ProjectEntries.Children.Add(new TextBlock { Text = LocalizedText.Get(key), FontSize = 11, Margin = new(8, 4, 8, 5), Foreground = ThemeBrushes.Get("TextSecondaryBrush") });
        var current = _recent.FirstOrDefault(item => string.Equals(item.Path, _project.ProjectPath, _vm.RepositoryPathComparison));
        if (current is not null) { Heading("git.workspace.open_projects"); AddProject(current, true); }
        var recent = _recent.Where(item => item != current).ToArray();
        if (recent.Length > 0)
        {
            ProjectEntries.Children.Add(new Separator { Margin = new(0, 7) }); Heading("git.workspace.recent_projects");
            foreach (var item in recent) AddProject(item, false);
        }
        var registered = _vm.Repositories.Where(repo => !_recent.Any(item => string.Equals(item.Path, repo.Path, _vm.RepositoryPathComparison))).ToArray();
        if (registered.Length > 0)
        {
            ProjectEntries.Children.Add(new Separator { Margin = new(0, 7) }); Heading("git.workspace.registered_projects");
            foreach (var repo in registered) AddProject(new(repo.Name, repo.Path, repo, repo.CurrentBranch), false);
        }
    }
    private void AddProject(GitHeaderProjectEntry entry, bool current)
    {
        var row = new Grid { ColumnDefinitions = new("22,*"), ColumnSpacing = 7, Margin = new(0, 3) };
        var icon = new Border { Width = 20, Height = 20, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.Parse(new[] { "#D9795B", "#B84BB5", "#1E9DAF", "#9652D5" }[entry.Name.Sum(character => (int)character) % 4])), Child = new TextBlock { Text = entry.Name[..Math.Min(2, entry.Name.Length)].ToUpperInvariant(), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        row.Children.Add(icon);
        var texts = new StackPanel { Spacing = 3 };
        texts.Children.Add(new TextBlock { Text = entry.Name, FontSize = 12, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = entry.Path, FontSize = 11, Foreground = ThemeBrushes.Get("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(entry.Branch)) texts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { new WorkspaceIcon { Kind = "Branch", Width = 12, Height = 12, Foreground = ThemeBrushes.Get("TextSecondaryBrush") }, new TextBlock { Text = entry.Branch, FontSize = 11, Foreground = ThemeBrushes.Get("TextSecondaryBrush") } } });
        Grid.SetColumn(texts, 1); row.Children.Add(texts);
        var button = new Button { Content = row, Tag = entry, Classes = { "project-entry" }, IsEnabled = _vm!.CanOpenProject };
        ToolTip.SetTip(button, entry.Path);
        button.Click += async (_, _) =>
        {
            _close?.Invoke();
            if (current || _vm is null || !_vm.CanOpenProject) return;
            try
            {
                if (entry.Repository is { } repo) await _vm.OpenProjectCommand.ExecuteAsync(repo);
                else if (_vm.OpenPlainFolderAsync is not null) await _vm.OpenPlainFolderAsync(entry.Path);
            }
            catch (Exception ex) { if (_vm.ShowMessageAsync is not null) await _vm.ShowMessageAsync(ex.Message); }
        };
        ProjectEntries.Children.Add(button);
    }
}
