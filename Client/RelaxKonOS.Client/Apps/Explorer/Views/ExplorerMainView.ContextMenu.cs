using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

public partial class ExplorerMainView
{
    private MenuItem? _saveToHostMenuItem;
    private MenuItem? _compressMenuItem;
    private MenuItem? _extractMenuItem;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_entryContextMenu is null || _saveToHostMenuItem is not null) return;

        _saveToHostMenuItem = new MenuItem
        {
            Header = LocalizedText.Get("explorer.operations.download"),
        };

        // Keep transfer actions together: Copy/Cut/Paste, then the explicit local-save escape hatch.
        var items = _entryContextMenu.Items;
        var insertAt = items.OfType<MenuItem>().Select((item, index) => (item, index))
            .FirstOrDefault(pair => ReferenceEquals(pair.item.Command, ViewModel?.RenameCommand)).index;
        if (insertAt <= 0) insertAt = Math.Max(0, items.Count - 2);
        items.Insert(insertAt, _saveToHostMenuItem);
        _compressMenuItem = new MenuItem { Header = LocalizedText.Get("explorer.archive.compress") };
        _extractMenuItem = new MenuItem { Header = LocalizedText.Get("explorer.archive.extract") };
        items.Insert(insertAt + 1, _compressMenuItem);
        items.Insert(insertAt + 2, _extractMenuItem);
        _entryContextMenu.Opened += EntryContextMenu_Opened;
    }

    private void EntryContextMenu_Opened(object? sender, EventArgs e)
    {
        if (ViewModel is not { } vm || _entryContextMenu is null || _saveToHostMenuItem is null) return;

        var selected = vm.SelectedEntries.Count;
        var single = selected == 1 && vm.SelectedEntry is not null;
        var singleFile = single && vm.SelectedEntry!.Type == FileSystemEntryType.File;
        var singleFolder = single && vm.SelectedEntry!.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive;

        foreach (var item in _entryContextMenu.Items.OfType<MenuItem>())
        {
            if (ReferenceEquals(item.Command, vm.OpenCommand)) item.IsVisible = single;
            else if (ReferenceEquals(item.Command, vm.OpenFolderInTabCommand)) item.IsVisible = singleFolder && !vm.IsPickerMode;
            else if (ReferenceEquals(item.Command, vm.OpenWithSelectedCommand)) item.IsVisible = singleFile;
            else if (ReferenceEquals(item.Command, vm.OpenTerminalCommand)) item.IsVisible = singleFolder;
            else if (ReferenceEquals(item.Command, vm.RenameCommand)) item.IsVisible = single && vm.CanRenameSelection;
            else if (ReferenceEquals(item.Command, vm.PropertiesCommand)) item.IsVisible = single;
        }

        if (_compressMenuItem is not null && _extractMenuItem is not null)
        {
            _compressMenuItem.Command = vm.CompressCommand;
            _compressMenuItem.IsVisible = !vm.IsPickerMode && vm.QueueOperationAsync is not null && selected > 0;
            _compressMenuItem.IsEnabled = vm.CanCompress;
            _extractMenuItem.Command = vm.ExtractCommand;
            _extractMenuItem.IsVisible = singleFile && vm.SelectedEntry!.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !vm.IsPickerMode && vm.QueueOperationAsync is not null;
            _extractMenuItem.IsEnabled = vm.CanExtract;
        }
        _saveToHostMenuItem.Command = vm.SaveToHostCommand;
        _saveToHostMenuItem.IsVisible = !vm.IsPickerMode && selected > 0;
        _saveToHostMenuItem.IsEnabled = vm.CanSaveToHost;
    }
}
