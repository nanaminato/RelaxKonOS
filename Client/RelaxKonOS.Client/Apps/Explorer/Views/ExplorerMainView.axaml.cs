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
    private sealed record ExplorerDragPayload(ExplorerViewModel Source, IReadOnlyList<FileSystemEntryDto> Entries);
    private static readonly DataFormat<ExplorerDragPayload> ExplorerEntriesFormat =
        DataFormat.CreateInProcessFormat<ExplorerDragPayload>("remoteos/explorer-entries");
    private const double MinimumDragDistance = 5;

    private PointerPressedEventArgs? _dragTrigger;
    private FileSystemEntryDto? _dragEntry;
    private IReadOnlyList<FileSystemEntryDto>? _preservedDragSelection;
    private bool _toggleSelectionOnRelease;
    private Point _dragStart;
    private readonly ContextMenu? _entryContextMenu;
    private ExplorerViewModel? _attachedViewModel;

    public ExplorerMainView()
    {
        InitializeComponent();
        _entryContextMenu = EntriesGrid.ContextMenu;
        EntriesGrid.AddHandler(PointerPressedEvent, EntriesGrid_PointerPressed, RoutingStrategies.Tunnel);
        DataContextChanged += ExplorerMainView_DataContextChanged;
    }

    private void ExplorerMainView_DataContextChanged(object? sender, EventArgs e)
    {
        if (_attachedViewModel is not null) _attachedViewModel.RequestRenameFocus = null;
        _attachedViewModel = ViewModel;
        if (_attachedViewModel is not null) _attachedViewModel.RequestRenameFocus = FocusRenameEditor;
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

    public void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
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
        EntriesGrid.Focus();
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
            EntriesGrid.Focus();
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

    private void EntriesGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true; // One folder-first ordering for column headers and the command bar.
        if (ViewModel is not { IsBusy: false } vm) return;
        if (Enum.TryParse<ExplorerSortField>(e.Column.SortMemberPath, out var field)) vm.SortBy(field);
    }

    private void EntriesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
            ViewModel?.UpdatePickerSelection(grid.SelectedItems?.Cast<object>() ?? []);
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
            if (entry is null) EntriesGrid.SelectedItems.Clear();
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
            EntriesGrid.Focus();
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
                EntriesGrid.SelectedItems.Clear();
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
            if (ReferenceEquals(control, EntriesGrid) || ReferenceEquals(control, EntriesScrollViewer))
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
        EntriesGrid.ScrollIntoView(entry, null);
        Dispatcher.UIThread.Post(() =>
        {
            var editor = EntriesGrid.GetVisualDescendants().OfType<TextBox>()
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
            EntriesGrid.Focus();
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
            && ViewModel is { } vm && ReferenceEquals(vm.EditingEntry, entry))
            await CommitRenameFromEditorAsync(vm, entry, editor);
    }

    private async Task CommitRenameFromEditorAsync(ExplorerViewModel vm, FileSystemEntryDto entry, TextBox editor)
    {
        if (!ReferenceEquals(vm.EditingEntry, entry)) return;
        if (await vm.CommitRenameAsync())
        {
            EntriesGrid.Focus();
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
