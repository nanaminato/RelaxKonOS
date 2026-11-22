using Avalonia.Platform.Storage;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services.ServerCenter;

internal sealed class SshTransferPicker(Func<UsageMemoryScope> captureMemory) : ISshTransferPicker
{
    public Task<IReadOnlyList<IStorageFile>> PickUploadFilesAsync(IStorageProvider storage) =>
        UsageFilePicker.OpenFilePickerAsync(storage, new FilePickerOpenOptions { AllowMultiple = true },
            captureMemory(), "SshFileBrowserView.axaml.0");

    public Task<string?> PickUploadFolderAsync(IStorageProvider storage) =>
        PickFolderAsync(storage, "SshFileBrowserView.axaml.1");

    public Task<IStorageFile?> PickDownloadFileAsync(IStorageProvider storage, string suggestedName) =>
        UsageFilePicker.SaveFilePickerAsync(storage, new FilePickerSaveOptions { SuggestedFileName = suggestedName },
            captureMemory(), "SshFileBrowserView.axaml.2");

    public Task<string?> PickDownloadFolderAsync(IStorageProvider storage) =>
        PickFolderAsync(storage, "SshFileBrowserView.axaml.3");

    private async Task<string?> PickFolderAsync(IStorageProvider storage, string purpose)
    {
        var folders = await UsageFilePicker.OpenFolderPickerAsync(storage, new FolderPickerOpenOptions { AllowMultiple = false },
            captureMemory(), purpose);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
