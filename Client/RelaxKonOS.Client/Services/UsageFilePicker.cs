using Avalonia.Platform.Storage;

namespace RelaxKonOS.Client.Services;

/// <summary>Restores and records confirmed native selections, independently from server paths.</summary>
public static class UsageFilePicker
{
    private static async Task Restore(IStorageProvider storage, PickerOptions options, UsageMemoryScope memory, string purpose)
    {
        if (options.SuggestedStartLocation is not null) return;
        var directory = memory.Directory(purpose, remote: false);
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return;
        try { options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(new Uri(directory)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
    public static async Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(IStorageProvider storage, FilePickerOpenOptions options, UsageMemoryScope memory, string purpose)
    {
        await Restore(storage, options, memory, purpose);
        var files = await storage.OpenFilePickerAsync(options);
        if (!memory.IsCurrent) return [];
        if (files.Count > 0) Remember(memory, purpose, files[0].TryGetLocalPath(), false);
        return files;
    }
    public static async Task<IStorageFile?> SaveFilePickerAsync(IStorageProvider storage, FilePickerSaveOptions options, UsageMemoryScope memory, string purpose)
    {
        await Restore(storage, options, memory, purpose);
        var file = await storage.SaveFilePickerAsync(options);
        if (!memory.IsCurrent) return null;
        if (file is not null) Remember(memory, purpose, file.TryGetLocalPath(), false);
        return file;
    }
    public static async Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(IStorageProvider storage, FolderPickerOpenOptions options, UsageMemoryScope memory, string purpose)
    {
        await Restore(storage, options, memory, purpose);
        var folders = await storage.OpenFolderPickerAsync(options);
        if (!memory.IsCurrent) return [];
        if (folders.Count > 0) Remember(memory, purpose, folders[0].TryGetLocalPath(), true);
        return folders;
    }
    private static void Remember(UsageMemoryScope memory, string purpose, string? path, bool folder)
    {
        if (path is not null) memory.RememberDirectory(purpose, false, folder ? path : Path.GetDirectoryName(path));
    }
}
