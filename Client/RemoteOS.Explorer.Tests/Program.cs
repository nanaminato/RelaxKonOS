using System.Reflection;
using Client.Apps.Explorer;
using Client.Apps.Explorer.Models;
using Client.Apps.Explorer.ViewModels;
using RemoteOS.Protocol.Files;

var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine($"PASS {++passed}: {label}");
}
Check(ExplorerBreadcrumb.ParentPath("/home/alice") == "/home", "POSIX parent");
Check(ExplorerBreadcrumb.ParentPath("/") is null, "POSIX root goes to Computer");
Check(ExplorerBreadcrumb.ParentPath(@"C:\Users\Alice") == @"C:\Users", "Windows parent on any client OS");
Check(ExplorerBreadcrumb.ParentPath(@"C:\") is null, "Drive root goes to Computer");
Check(ExplorerBreadcrumb.ParentPath(@"\\host\share\folder") == @"\\host\share", "UNC share parent");
Check(ExplorerBreadcrumb.ParentPath(@"\\host\share") is null, "UNC share is a root");
Check(ExplorerBreadcrumb.FromPath("/a/back\\slash")[^1].Path == "/a/back\\slash", "POSIX filenames can contain backslashes");

var client = DispatchProxy.Create<IExplorerClient, ExplorerFake>();
var fake = (ExplorerFake)(object)client;
var vm = new ExplorerViewModel(client);
await vm.NavigateToAsync("/a");
Check(vm.AddressbarPath == "/a" && vm.Entries.Count == 2, "Navigation commits loaded directory and hides hidden files");
Check(vm.Entries[0].Type == FileSystemEntryType.Directory, "Folders appear before files");
vm.AddressInput = "/unsubmitted";
Check(vm.AddressbarPath == "/a", "Address editing cannot change file operation destination");
vm.CancelAddressEdit();
Check(vm.AddressInput == "/a", "Escape restores committed address");
await vm.NavigateToAsync("/denied");
Check(vm.AddressbarPath == "/a" && vm.Entries.Count == 2 && !vm.CanGoBack, "Failed navigation preserves listing and history");
await vm.NavigateToAsync("/a");
Check(!vm.CanGoBack, "Repeated location does not duplicate history");
await vm.NavigateToAsync("/b");
fake.Denied = "/a";
await vm.GoBackCommand.ExecuteAsync(null);
Check(vm.AddressbarPath == "/b" && vm.CanGoBack && !vm.CanGoForward, "Failed Back does not move history cursor");
fake.Denied = null;
await vm.GoBackCommand.ExecuteAsync(null);
Check(vm.AddressbarPath == "/a" && vm.CanGoForward, "Back navigates to previous committed folder");
await vm.NavigateToAsync("/c");
Check(!vm.CanGoForward, "New navigation truncates forward history");
vm.ShowHiddenFiles = true;
Check(vm.Entries.Count == 3, "Hidden toggle reveals cached hidden files");
vm.SelectedEntry = vm.Entries.Last();
vm.UpdatePickerSelection([vm.SelectedEntry]);
vm.SearchText = "NOTE";
Check(vm.Entries.Count == 1 && vm.Entries[0].Name == "note.txt" && !vm.HasSelection, "Filtering is case-insensitive and clears stale selection");
await vm.RefreshCommand.ExecuteAsync(null);
Check(vm.SearchText == "NOTE" && vm.Entries.Count == 1, "Refresh preserves current filter");
await vm.NavigateToAsync("/d");
Check(vm.SearchText == "", "Changing location clears filter");
fake.Pending = new TaskCompletionSource<DirectoryDto>();
var loading = vm.NavigateToAsync("/slow");
await vm.NavigateToAsync("/ignored");
Check(vm.IsBusy && vm.AddressbarPath == "/d", "Overlapping navigation cannot replace committed state");
fake.Pending.SetResult(ExplorerFake.Directory("/slow"));
await loading;
Check(!vm.IsBusy && vm.AddressbarPath == "/slow", "Loading ends after successful commit");
vm.SelectedEntry = vm.Entries.First(e => e.Type == FileSystemEntryType.Directory);
await vm.OpenCommand.ExecuteAsync(null);
Check(vm.AddressbarPath == "/slow/folder", "Context Open enters directories");
var folderPicker = new ExplorerViewModel(client, new(Mode: ExplorerPickerMode.SelectFolder), _ => { });
await folderPicker.NavigateToAsync("/a");
Check(folderPicker.Entries.All(e => e.Type != FileSystemEntryType.File), "Folder picker still excludes files");
var picker = new ExplorerViewModel(client, new(Filters: [new("Text", ["*.txt"])]), _ => { });
await picker.NavigateToAsync("/a");
Check(picker.Entries.Count == 2, "File picker retains folders and matching files");
picker.SelectedEntry = picker.Entries.Single(e => e.Type == FileSystemEntryType.File);
picker.SearchText = "missing";
Check(!picker.CanConfirmPicker && picker.PickerEntryName == "", "Filtering clears picker filename and confirmation");
var savePicker = new ExplorerViewModel(client, new(Mode: ExplorerPickerMode.SaveFile, DefaultFileName: "draft.txt"), _ => { });
await savePicker.NavigateToAsync("/a");
savePicker.SearchText = "missing";
Check(savePicker.PickerEntryName == "draft.txt" && savePicker.CanConfirmPicker, "Save picker keeps explicitly entered filename");
Check(ExplorerPath.Combine("C:/Users/Alice", "New folder") == @"C:\Users\Alice\New folder", "Remote Windows combine is independent of client OS");
Check(ExplorerPath.Combine(@"/data/back\slash", @"child\name") == @"/data/back\slash/child\name", "POSIX backslash names survive combining");
Check(ExplorerPath.Equal(@"C:\USERS\Alice\", "c:/users/alice"), "Windows comparison accepts case and separator variants");
Check(!ExplorerPath.Equal("/Data/a", "/data/a"), "POSIX comparison remains case-sensitive");
Check(!ExplorerPath.IsAncestorOrEqual("/data/a", @"/data/a\b"), "POSIX backslash is not an ancestor separator");
Check(ExplorerPath.IsAncestorOrEqual(@"C:\Users", @"c:\users\alice"), "Windows descendant comparison is case-insensitive");
Check(!ExplorerPath.IsAncestorOrEqual("/data/a", "/data/abc"), "Prefix sibling is not a descendant");
Check(ExplorerPath.IsAncestorOrEqual("/", "/data"), "POSIX root ancestor");
Check(!ExplorerPath.IsValidName("../escape", "/data") && !ExplorerPath.IsValidName("NUL.txt", @"C:\data"), "Reject escaping and Windows reserved leaf names");
Check(ExplorerPath.IsValidName(@"back\slash", "/data") && !ExplorerPath.IsValidName(@"back\slash", @"C:\data"), "Filename validation follows remote path flavor");
Check(ExplorerPath.Resolve(@"C:\data", "sub/note.txt") == @"C:\data\sub\note.txt", "Picker relative subpaths use remote separators");
Check(ExplorerEntryComparer.CompareNames("file2.txt", "file10.txt") < 0, "Natural numeric filename order");
Check(ExplorerEntryComparer.CompareNames("file999999999999999999999999", "file1000000000000000000000000") < 0, "Numeric comparison cannot overflow");
var sortEntries = new[]
{
    new FileSystemEntryDto("/sort/file10.txt", "file10.txt", 10, FileSystemEntryType.File, null, DateTimeOffset.UnixEpoch, null, false, false, null),
    new FileSystemEntryDto("/sort/file2.txt", "file2.txt", 2, FileSystemEntryType.File, null, DateTimeOffset.UnixEpoch.AddDays(1), null, false, false, null),
    new FileSystemEntryDto("/sort/z", "z", null, FileSystemEntryType.Directory, null, null, null, false, false, null),
};
foreach (var field in Enum.GetValues<ExplorerSortField>())
{
    foreach (var descending in new[] { false, true })
        Check(sortEntries.OrderBy(e => e, new ExplorerEntryComparer(field, descending)).First().Type == FileSystemEntryType.Directory,
            $"Folders remain first for {field}, descending={descending}");
}
vm.Entries.Clear();
foreach (var entry in sortEntries) vm.Entries.Add(entry);
vm.SelectedEntry = sortEntries[0];
vm.UpdatePickerSelection([sortEntries[0], sortEntries[1]]);
vm.SortField = ExplorerSortField.Size;
Check(vm.Entries[1].Size == 2 && vm.SelectedEntries.Count == 2 && vm.SelectedEntry == sortEntries[0], "Sorting preserves selected entries");
vm.SortBy(ExplorerSortField.Size);
Check(vm.SortDescending && vm.Entries[1].Size == 10, "Clicking same header reverses value order");
vm.SortBy(ExplorerSortField.Name);
Check(!vm.SortDescending && vm.Entries[1].Name == "file2.txt", "New sort column starts ascending with natural order");
ExplorerViewPreferences? savedView = null;
vm.SaveViewPreferencesAsync = preferences => { savedView = preferences; return Task.CompletedTask; };
vm.IsCompactView = true;
await vm.SaveDefaultViewCommand.ExecuteAsync(null);
Check(savedView is { IsCompactView: true, SortField: ExplorerSortField.Name }, "Default view command captures current preferences");
var restored = new ExplorerViewModel(client);
restored.ApplyViewPreferences(savedView!);
Check(restored.IsCompactView && restored.SortField == ExplorerSortField.Name, "Saved view restores on a new view model");
restored.ApplyViewPreferences(new((ExplorerSortField)999));
Check(restored.SortField == ExplorerSortField.Name, "Unknown persisted sort field falls back to Name");
vm.SaveViewPreferencesAsync = _ => throw new IOException("Conflict");
await vm.SaveDefaultViewCommand.ExecuteAsync(null);
Check(vm.StatusText.Contains("view_save_failed"), "Preference save failure is visible");

await vm.NavigateToAsync(@"C:\data");
vm.RequestTextInputAsync = (_, _, _, _) => Task.FromResult<string?>("New folder");
await vm.NewFolderCommand.ExecuteAsync(null);
Check(fake.CreatedPath == @"C:\data\New folder", "New folder API receives a Windows path on any client OS");
vm.RequestTextInputAsync = (_, _, _, _) => Task.FromResult<string?>("../escape");
fake.CreatedPath = null;
await vm.NewFolderCommand.ExecuteAsync(null);
Check(fake.CreatedPath is null, "New folder rejects path traversal before calling API");
var windowsEntry = sortEntries[2] with { Path = @"C:\data\folder", Name = "folder" };
Check(!vm.CanMoveEntryToDirectory(windowsEntry, @"c:\DATA\FOLDER\child"), "Drag validation rejects case-varied Windows descendants");
vm.SelectedEntry = windowsEntry;
vm.UpdatePickerSelection([windowsEntry]);
vm.RequestConfirmAsync = (_, _, _) => Task.FromResult(true);
string? elevatedDirectory = null;
fake.RequireElevation = true;
vm.RequestFileOperationElevationAsync = (paths, _) => { elevatedDirectory = paths.Single(); return Task.FromResult(true); };
await vm.DeleteCommand.ExecuteAsync(null);
Check(elevatedDirectory == @"C:\data", "Delete elevation uses remote parent directory");
var windowsPicker = new ExplorerViewModel(client, new(Filters: [new("Uppercase", ["*.TXT"])]), _ => { });
await windowsPicker.NavigateToAsync(@"C:\data");
Check(windowsPicker.Entries.Any(e => e.Name == "note.txt"), "Windows picker filters use remote case-insensitive matching");
await windowsPicker.NavigateToAsync("/data");
Check(windowsPicker.Entries.All(e => e.Type != FileSystemEntryType.File), "POSIX picker filters use remote case-sensitive matching");
Check(ExplorerPath.Resolve(@"C:\data\child", @"\root") == @"C:\root", "Windows root-relative picker path");
await vm.NavigateToAsync(@"C:\data");
await vm.AddressbarGoAsync("sub/folder");
Check(vm.AddressbarPath == @"C:\data\sub\folder", "Relative address resolves against current remote directory");
await vm.AddressbarGoAsync("D:");
Check(vm.AddressbarPath == @"D:\", "Bare drive in address bar navigates to drive root");
await ExplorerBatchChecks.RunAsync(Check);
Console.WriteLine($"{passed} Explorer regression checks passed.");



public class ExplorerFake : DispatchProxy
{
    public string? Denied { get; set; }
    public string? CreatedPath { get; set; }
    public bool RequireElevation { get; set; }
    public TaskCompletionSource<DirectoryDto>? Pending { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IExplorerClient.GetDirectoryAsync))
        {
            var path = (string)args![0]!;
            if (path == "/denied" || path == Denied) return Task.FromException<DirectoryDto>(new IOException("Denied"));
            if (path == "/slow" && Pending is not null) return Pending.Task;
            return Task.FromResult(Directory(path));
        }
        if (method.Name == nameof(IExplorerClient.CreateDirectoryAsync))
        {
            CreatedPath = (string)args![0]!;
            return Task.FromResult(new FileSystemEntryDto(CreatedPath, "New folder", null, FileSystemEntryType.Directory, null, null, null, false, false, null));
        }
        if (method.Name == nameof(IExplorerClient.DeleteAsync))
        {
            if (RequireElevation)
            {
                RequireElevation = false;
                return Task.FromException(new Client.Services.Auth.RemoteOsAuthException(
                    new RemoteOS.Protocol.Common.ProblemDetails("test/elevation-required", "Elevation", 403, "Denied", null)));
            }
            return Task.CompletedTask;
        }
        throw new NotSupportedException(method.Name);
    }
    public static DirectoryDto Directory(string path) => new(path, path, FileSystemEntryType.Directory,
        [new(path + "/folder", "folder", null, FileSystemEntryType.Directory, null, null, null, false, false, null)],
        [new(path + "/note.txt", "note.txt", ".txt", 100, null, null, null, false, false, "text/plain"),
         new(path + "/.secret", ".secret", null, 20, null, null, null, true, false, "text/plain")], null, null);
}
