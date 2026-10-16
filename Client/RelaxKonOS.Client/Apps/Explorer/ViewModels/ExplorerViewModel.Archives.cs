using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.ViewModels;

public sealed partial class ExplorerViewModel
{
    private bool _archivePromptActive;
    public bool CanCompress => !IsPickerMode && !IsBusy && !IsBatchActive && !_archivePromptActive
        && QueueOperationAsync is not null && !string.IsNullOrEmpty(AddressbarPath) && HasSelection
        && GetSelectedEntries().All(entry => entry.Type != FileSystemEntryType.Drive);
    public bool CanExtract => CanCompress && GetSelectedEntries().Count == 1
        && GetSelectedEntries()[0] is { Type: FileSystemEntryType.File } entry
        && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressAsync() => SubmitArchiveAsync(false);
    [RelayCommand(CanExecute = nameof(CanExtract))]
    private Task ExtractAsync() => SubmitArchiveAsync(true);

    private async Task SubmitArchiveAsync(bool extract)
    {
        if (extract ? !CanExtract : !CanCompress) return;
        var selection = NormalizeBatchSelection(GetSelectedEntries()).ToArray();
        var directory = AddressbarPath!;
        var scope = FileListCacheScope?.Invoke();
        var defaultName = extract ? selection[0].Name[..^4]
            : selection.Length == 1 ? selection[0].Name + ".zip" : "Archive.zip";
        if (string.IsNullOrWhiteSpace(defaultName)) defaultName = "Archive";
        var action = extract ? "extract" : "compress";
        _archivePromptActive = true;
        NotifySelectionCommands();
        try
        {
            var name = await (RequestTextInputAsync?.Invoke(LocalizedText.Get("explorer.archive." + action),
                LocalizedText.Get("explorer.archive." + action + "_prompt"), defaultName,
                LocalizedText.Get("explorer.archive." + action)) ?? Task.FromResult<string?>(null));
            if (string.IsNullOrWhiteSpace(name) || _disposed || scope != FileListCacheScope?.Invoke()) return;
            if (!ExplorerPath.IsValidName(name, directory) || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl))
                throw new ArgumentException(LocalizedText.Get("explorer.archive.invalid_name"));
            if (!extract && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
            var destination = ExplorerPath.Combine(directory, name);
            await SubmitOperationAsync(extract ? FileOperationKind.Extract : FileOperationKind.Compress,
                selection.Select(entry => new FileOperationItem(entry.Path, destination)).ToArray());
        }
        catch (Exception ex) { StatusText = LocalizedText.Ref("explorer.archive.failed", ex.Message); }
        finally { _archivePromptActive = false; NotifySelectionCommands(); }
    }
}
