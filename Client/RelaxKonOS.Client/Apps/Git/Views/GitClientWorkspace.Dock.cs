using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;

namespace RelaxKonOS.Client.Apps.Git.Views;

internal partial class GitClientWorkspace
{
    public ObservableCollection<GitToolTab> GitTabs { get; } = [];
    public GitToolTab? SelectedGitTab { get; private set; }
    private readonly DispatcherTimer _consoleTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _dockRepository;
    private bool _syncingBusy, _selectingGitTab;
    private int _consoleReading;
    private long _consoleAfter;
    private int _consoleRevision, _dockEpoch;
    private bool _gitMaximized;
    private DateTimeOffset _lastLogRefresh = DateTimeOffset.UtcNow;
    private CancellationTokenSource _dockCancellation = new();

    private void InitializeGitDock()
    {
        GitTabItems.ItemsSource = GitTabs;
        _viewModel.OpenFileHistoryTabAsync = async path => await NewLogTabAsync(path: path);
        _consoleTimer.Tick += async (_, _) =>
        {
            await RefreshGitConsoleAsync();
            if (!_dockCancellation.IsCancellationRequested && _project.IsGitVisible && !_viewModel.IsBusy && _viewModel.IsAutoRefresh
                && SelectedGitTab?.Session is { } log && !log.RefreshCommand.IsRunning && DateTimeOffset.UtcNow - _lastLogRefresh > TimeSpan.FromSeconds(10))
            { _lastLogRefresh = DateTimeOffset.UtcNow; await log.RefreshCommand.ExecuteAsync(null); }
        };
        Loaded += (_, _) =>
        {
            if (_dockCancellation.IsCancellationRequested) _dockCancellation = new();
            _consoleTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _consoleTimer.Stop(); _dockCancellation.Cancel();
            foreach (var tab in GitTabs.Where(tab => tab.Session != _viewModel)) tab.Session?.Stop();
        };
    }
    public void DisposeGitDock()
    {
        _consoleTimer.Stop(); _dockCancellation.Cancel(); ResetGitDock();
        _viewModel.OpenFileHistoryTabAsync = null;
    }
    private void ResetGitDock()
    {
        foreach (var tab in GitTabs)
            if (tab.Session is { } session && session != _viewModel) { session.PropertyChanged -= LogSessionChanged; session.CloseLogSession(); }
        _dockEpoch++;
        GitTabs.Clear(); SelectedGitTab = null; ContentHost.Content = null;
        _dockRepository = _viewModel.SelectedRepository?.Id; _consoleAfter = 0; _tool = "log";
    }
    private void UpdateGitTabTitles()
    {
        foreach (var tab in GitTabs)
        {
            tab.CanClose = !_viewModel.IsBusy;
            if (tab.View is GitConsoleView console) console.RefreshLocalization();
            if (tab.Kind == "history") tab.Title = LocalizedText.Format("git.dock.history_title", tab.Path?.Replace('\\', '/').Split('/').Last() ?? "");
            else if (tab.Kind == "log")
            {
                var reference = tab.Session?.SelectedLogBranch?.Value;
                tab.Title = string.IsNullOrEmpty(reference) ? LocalizedText.Get("git.page.log") : LocalizedText.Format("git.editor.log_tab", reference);
            }
            else tab.Title = LocalizedText.Get(tab.Kind switch { "console" => "git.dock.console", "branches" => "git.editor.branches", "remotes" => "git.page.remotes", "worktrees" => "git.workspace.worktrees", "stashes" => "git.workspace.stashes", _ => "git.page.overview" });
        }
    }
    private GitToolTab AddGitTab(string kind, Control control, GitClientViewModel? session = null, string? path = null)
    {
        var tab = new GitToolTab(kind, control, session, path);
        if (control is GitConsoleView console) console.ClearRequested += (_, _) => _consoleRevision++;
        GitTabs.Add(tab);
        if (session is not null && session != _viewModel) session.PropertyChanged += LogSessionChanged;
        UpdateGitTabTitles(); return tab;
    }
    public void SelectGitTab(GitToolTab tab)
    {
        if (!GitTabs.Contains(tab)) return;
        foreach (var item in GitTabs) item.IsSelected = ReferenceEquals(item, tab);
        SelectedGitTab = tab; _tool = tab.Kind; ContentHost.Content = tab.View;
        _selectingGitTab = true;
        try
        {
            if (tab.Kind is "log" or "history") _viewModel.ActivePage = GitClientPage.Log;
            else if (tab.Kind == "remotes") _viewModel.ActivePage = GitClientPage.Remotes;
            else if (tab.Kind == "overview") _viewModel.ActivePage = GitClientPage.Overview;
        }
        finally { _selectingGitTab = false; }
        _project.IsGitVisible = true; UpdateLayoutState();
        Dispatcher.UIThread.Post(() => GitTabItems.ContainerFromItem(tab)?.BringIntoView(), DispatcherPriority.Loaded);
    }
    public async Task<GitToolTab> NewLogTabAsync(bool duplicate = false, string? path = null)
    {
        var session = _viewModel.CreateLogSession();
        if (duplicate && SelectedGitTab?.Session is { } source) session.CopyLogFiltersFrom(source);
        if (path is not null) session.LogPathFilter = path;
        var view = new GitLogView(session);
        if (duplicate && SelectedGitTab?.View is GitLogView original) view.CopyPresentationFrom(original);
        if (path is not null) view.SetBranchesVisible(false);
        var tab = AddGitTab(path is null ? "log" : "history", view, session, path);
        SelectGitTab(tab);
        await session.RefreshCommand.ExecuteAsync(null);
        if (GitTabs.Contains(tab)) UpdateGitTabTitles();
        return tab;
    }
    public void CloseGitTab(GitToolTab tab)
    {
        if (_viewModel.IsBusy || !GitTabs.Contains(tab)) return;
        var index = GitTabs.IndexOf(tab);
        if (tab.Session is { } session && session != _viewModel) { session.PropertyChanged -= LogSessionChanged; session.CloseLogSession(); }
        if (tab.Kind == "console") _consoleRevision++;
        GitTabs.Remove(tab);
        if (SelectedGitTab == tab)
        {
            SelectedGitTab = null; ContentHost.Content = null;
            if (GitTabs.Count > 0) SelectGitTab(GitTabs[Math.Min(index, GitTabs.Count - 1)]);
        }
        UpdateLayoutState();
    }
    private void LogSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitClientViewModel.IsBusy) && sender is GitClientViewModel session && !_syncingBusy)
        {
            _syncingBusy = true;
            try { _viewModel.IsBusy = session.IsBusy; foreach (var tab in GitTabs) if (tab.Session is { } other && other != session) other.IsBusy = session.IsBusy; }
            finally { _syncingBusy = false; }
            if (!session.IsBusy)
            {
                _ = _viewModel.RefreshCommand.ExecuteAsync(null);
                foreach (var tab in GitTabs.Where(tab => tab.Session is not null && tab.Session != session && tab.Session != _viewModel).ToArray()) _ = tab.Session!.RefreshCommand.ExecuteAsync(null);
            }
        }
        if (e.PropertyName is nameof(GitClientViewModel.SelectedLogBranch) or nameof(GitClientViewModel.IsBusy)) UpdateGitTabTitles();
    }
    private void SyncGitDock(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitClientViewModel.SelectedRepository) && _dockRepository != _viewModel.SelectedRepository?.Id) ResetGitDock();
        if (e.PropertyName == nameof(GitClientViewModel.IsBusy) && !_syncingBusy)
        {
            _syncingBusy = true;
            try { foreach (var tab in GitTabs) if (tab.Session is { } session && session != _viewModel) session.IsBusy = _viewModel.IsBusy; }
            finally { _syncingBusy = false; }
            if (!_viewModel.IsBusy) { _ = RefreshGitConsoleAsync(); foreach (var tab in GitTabs.Where(tab => tab.Session is not null && tab.Session != _viewModel).ToArray()) _ = tab.Session!.RefreshCommand.ExecuteAsync(null); }
        }
        UpdateGitTabTitles();
    }
    public async Task RefreshGitConsoleAsync()
    {
        if (_viewModel.SelectedRepository is not { } repo || Interlocked.CompareExchange(ref _consoleReading, 1, 0) != 0) return;
        var revision = _consoleRevision; var epoch = _dockEpoch;
        try
        {
            var entries = await _viewModel.ReadConsoleAsync(_consoleAfter, _dockCancellation.Token);
            if (_viewModel.SelectedRepository?.Id != repo.Id || _dockCancellation.IsCancellationRequested || epoch != _dockEpoch) return;
            (GitTabs.FirstOrDefault(tab => tab.Kind == "console")?.View as GitConsoleView)?.ShowUnavailable(false);
            if (entries.Count == 0) return;
            _consoleAfter = Math.Max(_consoleAfter, entries.Max(entry => entry.Sequence));
            if (revision != _consoleRevision) return;
            var tab = GitTabs.FirstOrDefault(tab => tab.Kind == "console");
            if (tab is null) tab = AddGitTab("console", new GitConsoleView());
            ((GitConsoleView)tab.View).Append(entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_viewModel.SelectedRepository?.Id == repo.Id) (GitTabs.FirstOrDefault(tab => tab.Kind == "console")?.View as GitConsoleView)?.ShowUnavailable(true); }
        finally { Interlocked.Exchange(ref _consoleReading, 0); }
    }
    private void SelectGitTab_Click(object? sender, RoutedEventArgs e) { if (sender is Control { Tag: GitToolTab tab }) SelectGitTab(tab); }
    private void CloseGitTab_Click(object? sender, RoutedEventArgs e) { if (sender is Control { Tag: GitToolTab tab }) CloseGitTab(tab); e.Handled = true; }
    private async void NewGitLog_Click(object? sender, RoutedEventArgs e) => await NewLogTabAsync();
    private async void DuplicateGitLog_Click(object? sender, RoutedEventArgs e) => await NewLogTabAsync(duplicate: true);
    private void ShowGitConsole_Click(object? sender, RoutedEventArgs e)
    {
        var tab = GitTabs.FirstOrDefault(tab => tab.Kind == "console") ?? AddGitTab("console", new GitConsoleView());
        SelectGitTab(tab); _ = RefreshGitConsoleAsync();
    }
    private void GitTabList_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        var menu = new MenuFlyout();
        foreach (var tab in GitTabs)
        {
            var item = new MenuItem { Header = tab.Title }; item.Click += (_, _) => SelectGitTab(tab); menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }
    private void MaximizeGit_Click(object? sender, RoutedEventArgs e) { _gitMaximized = !_gitMaximized; UpdateLayoutState(); }
}
