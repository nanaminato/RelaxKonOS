using Avalonia.Platform.Storage;

namespace RelaxKonOS.AppSDK;

public interface ISshTransferPicker
{
    Task<IReadOnlyList<IStorageFile>> PickUploadFilesAsync(IStorageProvider storage);
    Task<string?> PickUploadFolderAsync(IStorageProvider storage);
    Task<IStorageFile?> PickDownloadFileAsync(IStorageProvider storage, string suggestedName);
    Task<string?> PickDownloadFolderAsync(IStorageProvider storage);
}
