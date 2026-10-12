using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using System.Runtime.CompilerServices;
using RelaxKonOS.Client.Services;
using System.Collections.ObjectModel;
using RelaxKonOS.Client.Apps.Explorer.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;
using Renci.SshNet;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal partial class SshFileBrowserView : UserControl
{
    public static readonly StyledProperty<string> SearchTextProperty =
        AvaloniaProperty.Register<SshFileBrowserView, string>(nameof(SearchText), string.Empty);
    public static readonly StyledProperty<double> TabContentWidthProperty =
        AvaloniaProperty.Register<SshFileBrowserView, double>(nameof(TabContentWidth), MaximumTabWidth);
    public double TabContentWidth { get => GetValue(TabContentWidthProperty); private set => SetValue(TabContentWidthProperty, value); }
    public static readonly StyledProperty<double> EntriesTableWidthProperty =
        AvaloniaProperty.Register<SshFileBrowserView, double>(nameof(EntriesTableWidth));
    public static readonly StyledProperty<string> NameSortGlyphProperty =
        AvaloniaProperty.Register<SshFileBrowserView, string>(nameof(NameSortGlyph));
    public static readonly StyledProperty<string> ModifiedSortGlyphProperty =
        AvaloniaProperty.Register<SshFileBrowserView, string>(nameof(ModifiedSortGlyph));
    public static readonly StyledProperty<string> TypeSortGlyphProperty =
        AvaloniaProperty.Register<SshFileBrowserView, string>(nameof(TypeSortGlyph));
    public static readonly StyledProperty<string> SizeSortGlyphProperty =
        AvaloniaProperty.Register<SshFileBrowserView, string>(nameof(SizeSortGlyph));

    private const double MaximumTabWidth = 200;
    private const double MinimumTabWidth = 72;
    private const double TabItemChrome = 18;
    private const double TabRowChrome = 34;
    private const double TabMinimumPitch = MinimumTabWidth + TabItemChrome;
    private sealed record SshDragPayload(SshFileBrowserView Source, SshFileEntry[] Entries);
    private static readonly DataFormat<SshDragPayload> SshEntriesFormat =
        DataFormat.CreateInProcessFormat<SshDragPayload>("relaxkonos/ssh-file-entries");
    private PointerPressedEventArgs? _dragTrigger;
    private SshFileEntry? _dragEntry;
    private SshFileEntry[]? _preservedDragSelection;
    private bool _toggleSelectionOnRelease;
    private Point _dragStart;
    private readonly FileBrowserPresentation _presentation;
    private readonly SshDesktopSession _session;
    private readonly LocalizationService _localization;
    private readonly Func<string, bool> _canOpenFile;
    private readonly Func<string, bool> _openFile;
    private readonly Action<string>? _selectPackage;
    private readonly Action? _cancelPicker;
    private readonly List<string> _history = [];
    // Sharing is scoped to one live SSH session; different connections never exchange paths.
    private sealed class SessionClipboard
    {
        public List<SshFileEntry> Entries { get; } = [];
        public bool Cut { get; set; }
        public int Revision { get; set; }
        public ServerCenterSshEndpoint? Endpoint { get; set; }
        public event Action? Changed;
        public event Action<string[]>? Moved;
        public void NotifyMoved(string[] paths)
        {
            if (paths.Length == 0) return;
            var moved = paths.ToHashSet(StringComparer.Ordinal);
            if (Cut && Entries.RemoveAll(entry => moved.Contains(entry.Path)) > 0)
            {
                Revision++;
                Notify();
            }
            Moved?.Invoke(paths);
        }
        public void Notify() => Changed?.Invoke();
    }
    private static readonly ConditionalWeakTable<SshDesktopSession, SessionClipboard> Clipboards = new();
    private readonly SessionClipboard _sessionClipboard;
    private List<SshFileEntry> _clipboard
    {
        get
        {
            if (!ReferenceEquals(_sessionClipboard.Endpoint, _session.Endpoint))
            {
                _sessionClipboard.Endpoint = _session.Endpoint;
                _sessionClipboard.Entries.Clear();
                _sessionClipboard.Cut = false;
                _sessionClipboard.Revision++;
                _sessionClipboard.Notify();
            }
            return _sessionClipboard.Entries;
        }
    }
    private readonly ObservableCollection<TreeNodeModel> _navigationNodes = [];
    private readonly Dictionary<TreeNodeModel, Task> _treeLoads = [];
    private SshFileEntry[] _entries = [];
    private int _historyIndex = -1;
    private bool _showHidden;
    private bool _cutMode { get => _sessionClipboard.Cut; set => _sessionClipboard.Cut = value; }
    private SshFileEntry? _editingEntry;
    private bool _renameCommitInProgress;
    private LocalizedStatus _statusMessage;
    private LocalizedStatus StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; StatusText.Text = value.Resolve(); }
    }
    private int _sortIndex;
    private bool _descending;
    private string _path = ".";
    private bool _busy;
    private bool _initialized;
    private bool _viewReady;
    private bool _syncingTreeSelection;
    private int _treeRevision;
    private string? _homePath;
    private TreeNodeModel? _homeNode;
    private TreeNodeModel? _rootNode;
    private DataGridColumnHeader? _pressedSortHeader;
    private double _pressedSortHeaderWidth;
    private readonly Flyout _searchFlyout;
    private bool _compactSearch;
    private readonly ObservableCollection<SshFileBrowserTab> _tabs = [];
    private SshFileBrowserTab _activeTab = new();
    private bool _syncingTabs;
    private int _treeSelectionRevision;
    public Action? CloseAction { get; set; }
    internal IReadOnlyList<SshFileBrowserTab> Tabs => _tabs;
    internal SshFileBrowserTab ActiveTab => _activeTab;

    public string SearchText { get => GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }

    /// <summary>Combined live width of the details columns, used to constrain row layout.</summary>
    public double EntriesTableWidth
    {
        get => GetValue(EntriesTableWidthProperty);
        private set => SetValue(EntriesTableWidthProperty, value);
    }

    public string NameSortGlyph { get => GetValue(NameSortGlyphProperty); private set => SetValue(NameSortGlyphProperty, value); }
    public string ModifiedSortGlyph { get => GetValue(ModifiedSortGlyphProperty); private set => SetValue(ModifiedSortGlyphProperty, value); }
    public string TypeSortGlyph { get => GetValue(TypeSortGlyphProperty); private set => SetValue(TypeSortGlyphProperty, value); }
    public string SizeSortGlyph { get => GetValue(SizeSortGlyphProperty); private set => SetValue(SizeSortGlyphProperty, value); }

    public SshFileBrowserView(SshDesktopSession session, Func<string, bool>? canOpenFile = null,
        Func<string, bool>? openFile = null, Action<string>? selectPackage = null, Action? cancelPicker = null)
    {
        _session = session;
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _sessionClipboard = Clipboards.GetOrCreateValue(session);
        _canOpenFile = canOpenFile ?? (_ => false);
        _openFile = openFile ?? (_ => false);
        _selectPackage = selectPackage;
        _cancelPicker = cancelPicker;
        InitializeComponent();
        HeaderTabs.IsVisible = !IsPackagePicker;
        if (!IsPackagePicker) _tabs.Add(_activeTab);
        FolderTabs.ItemsSource = FolderPicker.ItemsSource = _tabs;
        FolderTabs.SelectedItem = _activeTab;
        TabScroller.SizeChanged += (_, e) => UpdateTabLayout(e.NewSize.Width);
        TabScroller.ScrollChanged += (_, _) => UpdateTabScrollMarks();
        TabScroller.AddHandler(PointerWheelChangedEvent, OnTabStripWheel, RoutingStrategies.Tunnel);
        _tabs.CollectionChanged += (_, _) => UpdateTabLayout(TabScroller.Bounds.Width);
        FolderTabs.AddHandler(PointerPressedEvent, Tab_PointerPressed, RoutingStrategies.Tunnel);
        _searchFlyout = (Flyout)CompactSearchButton.Flyout!;
        _searchFlyout.Opened += (_, _) => { CompactSearchBox.Focus(); CompactSearchBox.SelectAll(); };
        FileActions.IsVisible = !IsPackagePicker;
        BreadcrumbScroll.SizeChanged += (_, _) => ScrollBreadcrumbsToLocation();
        SizeChanged += (_, e) =>
        {
            FileContentGrid.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 620 ? 0 : 210);
            FileContentGrid.ColumnDefinitions[1].Width = new GridLength(e.NewSize.Width < 620 ? 0 : 4);
            StatusText.MaxWidth = e.NewSize.Width * 0.55;
        };
        PickerPanel.IsVisible = IsPackagePicker;
        PickerFilterText.Text = IsPackagePicker
            ? T("server_center.wizard.bundle_file_type", "RelaxKonOS release bundle") + " (*.zip)"
            : string.Empty;
        EntriesGrid.SelectionMode = IsPackagePicker ? DataGridSelectionMode.Single : DataGridSelectionMode.Extended;
        _presentation = new FileBrowserPresentation(EntriesHost, EntriesGrid, () => !_busy);
        _presentation.Items.AddHandler(PointerPressedEvent, EntriesGrid_PointerPressed, RoutingStrategies.Tunnel);
        _presentation.Items.DoubleTapped += EntriesGrid_DoubleTapped;
        foreach (var surface in new Control[] { EntriesGrid, _presentation.Items })
        {
            surface.PointerMoved += EntriesGrid_PointerMoved;
            surface.AddHandler(PointerReleasedEvent, EntriesGrid_PointerReleased, RoutingStrategies.Tunnel);
            surface.PointerCaptureLost += (_, _) => ClearPendingDrag();
            DragDrop.SetAllowDrop(surface, true);
        }
        DragDrop.SetAllowDrop(BrowserRoot, true);
        BrowserRoot.AddHandler(DragDrop.DragOverEvent, Files_DragOver);
        BrowserRoot.AddHandler(DragDrop.DropEvent, Files_Drop);
        NavigationTree.ItemsSource = _navigationNodes;
        _viewReady = true;
        // The grid marks pointer presses as handled while it updates the selection, so the
        // right-click handler has to run in the tunnel phase (same as ExplorerMainView).
        EntriesGrid.AddHandler(PointerPressedEvent, EntriesGrid_PointerPressed, RoutingStrategies.Tunnel);
        EntriesGrid.AddHandler(PointerPressedEvent, EntriesGrid_SortHeaderPointerPressed, RoutingStrategies.Tunnel);
        EntriesGrid.AddHandler(PointerReleasedEvent, EntriesGrid_SortHeaderPointerReleased, RoutingStrategies.Tunnel);
        EntriesGrid.LayoutUpdated += EntriesGrid_LayoutUpdated;
        // The details grid consumes a few keys itself (F2 starts an edit, Ctrl+C copies cell text).
        // Registering with handledEventsToo keeps the browser shortcuts — F5, F2, Delete,
        // Alt+arrows, Ctrl+C/X/V/A/F and Escape — working while the list has focus.
        AddHandler(KeyDownEvent, View_KeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        UpdateSortGlyphs();
        AttachedToVisualTree += (_, _) =>
        {
            _localization.LanguageChanged += LanguageChanged;
            _sessionClipboard.Changed += ClipboardChanged;
            _sessionClipboard.Moved += FilesMoved;
            ClipboardChanged();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _localization.LanguageChanged -= LanguageChanged;
            _sessionClipboard.Changed -= ClipboardChanged;
            _sessionClipboard.Moved -= FilesMoved;
            CancelRename(); ClearPendingDrag();
        };
        AttachedToVisualTree += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await NavigateAsync(".");
        };
        UpdateControls();
    }

    private void LanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        foreach (var entry in _entries.Concat(_tabs.SelectMany(tab => tab.Entries)).Distinct()) entry.RefreshLocalizedText();
        foreach (var tab in _tabs) tab.RefreshLocalizedText();
        if (_homeNode is not null) _homeNode.Label = T("ssh_files.home", "Home");
        if (_rootNode is not null) _rootNode.Label = T("ssh_files.root", "Root");
        BuildBreadcrumbs();
        StatusText.Text = StatusMessage.Resolve();
        if (IsPackagePicker) PickerFilterText.Text = T("server_center.wizard.bundle_file_type", "RelaxKonOS release bundle") + " (*.zip)";
        if (_editingEntry is { } editing && !ValidName(editing.RenameDraft))
            StatusMessage = LocalizedStatus.Key("explorer.status.file_name_invalid", "Invalid file name");
        UpdateControls();
    }

    public void AttachWindowHeader(ManagedWindow window)
    {
        if (IsPackagePicker || ReferenceEquals(window.View.HeaderTabs, HeaderTabs)) return;
        BrowserRoot.Children.Remove(NavigationToolbar);
        BrowserRoot.Children.Remove(HeaderTabs);
        window.View.HeaderTabs = HeaderTabs;
        window.View.HeaderToolbar = NavigationToolbar;
        NavigationToolbar.AddHandler(KeyDownEvent, View_KeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        HeaderTabs.AddHandler(KeyDownEvent, View_KeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void UpdateTabLayout(double available)
    {
        // The view model's tab list is the authority: a ListBox applies items a beat behind a
        // collection change, and dividing the row by a stale count would leave the last tab short.
        var count = _tabs.Count;
        // The share is floored: item measurement rounds every tab up to whole pixels, so a share
        // taken from an unrounded division would push the last tab past the edge of the row.
        TabContentWidth = count > 0 && available > 0
            ? Math.Clamp(Math.Floor(Math.Max(0, available - TabRowChrome) / count) - TabItemChrome, MinimumTabWidth, MaximumTabWidth)
            : MaximumTabWidth;

        // Decide against the width without arrows so showing them cannot latch overflow on.
        var unadornedWidth = available + (TabScrollLeft.IsVisible
            ? TabScrollLeft.Bounds.Width + TabScrollRight.Bounds.Width + 4 : 0);
        var filled = count > 0 && available > 0 && count * TabMinimumPitch + TabRowChrome > unadornedWidth + 0.5;
        if (TabScrollLeft.IsVisible != filled)
        {
            TabScrollLeft.IsVisible = TabScrollRight.IsVisible = filled;
            if (!filled) TabScroller.Offset = TabScroller.Offset.WithX(0);
        }
        UpdateTabScrollMarks();
        Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
    }

    /// <summary>Keeps the tab the user is on inside the strip's window, however it was chosen.</summary>
    private void ScrollActiveTabIntoView()
    {
        if (FolderTabs.SelectedItem is not { } tab || FolderTabs.ContainerFromItem(tab) is not Control container) return;
        var extent = TabScroller.Extent.Width;
        var viewport = TabScroller.Viewport.Width;
        if (extent <= viewport || container.TranslatePoint(default, TabScroller) is not { } origin) return;
        var offset = TabScroller.Offset.X;
        if (ReferenceEquals(tab, _tabs.LastOrDefault())) offset = extent - viewport;
        else if (origin.X < 0) offset += origin.X;
        else if (origin.X + container.Bounds.Width > viewport) offset += origin.X + container.Bounds.Width - viewport;
        TabScroller.Offset = TabScroller.Offset.WithX(Math.Clamp(offset, 0, extent - viewport));
        UpdateTabScrollMarks();
    }

    private void TabScrollLeft_Click(object? sender, RoutedEventArgs e) => ScrollTabsBy(-1);
    private void TabScrollRight_Click(object? sender, RoutedEventArgs e) => ScrollTabsBy(1);

    /// <summary>Scrolls the strip by whole tabs; reports whether it actually moved.</summary>
    internal bool ScrollTabsBy(int direction)
    {
        var range = TabScroller.Extent.Width - TabScroller.Viewport.Width;
        if (range <= 0) return false;
        var next = Math.Clamp(TabScroller.Offset.X + direction * (TabContentWidth + TabItemChrome), 0, range);
        if (next == TabScroller.Offset.X) return false;
        TabScroller.Offset = TabScroller.Offset.WithX(next);
        UpdateTabScrollMarks();
        return true;
    }

    private void OnTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ScrollTabsBy(e.Delta.Y > 0 ? -1 : 1)) e.Handled = true;
    }

    /// <summary>The marks stay on the row and go quiet at the end they have already reached.</summary>
    private void UpdateTabScrollMarks()
    {
        var range = TabScroller.Extent.Width - TabScroller.Viewport.Width;
        TabScrollLeft.IsEnabled = range > 0.5 && TabScroller.Offset.X > 0.5;
        TabScrollRight.IsEnabled = range > 0.5 && TabScroller.Offset.X < range - 0.5;
    }

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_busy && !IsPackagePicker && e.GetCurrentPoint(FolderTabs).Properties.IsMiddleButtonPressed
            && FindDataContext<SshFileBrowserTab>(e.Source) is { } tab)
        { CloseTab(tab); e.Handled = true; }
    }

    private void CaptureActiveTab()
    {
        _activeTab.Path = _path;
        _activeTab.Entries = _entries;
        _activeTab.History.Clear(); _activeTab.History.AddRange(_history);
        _activeTab.HistoryIndex = _historyIndex;
        _activeTab.SearchText = SearchText;
        _activeTab.SortIndex = _sortIndex;
        _activeTab.Descending = _descending;
        _activeTab.ShowHidden = _showHidden;
        _activeTab.ViewModeIndex = ViewModeBox.SelectedIndex;
    }

    internal void NewTab()
    {
        if (_busy || IsPackagePicker) return;
        CaptureActiveTab();
        var tab = new SshFileBrowserTab { Path = _path, Entries = _entries,
            SortIndex = _sortIndex, Descending = _descending, ShowHidden = _showHidden,
            ViewModeIndex = ViewModeBox.SelectedIndex, HistoryIndex = 0 };
        tab.History.Add(_path);
        _tabs.Add(tab);
        SwitchTab(tab);
    }

    internal void SwitchTab(SshFileBrowserTab tab)
    {
        if (_busy || IsPackagePicker || ReferenceEquals(tab, _activeTab) || !_tabs.Contains(tab)) return;
        CancelRename();
        CaptureActiveTab();
        _activeTab = tab;
        var hiddenChanged = _showHidden != tab.ShowHidden;
        _syncingTabs = true;
        var ready = _viewReady;
        _viewReady = false;
        try
        {
            _presentation.ClearSelection();
            _path = tab.Path; _entries = tab.Entries;
            _history.Clear(); _history.AddRange(tab.History); _historyIndex = tab.HistoryIndex;
            SearchText = tab.SearchText;
            _sortIndex = tab.SortIndex; _descending = tab.Descending; _showHidden = tab.ShowHidden;
            SortBox.SelectedIndex = _sortIndex; DescendingBox.IsChecked = _descending; HiddenBox.IsChecked = _showHidden;
            ViewModeBox.SelectedIndex = tab.ViewModeIndex;
            SetAddressEditing(false); AddressBox.Text = _path;
            _searchFlyout.Hide();
            BuildBreadcrumbs(); UpdateSortGlyphs();
            FolderTabs.SelectedItem = FolderPicker.SelectedItem = tab;
        }
        finally { _viewReady = ready; _syncingTabs = false; }
        ApplyView(); StatusMessage = "";
        Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
        _ = hiddenChanged ? RebuildNavigationTreeAsync() : SyncNavigationTreeAsync();
    }

    internal void CloseTab(SshFileBrowserTab tab)
    {
        if (_busy || IsPackagePicker || !_tabs.Contains(tab)) return;
        if (_tabs.Count == 1) { CloseAction?.Invoke(); return; }
        if (ReferenceEquals(tab, _activeTab))
        {
            var index = _tabs.IndexOf(tab);
            SwitchTab(_tabs[index == _tabs.Count - 1 ? index - 1 : index + 1]);
        }
        _tabs.Remove(tab);
    }

    private void NewTab_Click(object? sender, RoutedEventArgs e) => NewTab();
    private void CloseTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SshFileBrowserTab tab }) CloseTab(tab);
    }
    private void FolderTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabs || !_viewReady) return;
        if (FolderTabs.SelectedItem is SshFileBrowserTab tab) SwitchTab(tab);
        FolderTabs.SelectedItem = _activeTab;
    }
    private void FolderPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabs || !_viewReady) return;
        if (FolderPicker.SelectedItem is SshFileBrowserTab tab) SwitchTab(tab);
        FolderPicker.SelectedItem = _activeTab;
        FolderPickerButton.Flyout?.Hide();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SearchTextProperty && _viewReady) ApplyView();
    }

    private void NavigationToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpButton.IsVisible = RefreshButton.IsVisible = e.NewSize.Width >= 520;
        var compact = e.NewSize.Width < 600;
        if (compact != _compactSearch) _searchFlyout.Hide();
        _compactSearch = compact;
        CompactSearchButton.IsVisible = compact;
        InlineSearchHost.IsVisible = !compact;
        SearchBox.Width = Math.Clamp(e.NewSize.Width * 0.24, 140, 260);
    }

    private void CommandToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        RenameButton.IsVisible = DeleteButton.IsVisible = width >= 760;
        CutButton.IsVisible = CopyButton.IsVisible = PasteButton.IsVisible = width >= 620;
        EditingDivider.IsVisible = width >= 620;
        ViewDivider.IsVisible = !IsPackagePicker && width >= 480;
        NewFolderLabel.IsVisible = SortLabel.IsVisible = ViewLabel.IsVisible = width >= 480;
        SortLabelChevron.IsVisible = ViewLabelChevron.IsVisible = width >= 480;
    }

    public void FocusSearchBox()
    {
        if (_compactSearch) _searchFlyout.ShowAt(CompactSearchButton);
        var search = _compactSearch ? CompactSearchBox : SearchBox;
        search.Focus(); search.SelectAll();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchText = string.Empty;
        _searchFlyout.Hide();
        _presentation.Focus();
        e.Handled = true;
    }

    private void ScrollBreadcrumbsToLocation() => Dispatcher.UIThread.Post(() =>
        BreadcrumbScroll.Offset = new Vector(Math.Max(0, BreadcrumbScroll.Extent.Width - BreadcrumbScroll.Viewport.Width), 0),
        DispatcherPriority.Loaded);

    private void ViewModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        CancelRename();
        if (_presentation is not null && ViewModeBox.SelectedIndex >= 0)
            _presentation.SetMode((ExplorerViewMode)ViewModeBox.SelectedIndex);
    }

    private void EntriesGrid_LayoutUpdated(object? sender, EventArgs e)
    {
        var width = EntriesGrid.Columns.Sum(column => column.ActualWidth);
        if (width <= 0 || Math.Abs(width - EntriesTableWidth) < 0.1) return;
        EntriesTableWidth = width;
    }

    private async void Back_Click(object? sender, RoutedEventArgs e) => await BackAsync();
    private async void Forward_Click(object? sender, RoutedEventArgs e) => await ForwardAsync();
    private async void Up_Click(object? sender, RoutedEventArgs e) => await NavigateAsync(ParentPath(_path));
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void NewFolder_Click(object? sender, RoutedEventArgs e) => await CreateDirectoryAsync();
    private void Copy_Click(object? sender, RoutedEventArgs e) => CopySelection(false);
    private void Cut_Click(object? sender, RoutedEventArgs e) => CopySelection(true);
    private async void Paste_Click(object? sender, RoutedEventArgs e) => await PasteAsync();
    private async void Rename_Click(object? sender, RoutedEventArgs e) => await RenameAsync();
    private async void Delete_Click(object? sender, RoutedEventArgs e) => await DeleteAsync();
    private async void Upload_Click(object? sender, RoutedEventArgs e) => await UploadAsync();
    private async void UploadFolder_Click(object? sender, RoutedEventArgs e) => await UploadFolderAsync();
    private async void Download_Click(object? sender, RoutedEventArgs e) => await DownloadAsync();
    private async void Properties_Click(object? sender, RoutedEventArgs e) => await ShowPropertiesAsync();

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is SshFileEntry entry)
            await OpenEntryAsync(entry);
    }

    private bool IsPackagePicker => _selectPackage is not null;

    private void ConfirmPicker_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanConfirmPackage())
        {
            StatusMessage = LocalizedStatus.Key("server_center.wizard.bundle_required", "Choose a signed .zip release bundle to continue.");
            return;
        }
        _selectPackage!(Selection()[0].Path);
    }

    private void CancelPicker_Click(object? sender, RoutedEventArgs e) => _cancelPicker?.Invoke();

    private async void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; SetAddressEditing(false); _presentation.Focus(); return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var requestedPath = AddressBox.Text ?? ".";
        SetAddressEditing(false);
        await NavigateAsync(requestedPath);
    }

    private void EditAddress_Click(object? sender, RoutedEventArgs e) => SetAddressEditing(true);

    private void AddressBox_LostFocus(object? sender, RoutedEventArgs e) => SetAddressEditing(false);

    private void SetAddressEditing(bool editing)
    {
        AddressBox.IsVisible = editing;
        AddressDisplay.IsVisible = !editing;
        if (!editing) return;
        AddressBox.Text = _path;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private async void Breadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) await NavigateAsync(path);
    }

    private void SortBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady) return;
        _sortIndex = Math.Max(0, SortBox.SelectedIndex);
        UpdateSortGlyphs();
        ApplyView();
    }

    private void DescendingBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_viewReady) return;
        _descending = DescendingBox.IsChecked == true;
        UpdateSortGlyphs();
        ApplyView();
    }

    private void HiddenBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_viewReady) return;
        _showHidden = HiddenBox.IsChecked == true;
        ApplyView();
        _ = RebuildNavigationTreeAsync();
    }

    private async void NavigationTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingTreeSelection || _busy || NavigationTree.SelectedItem is not TreeNodeModel { Path: { } path } node
            || node.IsPlaceholder) return;
        await NavigateAsync(path);
    }

    private void EntriesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewReady) UpdateControls();
    }

    private void EntriesGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearPendingDrag();
        if (FindVisualAncestor<TextBox>(e.Source) is not null) return;
        var point = e.GetCurrentPoint(EntriesGrid);
        var entry = FindDataContext<SshFileEntry>(e.Source);
        if (point.Properties.IsRightButtonPressed)
        {
            if (entry is null) _presentation.ClearSelection();
            else if (!Selection().Contains(entry)) EntriesGrid.SelectedItem = entry;
            return;
        }
        if (_busy || IsPackagePicker || !point.Properties.IsLeftButtonPressed || entry is null) return;
        _dragTrigger = e; _dragEntry = entry; _dragStart = e.GetPosition(this);
        if (Selection().Contains(entry) && Selection().Length > 1
            && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Control)
        {
            _preservedDragSelection = Selection();
            _toggleSelectionOnRelease = e.KeyModifiers == KeyModifiers.Control;
            _presentation.Focus(); e.Handled = true;
        }
    }

    private void ClearPendingDrag()
    {
        _dragTrigger = null; _dragEntry = null; _preservedDragSelection = null;
        _toggleSelectionOnRelease = false;
    }

    private async void EntriesGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTrigger is null || _dragEntry is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { ClearPendingDrag(); return; }
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < 5 && Math.Abs(point.Y - _dragStart.Y) < 5) return;
        var trigger = _dragTrigger;
        var entries = _preservedDragSelection ?? (Selection().Contains(_dragEntry) ? Selection() : [_dragEntry]);
        ClearPendingDrag();
        if (_busy || IsPackagePicker) return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(SshEntriesFormat, new SshDragPayload(this, entries)));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void EntriesGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_preservedDragSelection is not null && _dragEntry is { } entry)
        {
            if (_toggleSelectionOnRelease) EntriesGrid.SelectedItems.Remove(entry);
            else { _presentation.ClearSelection(); EntriesGrid.SelectedItem = entry; }
        }
        ClearPendingDrag();
    }

    private string? DropTarget(object? source)
    {
        for (var control = source as Control; control is not null; control = control.GetVisualParent() as Control)
        {
            if (control.DataContext is SshFileEntry entry) return entry.IsDirectory && !entry.IsLink ? entry.Path : null;
            if (control.DataContext is TreeNodeModel { IsPlaceholder: false, Path: { } path }) return path;
            if (ReferenceEquals(control, EntriesGrid) || ReferenceEquals(control, _presentation.Items)
                || ReferenceEquals(control, EntriesHost)) return _path;
        }
        return null;
    }

    internal bool CanTransferEntries(SshFileBrowserView source, IReadOnlyList<SshFileEntry> entries, string? target, bool copy)
        => !_busy && !IsPackagePicker && !source._busy && !source.IsPackagePicker
            && ReferenceEquals(source._session, _session) && !string.IsNullOrEmpty(target)
            && entries.Count > 0 && entries.All(entry => (!copy || !entry.IsLink)
                && (!entry.IsDirectory || !IsWithin(entry.Path, target))
                && (copy || ParentPath(entry.Path) != target));

    private bool TryGetDrop(DragEventArgs e, out SshDragPayload? payload, out string? target)
    {
        payload = e.DataTransfer.TryGetValue(SshEntriesFormat);
        target = DropTarget(e.Source);
        return !e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && !(e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            && payload is not null && CanTransferEntries(payload.Source, payload.Entries, target,
                e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    private void Files_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetDrop(e, out _, out _)
            ? (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy : DragDropEffects.Move)
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Files_Drop(object? sender, DragEventArgs e)
    {
        var valid = TryGetDrop(e, out var payload, out var target);
        var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        e.DragEffects = valid ? (copy ? DragDropEffects.Copy : DragDropEffects.Move) : DragDropEffects.None;
        e.Handled = true;
        if (!valid || payload is null || target is null) return;
        var moved = await TransferEntriesAsync(payload.Entries, target, copy);
        if (!copy && moved.Length > 0 && !ReferenceEquals(payload.Source, this) && !payload.Source._busy)
            await payload.Source.RefreshAsync();
    }

    private void EntriesGrid_SortHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedSortHeader = null;
        if (!e.GetCurrentPoint(EntriesGrid).Properties.IsLeftButtonPressed) return;
        var header = FindVisualAncestor<DataGridColumnHeader>(e.Source);
        if (header is not null && SortIndexForHeader(header).HasValue)
        {
            _pressedSortHeader = header;
            _pressedSortHeaderWidth = header.Bounds.Width;
        }
    }

    private void EntriesGrid_SortHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressedHeader = _pressedSortHeader;
        _pressedSortHeader = null;
        if (_busy || pressedHeader is null || !ReferenceEquals(pressedHeader, FindVisualAncestor<DataGridColumnHeader>(e.Source))) return;
        // Resizing a column must not also switch its sort order.
        if (Math.Abs(pressedHeader.Bounds.Width - _pressedSortHeaderWidth) >= 0.1) return;
        if (SortIndexForHeader(pressedHeader) is not { } sortIndex) return;

        _descending = _sortIndex == sortIndex ? !_descending : false;
        _sortIndex = sortIndex;
        SortBox.SelectedIndex = sortIndex;
        DescendingBox.IsChecked = _descending;
        UpdateSortGlyphs();
        ApplyView();
    }

    private async void EntriesGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Activate the row under the pointer rather than the previous selection.
        var entry = FindDataContext<SshFileEntry>(e.Source);
        if (FindVisualAncestor<TextBox>(e.Source) is null && entry is not null) await OpenEntryAsync(entry);
    }

    private static T? FindDataContext<T>(object? source) where T : class
    {
        for (var control = source as Control; control is not null; control = control.GetVisualParent() as Control)
            if (control.DataContext is T value)
                return value;
        return null;
    }

    private static T? FindVisualAncestor<T>(object? source) where T : Control
    {
        for (var control = source as Control; control is not null; control = control.GetVisualParent() as Control)
            if (control is T value)
                return value;
        return null;
    }

    private int? SortIndexForHeader(DataGridColumnHeader header)
    {
        return ((header.Content as Control)?.Tag as string) switch
        {
            "name" => 0,
            "modified" => 1,
            "type" => 2,
            "size" => 3,
            _ => null
        };
    }

    private void UpdateSortGlyphs()
    {
        var glyph = _descending ? "↓" : "↑";
        NameSortGlyph = _sortIndex == 0 ? glyph : string.Empty;
        ModifiedSortGlyph = _sortIndex == 1 ? glyph : string.Empty;
        TypeSortGlyph = _sortIndex == 2 ? glyph : string.Empty;
        SizeSortGlyph = _sortIndex == 3 ? glyph : string.Empty;
    }

    private void FileMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selected = Selection();
        OpenMenuItem.IsEnabled = !_busy && selected.Length == 1
            && (selected[0].IsDirectory || _canOpenFile(selected[0].Path));
        CopyMenuItem.IsEnabled = CutMenuItem.IsEnabled = DeleteMenuItem.IsEnabled = !IsPackagePicker && !_busy && selected.Length > 0;
        PasteMenuItem.IsEnabled = !IsPackagePicker && !_busy && _clipboard.Count > 0;
        RenameMenuItem.IsEnabled = PropertiesMenuItem.IsEnabled = !IsPackagePicker && !_busy && selected.Length == 1;
        DownloadMenuItem.IsEnabled = !IsPackagePicker && !_busy && selected.Length > 0;
        NewFolderMenuItem.IsEnabled = UploadMenuItem.IsEnabled = !IsPackagePicker && !_busy;
        RefreshMenuItem.IsEnabled = !_busy;
    }

    private async void View_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && !IsPackagePicker && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.T) { e.Handled = true; NewTab(); return; }
            if (e.Key == Key.W) { e.Handled = true; CloseTab(_activeTab); return; }
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                var direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                SwitchTab(_tabs[(_tabs.IndexOf(_activeTab) + direction + _tabs.Count) % _tabs.Count]);
                return;
            }
        }
        if (e.Handled && e.Key == Key.Escape) return;
        if (FindVisualAncestor<TextBox>(e.Source) is not null && e.Key is not (Key.F5 or Key.L)) return;
        if (_busy) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Selection() is [var entry])
        { e.Handled = true; await OpenEntryAsync(entry); }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt)
        { e.Handled = true; await ShowPropertiesAsync(); }
        else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; await BackAsync(); }
        else if (e.Key == Key.F5) { e.Handled = true; await RefreshAsync(); }
        else if (e.Key == Key.F2) { e.Handled = true; await RenameAsync(); }
        else if (e.Key == Key.Delete) { e.Handled = true; await DeleteAsync(); }
        else if (e.Key == Key.Up && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        { e.Handled = true; await NavigateAsync(ParentPath(_path)); }
        else if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { e.Handled = true; await BackAsync(); }
        else if (e.Key == Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { e.Handled = true; await ForwardAsync(); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C) { e.Handled = true; CopySelection(false); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.X) { e.Handled = true; CopySelection(true); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V) { e.Handled = true; await PasteAsync(); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
        {
            e.Handled = true;
            if (!IsPackagePicker) EntriesGrid.SelectAll();
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F) { e.Handled = true; FocusSearchBox(); }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.L) { e.Handled = true; SetAddressEditing(true); }
        else if (e.Key == Key.Escape) { e.Handled = true; _presentation.ClearSelection(); }
    }

    private SftpClient OpenClient()
    {
        var endpoint = _session.Endpoint ?? throw new InvalidOperationException("SSH session ended.");
        var fingerprint = _session.HostKeyFingerprint ?? throw new InvalidOperationException("SSH host key is unavailable.");
        var client = new SftpClient(endpoint.Host, endpoint.Port, endpoint.UserName,
            _session.Password ?? throw new InvalidOperationException("SSH session ended."));
        client.HostKeyReceived += (_, args) =>
            args.CanTrust = string.Equals(ServerHostTrustRules.Fingerprint(args.HostKey), fingerprint, StringComparison.Ordinal);
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

    private async Task<T> ExecuteAsync<T>(Func<SftpClient, T> action)
    {
        using var client = await Task.Run(OpenClient);
        return await Task.Run(() => action(client));
    }

    private async Task OpenEntryAsync(SshFileEntry entry)
    {
        if (_busy) return;
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.Path);
            return;
        }
        if (IsPackagePicker && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            _selectPackage!(entry.Path);
            return;
        }
        if (_canOpenFile(entry.Path) && !_openFile(entry.Path))
            StatusMessage = LocalizedStatus.Key("ssh_files.file_open_failed", "Unable to open this file with an associated application");
    }

    private async Task NavigateAsync(string path, bool addHistory = true)
    {
        if (_busy || !_session.IsConnected) return;
        CancelRename();
        _busy = true;
        UpdateControls();
        StatusMessage = LocalizedStatus.Key("ssh_files.loading", "Loading…");
        try
        {
            var result = await ExecuteAsync(client =>
            {
                var requested = path == "." || path.StartsWith('/') ? path : Child(_path, path);
                client.ChangeDirectory(requested);
                var absolute = client.WorkingDirectory;
                var entries = client.ListDirectory(absolute)
                    .Where(file => file.Name is not ("." or ".."))
                    .Select(file => new SshFileEntry(file.Name, file.FullName, file.IsDirectory,
                        file.IsSymbolicLink, file.Length, file.LastWriteTime)).ToArray();
                return (absolute, entries);
            });
            if (_path != result.absolute) _presentation.ClearSelection();
            _path = result.absolute;
            _homePath ??= _path;
            AddressBox.Text = _path;
            BuildBreadcrumbs();
            _entries = result.entries;
            _activeTab.Path = _path;
            if (addHistory && (_historyIndex < 0 || _history[_historyIndex] != _path))
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(_path);
                _historyIndex = _history.Count - 1;
            }
            ApplyView();
            InitializeNavigationTree();
            await SyncNavigationTreeAsync();
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            AddressBox.Text = _path;
            StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Key("ssh_files.open_failed", "Unable to open folder"), LocalizedStatus.Literal(ex.Message)]);
        }
        finally { _busy = false; CaptureActiveTab(); UpdateControls(); }
    }

    private async Task RefreshAsync()
    {
        await NavigateAsync(_path, false);
        await RebuildNavigationTreeAsync();
    }

    private void InitializeNavigationTree()
    {
        if (_homeNode is not null) return;
        _homeNode = CreateTreeNode(T("ssh_files.home", "Home"), _homePath ?? _path, TreeNodeIconKind.Home);
        _rootNode = CreateTreeNode(T("ssh_files.root", "Root"), "/", TreeNodeIconKind.Drive);
        _navigationNodes.Add(_homeNode);
        _navigationNodes.Add(_rootNode);
    }

    private TreeNodeModel CreateTreeNode(string label, string path, TreeNodeIconKind iconKind)
    {
        var node = new TreeNodeModel(label, path, iconKind);
        node.AddDummyChild();
        node.ExpandRequested = LoadTreeChildrenAsync;
        return node;
    }

    private void BuildBreadcrumbs()
    {
        BreadcrumbsHost.Children.Clear();
        var basePath = _homePath;
        var label = T("ssh_files.root", "Root");
        var path = "/";
        var remaining = _path.Trim('/');

        if (basePath is not null && IsAtOrWithin(basePath, _path))
        {
            label = T("ssh_files.home", "Home");
            path = basePath;
            remaining = _path[basePath.Length..].Trim('/');
        }

        AddBreadcrumb(label, path);
        foreach (var segment in remaining.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path == "/" ? "/" + segment : path.TrimEnd('/') + "/" + segment;
            AddBreadcrumb(segment, path);
        }
        ScrollBreadcrumbsToLocation();
    }

    private void AddBreadcrumb(string label, string path)
    {
        if (BreadcrumbsHost.Children.Count > 0)
            BreadcrumbsHost.Children.Add(new PathIcon { Width = 10, Height = 10,
                Data = Avalonia.Media.Geometry.Parse("M10 6 L8.59 7.41 L13.17 12 L8.59 16.59 L10 18 L16 12 Z"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Opacity = 0.5 });
        var button = new Button { Content = label, Tag = path, Padding = new Thickness(7, 0),
            Height = 26, MinHeight = 0, Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        button.Click += Breadcrumb_Click;
        BreadcrumbsHost.Children.Add(button);
    }

    private async Task RebuildNavigationTreeAsync()
    {
        if (_homeNode is null) return;
        _treeRevision++;
        _treeLoads.Clear();
        _navigationNodes.Clear();
        _homeNode = null;
        _rootNode = null;
        InitializeNavigationTree();
        await SyncNavigationTreeAsync();
    }

    private async Task SyncNavigationTreeAsync()
    {
        if (_homeNode is null || _rootNode is null) return;
        var revision = ++_treeSelectionRevision;
        _syncingTreeSelection = true;
        try
        {
            var current = IsAtOrWithin(_homeNode.Path!, _path) ? _homeNode : _rootNode;
            var remainingPath = _path[current.Path!.Length..].Trim('/');
            foreach (var segment in remainingPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                await LoadTreeChildrenAsync(current);
                if (revision != _treeSelectionRevision) return;
                current.IsExpanded = true;
                var child = current.Children.FirstOrDefault(item => string.Equals(item.Label, segment,
                    StringComparison.Ordinal) && !item.IsPlaceholder);
                if (child is null) break;
                current = child;
            }
            NavigationTree.SelectedItem = current;
        }
        finally { if (revision == _treeSelectionRevision) _syncingTreeSelection = false; }
    }

    private async Task LoadTreeChildrenAsync(TreeNodeModel node)
    {
        if (node.HasLoadedChildren || node.Path is null) return;
        if (_treeLoads.TryGetValue(node, out var existing))
        {
            await existing;
            return;
        }

        var load = LoadTreeChildrenCoreAsync(node, _treeRevision);
        _treeLoads[node] = load;
        try { await load; }
        finally { _treeLoads.Remove(node); }
    }

    private async Task LoadTreeChildrenCoreAsync(TreeNodeModel node, int revision)
    {
        node.IsLoading = true;
        try
        {
            var directories = await ExecuteAsync(client => client.ListDirectory(node.Path!)
                .Where(file => file.Name is not ("." or "..") && file.IsDirectory && !file.IsSymbolicLink
                    && (_showHidden || !file.Name.StartsWith('.')))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => (file.Name, file.FullName))
                .ToArray());
            if (revision != _treeRevision) return;

            node.MarkChildrenLoaded();
            foreach (var directory in directories)
                node.Children.Add(CreateTreeNode(directory.Name, directory.FullName, TreeNodeIconKind.Folder));
        }
        catch (Exception ex)
        {
            if (revision == _treeRevision)
                StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Key("ssh_files.tree_load_failed", "Unable to load folder tree"), LocalizedStatus.Literal(ex.Message)]);
        }
        finally { node.IsLoading = false; }
    }

    private async Task BackAsync()
    {
        if (_historyIndex <= 0) return;
        var next = _historyIndex - 1;
        await NavigateAsync(_history[next], false);
        if (_path == _history[next]) _historyIndex = next;
        UpdateControls();
    }

    private async Task ForwardAsync()
    {
        if (_historyIndex >= _history.Count - 1) return;
        var next = _historyIndex + 1;
        await NavigateAsync(_history[next], false);
        if (_path == _history[next]) _historyIndex = next;
        UpdateControls();
    }

    private SshFileEntry[] Selection() => EntriesGrid.SelectedItems.OfType<SshFileEntry>().ToArray();

    private void ApplyView()
    {
        var query = SearchText.Trim();
        IEnumerable<SshFileEntry> entries = _entries.Where(file =>
            (_showHidden || !file.Name.StartsWith('.')) &&
            (string.IsNullOrEmpty(query) || file.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)) &&
            (!IsPackagePicker || file.IsDirectory || file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));
        var selectedPaths = Selection().Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var sorted = entries.ToArray();
        UpdateCutAppearance();
        Array.Sort(sorted, CompareEntries);
        EntriesGrid.ItemsSource = sorted;
        foreach (var entry in sorted)
            if (selectedPaths.Contains(entry.Path)) EntriesGrid.SelectedItems.Add(entry);
        UpdateControls();
    }

    private int CompareEntries(SshFileEntry x, SshFileEntry y)
    {
        var group = y.IsDirectory.CompareTo(x.IsDirectory);
        if (group != 0) return group;
        var value = _sortIndex switch
        {
            1 => x.Modified.CompareTo(y.Modified),
            2 => StringComparer.CurrentCultureIgnoreCase.Compare(ExplorerPath.Extension(x.Name), ExplorerPath.Extension(y.Name)),
            3 => x.Size.CompareTo(y.Size),
            _ => ExplorerEntryComparer.CompareNames(x.Name, y.Name),
        };
        if (value == 0) value = ExplorerEntryComparer.CompareNames(x.Name, y.Name);
        if (value == 0) value = StringComparer.Ordinal.Compare(x.Path, y.Path);
        return _descending ? -Math.Sign(value) : Math.Sign(value);
    }

    private void UpdateControls()
    {
        var selected = Selection();
        var count = (EntriesGrid.ItemsSource as SshFileEntry[])?.Length ?? 0;
        SummaryText.Text = selected.Length == 0
            ? string.Format(T("ssh_files.count", "{0} items"), count)
            : string.Format(T("ssh_files.selected_count", "{0} selected · {1} items"), selected.Length, count);
        BackButton.IsEnabled = !_busy && _historyIndex > 0;
        ForwardButton.IsEnabled = !_busy && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = !_busy && _path != "/";
        RefreshButton.IsEnabled = !_busy;
        NewFolderButton.IsEnabled = UploadButton.IsEnabled = UploadFolderButton.IsEnabled = !IsPackagePicker && !_busy;
        RenameButton.IsEnabled = !IsPackagePicker && !_busy && selected.Length == 1;
        DeleteButton.IsEnabled = DownloadButton.IsEnabled = !IsPackagePicker && !_busy && selected.Length > 0;
        CopyButton.IsEnabled = CutButton.IsEnabled = PasteButton.IsEnabled = !IsPackagePicker && !_busy && selected.Length > 0;
        if (!IsPackagePicker) PasteButton.IsEnabled = !_busy && _clipboard.Count > 0;
        PickerEntryName.Text = IsPackagePicker && Selection() is [var picked] ? picked.Name : string.Empty;
        ConfirmPickerButton.IsEnabled = CanConfirmPackage();
        NavigationTree.IsEnabled = !_busy;
        NavigationToolbar.IsEnabled = CommandToolbar.IsEnabled = !_busy;
        OverflowProperties.IsEnabled = !_busy && selected.Length == 1;
        FolderTabs.IsEnabled = FolderPickerButton.IsEnabled = NewTabButton.IsEnabled = !_busy;
    }

    private bool CanConfirmPackage() => IsPackagePicker && !_busy && Selection() is [var entry] &&
        !entry.IsDirectory && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private async Task ShowPropertiesAsync()
    {
        if (EntriesGrid.SelectedItem is not SshFileEntry entry || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new SshFilePropertiesDialog(entry).ShowDialog(owner);
    }

    private static string T(string key, string fallback) => LocalizedText.Get(key, fallback);

    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    private static string Child(string directory, string name) =>
        directory.TrimEnd('/') + "/" + name;

    private static bool IsAtOrWithin(string parent, string candidate) => candidate == parent ||
        candidate.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    private async Task<string?> PromptAsync(string title, string initial = "")
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        return await new SshFileNameDialog(title, initial).ShowDialog<string?>(owner);
    }

    private async Task CreateDirectoryAsync()
    {
        if (_busy || IsPackagePicker) return;
        var name = await PromptAsync(T("ssh_files.new_folder", "New folder"));
        if (!ValidName(name)) return;
        await ChangeAsync(client =>
        {
            var target = Child(_path, name!);
            if (client.Exists(target)) throw new IOException(T("ssh_files.exists", "Destination already exists"));
            client.CreateDirectory(target);
        });
    }

    private Task RenameAsync()
    {
        if (_busy || IsPackagePicker || Selection() is not [var selected]) return Task.CompletedTask;
        CancelRename();
        _editingEntry = selected;
        selected.RenameDraft = selected.Name;
        selected.IsEditing = true;
        if (EntriesGrid.IsVisible) EntriesGrid.ScrollIntoView(selected, null);
        else _presentation.Items.ScrollIntoView(selected);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = FindRenameEditor(selected);
            if (editor is null) return;
            editor.Focus();
            var extension = selected.IsDirectory ? -1 : selected.Name.LastIndexOf('.');
            if (extension > 0) { editor.SelectionStart = 0; editor.SelectionEnd = extension; }
            else editor.SelectAll();
        }, DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    private TextBox? FindRenameEditor(SshFileEntry entry) => EntriesHost.GetVisualDescendants()
        .OfType<TextBox>().FirstOrDefault(editor => editor.Classes.Contains("inline-rename")
            && ReferenceEquals(editor.DataContext, entry) && editor.IsEffectivelyVisible);

    private void CancelRename()
    {
        var entry = _editingEntry;
        _editingEntry = null;
        if (entry is not null)
        {
            entry.IsEditing = false;
            StatusMessage = "";
        }
    }

    private async void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        { e.Handled = true; CancelRename(); _presentation.Focus(); }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; await CommitRenameAsync(); }
    }

    private async void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_renameCommitInProgress && sender is TextBox { DataContext: SshFileEntry entry }
            && ReferenceEquals(entry, _editingEntry)) await CommitRenameAsync();
    }

    private async Task CommitRenameAsync()
    {
        if (_busy || _renameCommitInProgress || _editingEntry is not { } entry) return;
        var name = entry.RenameDraft;
        if (name == entry.Name) { CancelRename(); _presentation.Focus(); return; }
        if (!ValidName(name))
        {
            StatusMessage = LocalizedStatus.Key("explorer.status.file_name_invalid", "Invalid file name");
            Dispatcher.UIThread.Post(() => FindRenameEditor(entry)?.Focus());
            return;
        }
        _renameCommitInProgress = true;
        _busy = true; UpdateControls();
        var target = Child(ParentPath(entry.Path), name);
        var completed = false;
        try
        {
            await ExecuteAsync(client =>
            {
                if (client.Exists(target)) throw new IOException(T("ssh_files.exists", "Destination already exists"));
                client.RenameFile(entry.Path, target);
                return true;
            });
            CancelRename();
            completed = true;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { _busy = false; _renameCommitInProgress = false; UpdateControls(); }
        if (completed)
        {
            await RefreshAsync();
            if (_entries.FirstOrDefault(file => file.Path == target) is { } renamed) EntriesGrid.SelectedItem = renamed;
            _presentation.Focus();
        }
        else Dispatcher.UIThread.Post(() => FindRenameEditor(entry)?.Focus());
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name == name.Trim() && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\') && !name.Contains('\0');

    private async Task DeleteAsync()
    {
        if (_busy || IsPackagePicker) return;
        var selected = Selection();
        if (selected.Length == 0 || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await new SshFileDeleteDialog(selected.Length).ShowDialog<bool>(owner)) return;
        await ChangeAsync(client => { foreach (var entry in selected) DeleteTree(client, entry.Path, entry.IsDirectory && !entry.IsLink); });
    }

    private static void DeleteTree(SftpClient client, string path, bool directory)
    {
        if (!directory) { client.DeleteFile(path); return; }
        foreach (var child in client.ListDirectory(path).Where(file => file.Name is not ("." or "..")))
            DeleteTree(client, child.FullName, child.IsDirectory && !child.IsSymbolicLink);
        client.DeleteDirectory(path);
    }

    private void CopySelection(bool cut)
    {
        var selected = Selection();
        if (selected.Length == 0 || _busy || IsPackagePicker) return;
        _clipboard.Clear();
        _clipboard.AddRange(selected);
        _cutMode = cut;
        _sessionClipboard.Revision++;
        _sessionClipboard.Notify();
        UpdateCutAppearance();
        StatusMessage = LocalizedStatus.Key(cut ? "ssh_files.cut_ready" : "ssh_files.copy_ready",
            cut ? "{0} items ready to move" : "{0} items ready to copy", selected.Length);
        UpdateControls();
    }

    private void FilesMoved(string[] paths)
    {
        if (paths.Length == 0) return;
        var moved = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var tab in _tabs) tab.Entries = tab.Entries.Where(entry => !moved.Contains(entry.Path)).ToArray();
        _entries = _entries.Where(entry => !moved.Contains(entry.Path)).ToArray();
        if (_viewReady) ApplyView();
    }

    private void ClipboardChanged()
    {
        UpdateCutAppearance();
        if (_viewReady) UpdateControls();
    }

    private void UpdateCutAppearance()
    {
        var paths = _cutMode ? _clipboard.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var entry in _entries.Concat(_tabs.SelectMany(tab => tab.Entries)).Distinct())
            entry.CutOpacity = paths.Contains(entry.Path) ? 0.45 : 1;
    }

    private async Task PasteAsync()
    {
        if (_clipboard.Count == 0 || _busy || IsPackagePicker) return;
        var source = _clipboard.ToArray();
        var copy = !_cutMode;
        var revision = _sessionClipboard.Revision;
        var moved = await TransferEntriesAsync(source, _path, copy);
        if (!copy && revision == _sessionClipboard.Revision)
        {
            foreach (var entry in moved) _clipboard.Remove(entry);
            _sessionClipboard.Notify();
        }
        UpdateCutAppearance(); UpdateControls();
    }

    private async Task<SshFileEntry[]> TransferEntriesAsync(SshFileEntry[] source, string destination, bool copy)
    {
        var moved = new List<SshFileEntry>();
        var changedPaths = new List<string>();
        await ChangeAsync(client =>
        {
            foreach (var entry in source)
            {
                if (entry.IsLink && copy)
                    throw new IOException($"{entry.Path}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
                if (entry.IsDirectory && IsWithin(entry.Path, destination))
                    throw new IOException(T("ssh_files.circular_copy", "Cannot paste a folder into itself"));
                var target = Child(destination, entry.Name);
                if (entry.Path == target && !copy) { moved.Add(entry); continue; }
                if (entry.Path == target) target = UniqueCopyTarget(client, destination, entry.Name, entry.IsDirectory);
                if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {entry.Name}");
                if (!copy) { client.RenameFile(entry.Path, target); moved.Add(entry); changedPaths.Add(entry.Path); }
                else CopyTree(client, entry.Path, target, entry.IsDirectory && !entry.IsLink);
            }
        });
        _sessionClipboard.NotifyMoved(changedPaths.ToArray());
        return moved.ToArray();
    }

    private static string UniqueCopyTarget(SftpClient client, string directory, string name, bool directoryEntry)
    {
        var dot = directoryEntry ? -1 : name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : "";
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? " (copy)" : $" (copy {index})";
            var target = Child(directory, stem + suffix + extension);
            if (!client.Exists(target)) return target;
        }
    }

    private static bool IsWithin(string parent, string candidate) => candidate == parent ||
        candidate.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static void CopyTree(SftpClient client, string source, string target, bool directory)
    {
        if (!directory)
        {
            using var input = client.OpenRead(source);
            using var output = client.OpenWrite(target);
            input.CopyTo(output);
            return;
        }
        client.CreateDirectory(target);
        foreach (var child in client.ListDirectory(source).Where(file => file.Name is not ("." or "..")))
        {
            if (child.IsSymbolicLink) throw new IOException($"{child.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            CopyTree(client, child.FullName, Child(target, child.Name), child.IsDirectory);
        }
    }

    private async Task UploadAsync()
    {
        if (_busy || IsPackagePicker) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await UsageFilePicker.OpenFilePickerAsync(storage, new FilePickerOpenOptions { AllowMultiple = true }, UsageMemoryStore.CaptureServices(App.Services), "SshFileBrowserView.axaml.0");
        if (picked.Count == 0 || _busy) return;
        using var progress = new SshTransferProgressWindow(this, uploading: true);
        _busy = true; UpdateControls();
        var uploaded = 0;
        try
        {
            foreach (var file in picked)
            {
                StatusMessage = LocalizedStatus.Key("ssh_files.uploading", "Uploading {0} of {1}: {2}", uploaded + 1, picked.Count, file.Name);
                await using var input = await file.OpenReadAsync();
                progress.Report(file.Name, 0, input.CanSeek ? input.Length : null);
                await ExecuteAsync(client =>
                {
                    var target = Child(_path, file.Name);
                    if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {file.Name}");
                    client.UploadFile(input, target, canOverride: false,
                        uploaded => progress.Report(file.Name, (long)uploaded, input.CanSeek ? input.Length : null));
                    return true;
                });
                uploaded++;
            }
        }
        catch (Exception ex) { StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Key("ssh_files.upload_failed", "Upload failed"), LocalizedStatus.Literal(ex.Message)]); }
        finally { _busy = false; UpdateControls(); }
        var message = StatusMessage;
        await NavigateAsync(_path, false);
        if (uploaded == picked.Count) StatusMessage = LocalizedStatus.Key("ssh_files.uploaded", "Uploaded {0} files", uploaded);
        else StatusMessage = message;
    }

    private async Task UploadFolderAsync()
    {
        if (_busy || IsPackagePicker) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _busy) return;
        var folders = await UsageFilePicker.OpenFolderPickerAsync(storage, new FolderPickerOpenOptions { AllowMultiple = false }, UsageMemoryStore.CaptureServices(App.Services), "SshFileBrowserView.axaml.1");
        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (localPath is null) return;
        var folder = new DirectoryInfo(localPath);
        using var progress = new SshTransferProgressWindow(this, uploading: true);
        _busy = true; UpdateControls();
        try
        {
            StatusMessage = LocalizedStatus.Key("ssh_files.uploading_folder", "Uploading folder {0}", folder.Name);
            await ExecuteAsync(client =>
            {
                var target = Child(_path, folder.Name);
                if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {folder.Name}");
                UploadTree(client, folder, target, progress);
                return true;
            });
            StatusMessage = LocalizedStatus.Key("ssh_files.uploaded_folder", "Uploaded folder {0}", folder.Name);
        }
        catch (Exception ex) { StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Key("ssh_files.upload_failed", "Upload failed"), LocalizedStatus.Literal(ex.Message)]); }
        finally { _busy = false; UpdateControls(); }
        var message = StatusMessage;
        await NavigateAsync(_path, false);
        if (!string.IsNullOrEmpty(message)) StatusMessage = message;
    }

    private static void UploadTree(SftpClient client, DirectoryInfo directory, string target, SshTransferProgressWindow progress)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{directory.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
        progress.Cancellation.ThrowIfCancellationRequested();
        client.CreateDirectory(target);
        foreach (var file in directory.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{file.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            using var input = file.OpenRead();
            progress.Report(file.FullName, 0, input.Length);
            client.UploadFile(input, Child(target, file.Name), canOverride: false,
                uploaded => progress.Report(file.FullName, (long)uploaded, input.Length));
        }
        foreach (var child in directory.EnumerateDirectories())
            UploadTree(client, child, Child(target, child.Name), progress);
    }

    private async Task DownloadAsync()
    {
        if (_busy || IsPackagePicker) return;
        var selected = Selection();
        if (selected.Length == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _busy) return;
        if (selected.FirstOrDefault(file => file.IsLink) is { } link)
        {
            StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Literal(link.Path), LocalizedStatus.Key("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")]);
            return;
        }
        if (selected.Length == 1 && !selected[0].IsDirectory)
        {
            var file = await UsageFilePicker.SaveFilePickerAsync(storage, new FilePickerSaveOptions { SuggestedFileName = selected[0].Name }, UsageMemoryStore.CaptureServices(App.Services), "SshFileBrowserView.axaml.2");
            if (file is null) return;
            await RunDownloadAsync(selected[0], file);
            return;
        }
        var folders = await UsageFilePicker.OpenFolderPickerAsync(storage, new FolderPickerOpenOptions { AllowMultiple = false }, UsageMemoryStore.CaptureServices(App.Services), "SshFileBrowserView.axaml.3");
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return;
        using var progress = new SshTransferProgressWindow(this, uploading: false);
        _busy = true; UpdateControls();
        try
        {
            StatusMessage = LocalizedStatus.Key("ssh_files.downloading", "Downloading {0}", string.Join(", ", selected.Select(file => file.Name)));
            await ExecuteAsync(client =>
            {
                foreach (var entry in selected)
                    DownloadTree(client, entry.Path, SafeLocalChild(folder, entry.Name), entry.IsDirectory && !entry.IsLink, progress);
                return true;
            });
            StatusMessage = LocalizedStatus.Key("ssh_files.downloaded", "Downloaded {0} items", selected.Length);
        }
        catch (Exception ex) { StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Key("ssh_files.download_failed", "Download failed"), LocalizedStatus.Literal(ex.Message)]); }
        finally { _busy = false; UpdateControls(); }
    }

    private async Task RunDownloadAsync(SshFileEntry entry, IStorageFile file)
    {
        using var progress = new SshTransferProgressWindow(this, uploading: false);
        progress.Report(entry.Name, 0, entry.Size);
        _busy = true; UpdateControls();
        try
        {
            await using var output = await file.OpenWriteAsync();
            await ExecuteAsync(client => { client.DownloadFile(entry.Path, output,
                downloaded => progress.Report(entry.Name, (long)downloaded, entry.Size)); return true; });
            StatusMessage = LocalizedStatus.Key("ssh_files.downloaded", "Downloaded {0} items", 1);
        }
        catch (Exception ex) { StatusMessage = LocalizedStatus.Join(": ", [LocalizedStatus.Key("ssh_files.download_failed", "Download failed"), LocalizedStatus.Literal(ex.Message)]); }
        finally { _busy = false; UpdateControls(); }
    }

    private static void DownloadTree(SftpClient client, string source, string destination, bool directory, SshTransferProgressWindow progress)
    {
        if (!directory)
        {
            var size = client.GetAttributes(source).Size;
            progress.Report(source, 0, size);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            client.DownloadFile(source, output, downloaded => progress.Report(source, (long)downloaded, size));
            return;
        }
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {destination}");
        Directory.CreateDirectory(destination);
        foreach (var child in client.ListDirectory(source).Where(file => file.Name is not ("." or "..")))
        {
            if (child.IsSymbolicLink) throw new IOException($"{child.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            DownloadTree(client, child.FullName, SafeLocalChild(destination, child.Name), child.IsDirectory, progress);
        }
    }

    private static string SafeLocalChild(string directory, string name)
    {
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || Path.GetFileName(name) != name)
            throw new IOException($"{T("ssh_files.invalid_local_name", "File name is not valid on this device")}: {name}");
        return Path.Combine(directory, name);
    }

    private async Task<bool> ChangeAsync(Action<SftpClient> action)
    {
        if (_busy) return false;
        _busy = true; UpdateControls();
        var completed = false;
        try
        {
            await ExecuteAsync(client => { action(client); return true; });
            StatusMessage = "";
            completed = true;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { _busy = false; UpdateControls(); }
        var message = StatusMessage;
        await RefreshAsync();
        if (!string.IsNullOrEmpty(message)) StatusMessage = message;
        return completed;
    }
}
