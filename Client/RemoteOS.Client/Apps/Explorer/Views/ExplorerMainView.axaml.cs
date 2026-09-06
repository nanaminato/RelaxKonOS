using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Client.Apps.Explorer.Models;
using Client.Apps.Explorer.ViewModels;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.Views;

public partial class ExplorerMainView : UserControl
{
    private static readonly DataFormat<FileSystemEntryDto> ExplorerEntryFormat =
        DataFormat.CreateInProcessFormat<FileSystemEntryDto>("remoteos/explorer-entry");
    private const double MinimumDragDistance = 5;

    private PointerPressedEventArgs? _dragTrigger;
    private FileSystemEntryDto? _dragEntry;
    private Point _dragStart;
    private readonly ContextMenu? _entryContextMenu;

    public ExplorerMainView()
    {
        InitializeComponent();
        _entryContextMenu = EntriesGrid.ContextMenu;
        EntriesGrid.AddHandler(PointerPressedEvent, EntriesGrid_PointerPressed, RoutingStrategies.Tunnel);
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
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && vm.SelectedEntry is { } entry)
        {
            _ = vm.InvokeEntryAsync(entry);
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
        if (FindDataContext<FileSystemEntryDto>(e.Source) is { } entry && ViewModel is { IsBusy: false } vm)
            _ = vm.InvokeEntryAsync(entry);
    }

    private void EntriesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
            ViewModel?.UpdatePickerSelection(grid.SelectedItems?.Cast<object>() ?? []);
    }

    private void EntriesGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
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
        ClearPendingDrag();

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ExplorerEntryFormat, entry));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
    }

    private void EntriesGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => ClearPendingDrag();

    private void Explorer_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetDrop(e, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Explorer_Drop(object? sender, DragEventArgs e)
    {
        if (!TryGetDrop(e, out var entry, out var targetDirectory))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        if (ViewModel is not null)
            await ViewModel.MoveEntryToDirectoryAsync(entry, targetDirectory);
    }

    private bool TryGetDrop(DragEventArgs e, out FileSystemEntryDto entry, out string targetDirectory)
    {
        entry = e.DataTransfer.TryGetValue(ExplorerEntryFormat)!;
        targetDirectory = FindDropTargetPath(e.Source) ?? string.Empty;
        return entry is not null &&
               !string.IsNullOrWhiteSpace(targetDirectory) &&
               ViewModel?.CanMoveEntryToDirectory(entry, targetDirectory) == true;
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

    private void ClearPendingDrag()
    {
        _dragTrigger = null;
        _dragEntry = null;
    }

}
