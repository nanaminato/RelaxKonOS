using System.ComponentModel;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Client.Apps.Explorer.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

public partial class ExplorerMainView : UserControl
{
    public static readonly StyledProperty<double> EntriesTableWidthProperty =
        AvaloniaProperty.Register<ExplorerMainView, double>(nameof(EntriesTableWidth));

    public static readonly StyledProperty<double> TabContentWidthProperty =
        AvaloniaProperty.Register<ExplorerMainView, double>(nameof(TabContentWidth), MaximumTabWidth);

    /// <summary>Width every folder tab takes; it shrinks with the tab count down to its floor.</summary>
    public double TabContentWidth
    {
        get => GetValue(TabContentWidthProperty);
        private set => SetValue(TabContentWidthProperty, value);
    }

    private sealed record ExplorerDragPayload(ExplorerViewModel Source, IReadOnlyList<FileSystemEntryDto> Entries);
    private static readonly DataFormat<ExplorerDragPayload> ExplorerEntriesFormat =
        DataFormat.CreateInProcessFormat<ExplorerDragPayload>("relaxkonos/explorer-entries");
    private const double MinimumDragDistance = 5;

    /// <summary>A folder tab states its location's mark, its name and its close mark together.</summary>
    private const double MaximumTabWidth = 200;
    /// <summary>The floor a tab shrinks to before the strip starts scrolling instead.</summary>
    private const double MinimumTabWidth = 72;
    /// <summary>Per-tab chrome: the item's margin plus its content padding.</summary>
    private const double TabItemChrome = 18;
    private const double TabMinimumPitch = MinimumTabWidth + TabItemChrome;
    /// <summary>Distance one wheel notch travels along a strip that only scrolls sideways.</summary>
    private const double TabWheelStep = 60;

    private readonly FileBrowserPresentation _presentation;
    private PointerPressedEventArgs? _dragTrigger;
    private FileSystemEntryDto? _dragEntry;
    private IReadOnlyList<FileSystemEntryDto>? _preservedDragSelection;
    private bool _toggleSelectionOnRelease;
    private Point _dragStart;
    private readonly ContextMenu? _entryContextMenu;
    private ExplorerViewModel? _attachedViewModel;
    private DataGridColumnHeader? _pressedSortHeader;
    private double _pressedSortHeaderWidth;
    private readonly Flyout _searchFlyout;
    private bool _compactSearch;

    /// <summary>Combined live width of the details columns, used to constrain row layout.</summary>
    public double EntriesTableWidth
    {
        get => GetValue(EntriesTableWidthProperty);
        private set => SetValue(EntriesTableWidthProperty, value);
    }

