using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.ViewModels;

public sealed partial class ExplorerViewModel
{
    private bool _archivePromptActive;
    private readonly CancellationTokenSource _archiveLifetime = new();
    public Func<FileArchiveFormat?, bool, CancellationToken, Task<string?>>? RequestArchivePasswordAsync { get; set; }
    public bool CanCompress => !IsPickerMode && !IsBusy && !IsBatchActive && !_archivePromptActive
        && QueueOperationAsync is not null && !string.IsNullOrEmpty(AddressbarPath) && HasSelection
        && GetSelectedEntries().All(entry => entry.Type != FileSystemEntryType.Drive);
    public bool CanExtract => CanCompress && GetSelectedEntries().Count == 1
        && GetSelectedEntries()[0] is { Type: FileSystemEntryType.File } entry
        && FileArchiveFormats.Detect(entry.Name) is not null;

    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressAsync() => SubmitArchiveAsync(false);
    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressTarAsync() => SubmitArchiveAsync(false, FileArchiveFormat.Tar);
    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressTarGZipAsync() => SubmitArchiveAsync(false, FileArchiveFormat.TarGZip);
    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressSevenZipAsync() => SubmitArchiveAsync(false, FileArchiveFormat.SevenZip);
    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressEncryptedZipAsync() => SubmitArchiveAsync(false, FileArchiveFormat.Zip, true);
    [RelayCommand(CanExecute = nameof(CanCompress))]
    private Task CompressEncryptedSevenZipAsync() => SubmitArchiveAsync(false, FileArchiveFormat.SevenZip, true);
    [RelayCommand(CanExecute = nameof(CanExtract))]
    private Task ExtractAsync() => SubmitArchiveAsync(true);

    private async Task SubmitArchiveAsync(bool extract, FileArchiveFormat format = FileArchiveFormat.Zip, bool encrypt = false)
    {
        if (extract ? !CanExtract : !CanCompress) return;
        var selection = NormalizeBatchSelection(GetSelectedEntries()).ToArray();
        var directory = AddressbarPath!;
        var scope = FileListCacheScope?.Invoke();
        var extension = FileArchiveFormats.Extension(format);
        var defaultName = extract ? FileArchiveFormats.Stem(selection[0].Name)
            : (selection.Length == 1 ? selection[0].Name : "Archive") + extension;
        if (string.IsNullOrWhiteSpace(defaultName)) defaultName = "Archive";
        var action = extract ? "extract" : "compress";
        _archivePromptActive = true;
        NotifySelectionCommands();
        try
        {
            var name = await (RequestTextInputAsync?.Invoke(LocalizedText.Get("explorer.archive." + action),
                LocalizedText.Get(extract ? "explorer.archive.extract_prompt" : "explorer.archive.compress_prompt"), defaultName,
                LocalizedText.Get("explorer.archive." + action)) ?? Task.FromResult<string?>(null));
            if (string.IsNullOrWhiteSpace(name) || _disposed || scope != FileListCacheScope?.Invoke()) return;
            if (!ExplorerPath.IsValidName(name, directory) || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl))
                throw new ArgumentException(LocalizedText.Get("explorer.archive.invalid_name"));
            if (!extract && FileArchiveFormats.Detect(name) != format) name += extension;
            var destination = ExplorerPath.Combine(directory, name);
            ArchiveCredentials? credentials = null;
            if (encrypt)
            {
                var password = await (RequestArchivePasswordAsync?.Invoke(format, false, _archiveLifetime.Token) ?? Task.FromResult<string?>(null));
                if (password is null || _disposed || scope != FileListCacheScope?.Invoke()) return;
                credentials = new(password); credentials.ValidateForCreation(format);
            }
            await SubmitOperationAsync(extract ? FileOperationKind.Extract : FileOperationKind.Compress,
                selection.Select(entry => new FileOperationItem(entry.Path, destination)).ToArray(), credentials);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText = LocalizedText.Ref("explorer.archive.failed", ex.Message); }
        finally { _archivePromptActive = false; NotifySelectionCommands(); }
    }
}
