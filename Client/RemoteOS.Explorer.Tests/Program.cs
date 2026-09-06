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
Console.WriteLine($"{passed} Explorer regression checks passed.");

public class ExplorerFake : DispatchProxy
{
    public string? Denied { get; set; }
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
        throw new NotSupportedException(method.Name);
    }
    public static DirectoryDto Directory(string path) => new(path, path, FileSystemEntryType.Directory,
        [new(path + "/folder", "folder", null, FileSystemEntryType.Directory, null, null, null, false, false, null)],
        [new(path + "/note.txt", "note.txt", ".txt", 100, null, null, null, false, false, "text/plain"),
         new(path + "/.secret", ".secret", null, 20, null, null, null, true, false, "text/plain")], null, null);
}