    public ExplorerMainView()
    {
        InitializeComponent();
        BreadcrumbScroll.SizeChanged += (_, _) => ScrollBreadcrumbsToLocation();
        // The strip is exactly as wide as its tabs now, so the width the tabs divide between them
        // is the scroller's — and that is also what decides whether the row has filled up.
        TabScroller.SizeChanged += (_, e) => UpdateTabLayout(e.NewSize.Width);
        // Tunnelling on the strip's own scroller keeps the wheel inside the tab row.
        TabScroller.AddHandler(PointerWheelChangedEvent, OnTabStripWheel, RoutingStrategies.Tunnel);
        _searchFlyout = (Flyout)CompactSearchButton.Flyout!;
        _searchFlyout.Opened += (_, _) => { CompactSearchBox.Focus(); CompactSearchBox.SelectAll(); };
        _entryContextMenu = EntriesGrid.ContextMenu;
        _presentation = new FileBrowserPresentation(EntriesScrollViewer, EntriesGrid, () => ViewModel is { IsBusy: false });
        _presentation.Items.AddHandler(PointerPressedEvent, EntriesGrid_PointerPressed, RoutingStrategies.Tunnel);
        _presentation.Items.PointerMoved += EntriesGrid_PointerMoved;
        _presentation.Items.PointerReleased += EntriesGrid_PointerReleased;
        _presentation.Items.DoubleTapped += EntriesGrid_DoubleTapped;
        _presentation.Items.KeyDown += EntriesGrid_KeyDown;
        DragDrop.SetAllowDrop(_presentation.Items, true);
        EntriesGrid.AddHandler(PointerPressedEvent, EntriesGrid_PointerPressed, RoutingStrategies.Tunnel);
        EntriesGrid.AddHandler(PointerPressedEvent, EntriesGrid_SortHeaderPointerPressed, RoutingStrategies.Tunnel);
        EntriesGrid.AddHandler(PointerReleasedEvent, EntriesGrid_SortHeaderPointerReleased, RoutingStrategies.Tunnel);
        EntriesGrid.LayoutUpdated += EntriesGrid_LayoutUpdated;
        DataContextChanged += ExplorerMainView_DataContextChanged;
        SizeChanged += (_, e) =>
        {
            FileContentGrid.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 620 ? 0 : 210);
            FileContentGrid.ColumnDefinitions[1].Width = new GridLength(e.NewSize.Width < 620 ? 0 : 4);
        };
    }

    /// <summary>
    /// Gives every folder tab the same share of the row, down to a readable floor. Once the tabs
    /// have reached that floor and still fill the row the strip becomes scrollable and the two
    /// scroll marks appear, so a folder is never left unreachable — only moved out of sight.
    /// </summary>
    private void UpdateTabLayout(double available)
    {
        // The view model's tab list is the authority: a ListBox applies items a beat behind a
        // collection change, and dividing the row by a stale count would leave the last tab short.
        var count = ViewModel?.Tabs.Count ?? FolderTabs.ItemCount;
        // The share is floored: item measurement rounds every tab up to whole pixels, so a share
        // taken from an unrounded division would push the last tab past the edge of the row.
        TabContentWidth = count > 0 && available > 0
            ? Math.Clamp(Math.Floor(available / count) - TabItemChrome, MinimumTabWidth, MaximumTabWidth)
            : MaximumTabWidth;

        var filled = count > 0 && available > 0 && count * TabMinimumPitch > available + 0.5;
        if (TabScrollLeft.IsVisible != filled)
        {
            TabScrollLeft.IsVisible = TabScrollRight.IsVisible = filled;
            if (!filled) TabScroller.Offset = TabScroller.Offset.WithX(0);
        }
        UpdateTabScrollMarks();
    }

    /// <summary>Keeps the tab the user is on inside the strip's window, however it was chosen.</summary>
    private void ScrollActiveTabIntoView()
    {
        if (FolderTabs.SelectedItem is not { } tab || FolderTabs.ContainerFromItem(tab) is not Control container) return;
        var extent = TabScroller.Extent.Width;
        var viewport = TabScroller.Viewport.Width;
        if (extent <= viewport || container.TranslatePoint(default, TabScroller) is not { } origin) return;
        var offset = TabScroller.Offset.X;
        if (origin.X < 0) offset += origin.X;
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

    public void AttachWindowHeader(ManagedWindow window)
    {
        if (ViewModel?.IsPickerMode != false) return;
        HeaderTabs.DataContext = DataContext;
        NavigationToolbar.DataContext = DataContext;
        HeaderHost.Children.Clear();
        window.View.HeaderTabs = HeaderTabs;
        window.View.HeaderToolbar = NavigationToolbar;
    }

    private void NavigationToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        GoUpButton.IsVisible = RefreshButton.IsVisible = e.NewSize.Width >= 520;
        var compact = e.NewSize.Width < 600;
        if (compact != _compactSearch) _searchFlyout.Hide();
        _compactSearch = compact;
        CompactSearchButton.IsVisible = compact;
        InlineSearchHost.IsVisible = !compact;
        SearchBox.Width = Math.Clamp(e.NewSize.Width * 0.24, 140, 260);
    }

    private void CommandToolbar_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Respond to this row's available width, including when the host reparents navigation.
        var width = e.NewSize.Width;
        RenameAction.IsVisible = DeleteAction.IsVisible = width >= 760;
        CutAction.IsVisible = CopyAction.IsVisible = PasteAction.IsVisible = width >= 620;
        EditingDivider.IsVisible = width >= 620;
        ViewDivider.IsVisible = ViewModel?.IsPickerMode != true && width >= 480;
        var labels = width >= 480;
        NewFolderLabel.IsVisible = SortLabel.IsVisible = ViewLabel.IsVisible = labels;
        SortChevron.IsVisible = ViewChevron.IsVisible = labels;
    }

    private async void FolderTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // However the active location changed — the user, a shortcut, or the view model itself —
        // the tab that names it has to end up visible.
        Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
        if (ViewModel is not { } vm || FolderTabs.SelectedItem is not ExplorerTab tab
            || ReferenceEquals(tab, vm.ActiveTab)) return;
        await vm.SwitchTabAsync(tab);
        FolderTabs.SetCurrentValue(ListBox.SelectedItemProperty, vm.ActiveTab);
        Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
    }

    private void EntriesGrid_LayoutUpdated(object? sender, EventArgs e)
    {
        var width = EntriesGrid.Columns.Sum(column => column.ActualWidth);
        if (width <= 0 || Math.Abs(width - EntriesTableWidth) < 0.1) return;
        EntriesTableWidth = width;
    }

    private void ExplorerMainView_DataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.RequestRenameFocus = null;
            _attachedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _attachedViewModel.Tabs.CollectionChanged -= TabsChanged;
        }
        _attachedViewModel = ViewModel;
        CompactSearchBox.DataContext = DataContext;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.RequestRenameFocus = FocusRenameEditor;
            _attachedViewModel.PropertyChanged += ViewModel_PropertyChanged;
            // Every tab that arrives or leaves re-divides the row, so the share is recomputed from
            // the tab count rather than from the strip's own size.
            _attachedViewModel.Tabs.CollectionChanged += TabsChanged;
            _presentation.SetMode(_attachedViewModel.ViewMode);
            UpdateTabLayout(TabScroller.Bounds.Width);
        }
    }

    private void TabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateTabLayout(TabScroller.Bounds.Width);
        Dispatcher.UIThread.Post(ScrollActiveTabIntoView, DispatcherPriority.Background);
    }

    private void ScrollBreadcrumbsToLocation() => Dispatcher.UIThread.Post(() =>
        BreadcrumbScroll.Offset = new Vector(Math.Max(0, BreadcrumbScroll.Extent.Width - BreadcrumbScroll.Viewport.Width), 0),
        DispatcherPriority.Loaded);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.IsBusy) && ViewModel is { IsBusy: false })
            Dispatcher.UIThread.Post(() =>
            {
                if (ViewModel is not { IsBusy: false }
                    || this.GetVisualAncestors().OfType<RemoteWindow>().FirstOrDefault()?.DataContext is not ManagedWindow { IsActive: true }) return;
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                if (focused is not Control control || control is TopLevel or RemoteWindow || TopLevel.GetTopLevel(control) is null
                    || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled)
                    _presentation.Focus();
            }, DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(ExplorerViewModel.AddressbarPath)) ScrollBreadcrumbsToLocation();
        if (e.PropertyName == nameof(ExplorerViewModel.ViewMode) && ViewModel is { } vm)
            _presentation.SetMode(vm.ViewMode);
    }

    private void ViewModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_presentation is not null && ViewModel is { } vm && ViewModeBox.SelectedIndex >= 0)
            vm.ViewModeIndex = ViewModeBox.SelectedIndex;
    }

    /// <summary>Moves keyboard focus to the current-folder address field.</summary>
    public void FocusAddressBox()
    {
        if (ViewModel is { } vm)
        {
            vm.AddressInput = vm.AddressbarPath;
            vm.IsEditingAddress = true;
        }
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    public bool IsTextEditing => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    public TextBox ActiveSearchBox => _compactSearch ? CompactSearchBox : SearchBox;

    public void FocusSearchBox()
    {
        if (_compactSearch) _searchFlyout.ShowAt(CompactSearchButton);
        ActiveSearchBox.Focus();
        ActiveSearchBox.SelectAll();
    }

    private void EditAddress_Click(object? sender, RoutedEventArgs e) => FocusAddressBox();

    private void Breadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) _ = ViewModel?.NavigateToAsync(button.Tag as string);
    }

    private void AddressBox_LostFocus(object? sender, RoutedEventArgs e) => ViewModel?.CancelAddressEdit();

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (ViewModel is { } vm) vm.SearchText = string.Empty;
        _searchFlyout.Hide();
        _presentation.Focus();
        e.Handled = true;
    }

    private void EntriesGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { IsBusy: false } vm) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt && vm.PropertiesCommand.CanExecute(null))
        {
            vm.PropertiesCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && vm.GoBackCommand.CanExecute(null))
        {
            vm.GoBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control && vm.EntrySelectionMode == DataGridSelectionMode.Extended)
        {
            EntriesGrid.SelectAll();
            e.Handled = true;
        }
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    /// <summary>地址栏回车跳转。</summary>
    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel?.CancelAddressEdit();
            _presentation.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && sender is TextBox tb)
        {
            _ = ViewModel?.AddressbarGoAsync(tb.Text);
            e.Handled = true;
        }
    }

    /// <summary>Double-click only activates the row under the pointer.</summary>
    private void EntriesGrid_DoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (IsWithinTextBox(e.Source)) return;
        if (FindDataContext<FileSystemEntryDto>(e.Source) is { } entry && ViewModel is { IsBusy: false } vm)
            _ = vm.InvokeEntryAsync(entry);
    }

    private void EntriesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
            ViewModel?.UpdatePickerSelection(grid.SelectedItems?.Cast<object>() ?? []);
    }

    private void EntriesGrid_SortHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedSortHeader = null;
        if (!e.GetCurrentPoint(EntriesGrid).Properties.IsLeftButtonPressed) return;
        var header = FindVisualAncestor<DataGridColumnHeader>(e.Source);
        if (header is not null && SortFieldForHeader(header).HasValue)
        {
            _pressedSortHeader = header;
            _pressedSortHeaderWidth = header.Bounds.Width;
        }
    }

    private void EntriesGrid_SortHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressedHeader = _pressedSortHeader;
        _pressedSortHeader = null;
        if (pressedHeader is null || !ReferenceEquals(pressedHeader, FindVisualAncestor<DataGridColumnHeader>(e.Source))) return;
        // A drag on the column divider changes the column width; it must not also sort.
        if (Math.Abs(pressedHeader.Bounds.Width - _pressedSortHeaderWidth) >= 0.1) return;
        if (SortFieldForHeader(pressedHeader) is { } field && ViewModel is { IsBusy: false } vm)
            vm.SortBy(field);
    }

    private void EntriesGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearPendingDrag();
        if (IsWithinTextBox(e.Source)) return;
        var entry = FindDataContext<FileSystemEntryDto>(e.Source);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            EntriesGrid.ContextMenu = entry is null ? EntriesScrollViewer.ContextMenu : _entryContextMenu;
            _presentation.Items.ContextMenu = EntriesGrid.ContextMenu;
            if (entry is null) _presentation.ClearSelection();
            else if (!EntriesGrid.SelectedItems.Contains(entry)) EntriesGrid.SelectedItem = entry;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed || entry is null || ViewModel?.CanDragEntry(entry) != true)
            return;

        _dragTrigger = e;
        _dragEntry = entry;
        _dragStart = e.GetPosition(this);
        // Delay collapsing/toggling an existing multi-selection until a click is released.
        // If a drag starts, it operates on the whole original selection instead.
        if (EntriesGrid.SelectedItems.Contains(entry) && EntriesGrid.SelectedItems.Count > 1
            && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Control))
        {
            _preservedDragSelection = ViewModel.GetDragEntries(entry);
            _toggleSelectionOnRelease = e.KeyModifiers == KeyModifiers.Control;
            _presentation.Focus();
            e.Handled = true;
        }
    }

    private async void EntriesGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTrigger is null || _dragEntry is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ClearPendingDrag();
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < MinimumDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < MinimumDragDistance)
            return;

        var trigger = _dragTrigger;
        var entry = _dragEntry;
        var vm = ViewModel;
        var entries = _preservedDragSelection ?? vm?.GetDragEntries(entry);
        ClearPendingDrag();
        if (vm is null || entries is null || entries.Count == 0) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ExplorerEntriesFormat, new ExplorerDragPayload(vm, entries.ToArray())));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void EntriesGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_preservedDragSelection is not null && _dragEntry is { } entry)
        {
            if (_toggleSelectionOnRelease) EntriesGrid.SelectedItems.Remove(entry);
            else
            {
                _presentation.ClearSelection();
                EntriesGrid.SelectedItem = entry;
            }
        }
        ClearPendingDrag();
    }

    private void Explorer_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetDrop(e, out _, out _) ? DropEffect(e) : DragDropEffects.None;
        e.Handled = true;
    }

    private static DragDropEffects DropEffect(DragEventArgs e)
        => e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy : DragDropEffects.Move;

    private async void Explorer_Drop(object? sender, DragEventArgs e)
    {
        if (!TryGetDrop(e, out var payload, out var targetDirectory))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var copy = DropEffect(e) == DragDropEffects.Copy;
        e.DragEffects = DropEffect(e);
        e.Handled = true;
        if (ViewModel is not { } vm) return;
        var result = await vm.TransferEntriesToDirectoryAsync(payload.Entries, targetDirectory, copy);
        if (!copy && result?.Completed.Count > 0 && !ReferenceEquals(payload.Source, vm) && !payload.Source.IsBusy)
            await payload.Source.RefreshCommand.ExecuteAsync(null);
    }

    private bool TryGetDrop(DragEventArgs e, out ExplorerDragPayload payload, out string targetDirectory)
    {
        payload = e.DataTransfer.TryGetValue(ExplorerEntriesFormat)!;
        targetDirectory = FindDropTargetPath(e.Source) ?? string.Empty;
        return !e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && !(e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            && payload is not null && !payload.Source.IsBusy
            && ViewModel?.CanTransferEntriesToDirectory(payload.Entries, targetDirectory, DropEffect(e) == DragDropEffects.Copy) == true;
    }

    private string? FindDropTargetPath(object? source)
    {
        for (var control = source as Control; control is not null; control = control.GetVisualParent() as Control)
        {
            if (control.DataContext is FileSystemEntryDto
                {
                    Type: FileSystemEntryType.Directory or FileSystemEntryType.Drive
                } entry)
                return entry.Path;

            if (control.DataContext is TreeNodeModel
                {
                    IsPlaceholder: false,
                    IsComputer: false,
                    IsNetwork: false,
                    Path: { Length: > 0 } path
                })
                return path;

            // Empty space in the file list represents the directory currently being viewed.
            if (ReferenceEquals(control, EntriesGrid) || ReferenceEquals(control, _presentation.Items) || ReferenceEquals(control, EntriesScrollViewer))
                return ViewModel?.AddressbarPath;
        }

        return null;
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
            if (control is T value) return value;
        return null;
    }

    private ExplorerSortField? SortFieldForHeader(DataGridColumnHeader header)
    {
        if (ViewModel is not { } vm || header.Content is not string text) return null;
        if (text == vm.NameColumnHeader) return ExplorerSortField.Name;
        if (text == vm.ModifiedColumnHeader) return ExplorerSortField.Modified;
        if (text == vm.TypeColumnHeader) return ExplorerSortField.Type;
        return text == vm.SizeColumnHeader ? ExplorerSortField.Size : null;
    }

    private static bool IsWithinTextBox(object? source)
    {
        for (var control = source as Control; control is not null; control = control.GetVisualParent() as Control)
        {
            if (control is TextBox) return true;
            if (control is DataGrid) return false;
        }
        return false;
    }

    private void FocusRenameEditor(FileSystemEntryDto entry)
    {
        if (EntriesGrid.IsVisible) EntriesGrid.ScrollIntoView(entry, null);
        else _presentation.Items.ScrollIntoView(entry);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = EntriesScrollViewer.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(textBox => textBox.Classes.Contains("inline-rename")
                    && ReferenceEquals(textBox.DataContext, entry));
            if (editor is null) return;
            editor.Focus();
            var extensionIndex = entry.Type == FileSystemEntryType.File ? entry.Name.LastIndexOf('.') : -1;
            if (extensionIndex > 0)
            {
                editor.SelectionStart = 0;
                editor.SelectionEnd = extensionIndex;
            }
            else editor.SelectAll();
        });
    }

    private async void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileSystemEntryDto entry } editor || ViewModel is not { } vm) return;
        if (e.Key == Key.Escape)
        {
            vm.CancelRename();
            _presentation.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            await CommitRenameFromEditorAsync(vm, entry, editor);
        }
    }

    private async void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileSystemEntryDto entry } editor
            && ViewModel is { IsRenameCommitInProgress: false } vm
            && ReferenceEquals(vm.EditingEntry, entry))
            await CommitRenameFromEditorAsync(vm, entry, editor);
    }

    private async Task CommitRenameFromEditorAsync(ExplorerViewModel vm, FileSystemEntryDto entry, TextBox editor)
    {
        if (!ReferenceEquals(vm.EditingEntry, entry)) return;
        if (await vm.CommitRenameAsync())
        {
            _presentation.Focus();
            return;
        }

        if (ReferenceEquals(vm.EditingEntry, entry))
            Dispatcher.UIThread.Post(() => { editor.Focus(); editor.SelectAll(); });
    }

    private void ClearPendingDrag()
    {
        _dragTrigger = null;
        _dragEntry = null;
        _preservedDragSelection = null;
        _toggleSelectionOnRelease = false;
    }

}
