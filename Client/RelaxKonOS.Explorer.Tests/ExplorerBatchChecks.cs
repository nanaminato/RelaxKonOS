using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;

public static class ExplorerBatchChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        static FileSystemEntryDto FileEntry(string path, FileSystemEntryType type = FileSystemEntryType.File)
            => new(path, path[(path.LastIndexOf('/') + 1)..], 10, type, null, null, null, false, false, null);
        var files = new[] { FileEntry("/source/a.txt"), FileEntry("/source/b.txt"), FileEntry("/source/c.txt") };
        var client = DispatchProxy.Create<IExplorerClient, BatchClientFake>();
        var fake = (BatchClientFake)(object)client;
        var clipboard = new RemoteFileClipboard();
        var vm = new ExplorerViewModel(client, fileClipboard: clipboard);
        var peer = new ExplorerViewModel(client, fileClipboard: clipboard);
        await vm.NavigateToAsync("/source");
        await peer.NavigateToAsync("/source");
        void Select(params FileSystemEntryDto[] entries)
        {
            vm.SelectedEntry = entries.FirstOrDefault();
            vm.UpdatePickerSelection(entries);
        }
        Select(files);
        vm.CutCommand.Execute(null);
        check(vm.CutEntryPaths.Count == 3 && peer.CutEntryPaths.Count == 3,
            "Cut clipboard state is reflected in every open Explorer view model");
        peer.SelectedEntry = files[0];
        peer.UpdatePickerSelection([files[0]]);
        peer.CopyCommand.Execute(null);
        check(vm.CutEntryPaths.Count == 0 && peer.CutEntryPaths.Count == 0,
            "Replacing a cut with a copy clears faded entries across windows");
        check(vm.GetDragEntries(files[1]).Count == 3, "Drag snapshot includes whole selection when pressed row is selected");
        check(vm.GetDragEntries(FileEntry("/source/other")).Count == 1, "Dragging an unselected row excludes old selection");
        check(!vm.RenameCommand.CanExecute(null) && !vm.MoveCommand.CanExecute(null) && !vm.PropertiesCommand.CanExecute(null),
            "Single-item commands are disabled for multiple selection");
        var confirmations = 0;
        vm.RequestConfirmAsync = (_, message, _) =>
        {
            confirmations++;
            check(message.Contains("a.txt") && message.Contains("c.txt"), "Delete confirmation previews selected names");
            Select(FileEntry("/source/unrelated"));
            return Task.FromResult(true);
        };
        await vm.DeleteCommand.ExecuteAsync(null);
        check(confirmations == 1 && fake.Calls.Select(c => c.Path).SequenceEqual(files.Select(f => f.Path)),
            "Delete executes confirmed selection snapshot exactly once per item");
        check(!vm.IsBusy && !vm.IsBatchActive && !vm.HasTransferProgress, "Batch clears busy and progress state");
        Select(files);
        fake.Reset();
        vm.RequestConfirmAsync = (_, _, _) => Task.FromResult(false);
        await vm.DeleteCommand.ExecuteAsync(null);
        check(fake.Calls.Count == 0 && !vm.IsBusy, "Cancelled confirmation makes no delete requests");
        Select(FileEntry("/", FileSystemEntryType.Drive), files[0]);
        check(!vm.DeleteCommand.CanExecute(null), "Mixed drive selection cannot be deleted");
        await vm.DeleteCommand.ExecuteAsync(null);
        check(fake.Calls.Count == 0, "Delete guard also protects direct command execution");

        fake.Reset();
        fake.FailPaths.Add(files[1].Path);
        var result = await vm.TransferEntriesToDirectoryAsync(files, "/destination", copy: true);
        check(result is { Completed.Count: 2, Failures.Count: 1, NotStarted: 0 }, "Copy batch reports partial failures and continues remaining items");
        check(fake.Calls.All(c => c.Action == nameof(IExplorerClient.CopyAsync) && !c.Overwrite)
            && fake.Calls[2].Destination == "/destination/c.txt", "Batch copy uses Copy API with overwrite disabled");
        check(vm.LastOperationDetails.Contains(files[1].Path) && vm.StatusText.Contains("batch.result"), "Partial failure details survive directory refresh");
        fake.Reset();
        var gate = new TaskCompletionSource();
        fake.BeforeMutation = _ => gate.Task;
        var pending = vm.TransferEntriesToDirectoryAsync(files, "/destination", copy: false);
        check(vm.IsBatchActive && vm.StopBatchCommand.CanExecute(null), "Stop is available while current item is in flight");
        await vm.NavigateToAsync("/unrelated");
        check(vm.AddressbarPath == "/source", "Navigation is rejected while a batch holds the current view");
        check(await vm.TransferEntriesToDirectoryAsync(files, "/other", copy: false) is null, "Overlapping batch is rejected");
        vm.StopBatchCommand.Execute(null);
        check(!vm.StopBatchCommand.CanExecute(null) && fake.Calls.Count == 1, "Stop request neither repeats nor interrupts active mutation");
        gate.SetResult();
        result = await pending;
        check(result is { Completed.Count: 1, NotStarted: 2 } && fake.Calls.Count == 1, "Stop prevents subsequent requests after current item completes");
        check(vm.LastOperationDetails.Contains(files[2].Path), "Unstarted items are listed for follow-up");
        fake.Reset();
        fake.RequireElevation = true;
        vm.RequestFileOperationElevationAsync = (_, _) => Task.FromResult(false);
        result = await vm.TransferEntriesToDirectoryAsync(files, "/destination", copy: false);
        check(result is { Failures.Count: 1, NotStarted: 2 } && fake.Calls.Count == 1, "Declined elevation stops remaining items without repeated prompts");

        fake.Reset();
        clipboard.Set(files, RemoteFileClipboardOperation.Cut);
        fake.FailPaths.Add(files[1].Path);
        await vm.NavigateToAsync("/destination");
        await vm.PasteCommand.ExecuteAsync(null);
        check(clipboard.Entries.Count == 1 && clipboard.Entries[0].Path == files[1].Path,
            "Partial cut paste removes successful items and retains failures for retry");
        check(vm.CutEntryPaths.SequenceEqual([files[1].Path]) && peer.CutEntryPaths.SequenceEqual([files[1].Path]),
            "Partial cut paste keeps only failed items faded across windows");
        fake.Reset();
        clipboard.Set(files, RemoteFileClipboardOperation.Cut);
        var newClipboard = new[] { FileEntry("/other/new.txt") };
        fake.BeforeMutation = _ => { clipboard.Set(newClipboard, RemoteFileClipboardOperation.Copy); return Task.CompletedTask; };
        await vm.PasteCommand.ExecuteAsync(null);
        check(clipboard.Operation == RemoteFileClipboardOperation.Copy && clipboard.Entries[0].Path == newClipboard[0].Path,
            "A newer shared clipboard is not overwritten when an older batch completes");
        check(fake.Calls.All(c => c.Action == nameof(IExplorerClient.MoveAsync)), "Clipboard changes cannot change operation type mid-batch");
        fake.Reset();
        clipboard.Set(files, RemoteFileClipboardOperation.Copy);
        await vm.PasteCommand.ExecuteAsync(null);
        check(clipboard.Entries.Count == 3 && clipboard.Operation == RemoteFileClipboardOperation.Copy, "Successful copy leaves reusable clipboard intact");
        fake.Reset();
        check(!vm.CanTransferEntriesToDirectory(files, "/source"), "Drop into current parent is rejected");
        check(vm.CanTransferEntriesToDirectory(files, "/source", copy: true), "Same-directory copy is accepted");
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A (2).TXT" };
        check(ExplorerPath.ReserveCopyName("a.txt", false, occupied) == "a (3).txt",
            "Windows sibling names skip case-insensitive collisions");
        check(ExplorerPath.ReserveCopyName("a.txt", false, occupied) == "a (4).txt",
            "Generated names are reserved for subsequent batch entries");
        check(ExplorerPath.ReserveCopyName(".env", false, occupied) == ".env (2)", "Dotfiles keep their complete name");
        check(ExplorerPath.ReserveCopyName("folder.ext", true, occupied) == "folder.ext (2)", "Folder dots are not extensions");
        fake.DirectoryEntries = [FileEntry("/source/a (2).txt", FileSystemEntryType.Directory)];
        result = await vm.TransferEntriesToDirectoryAsync(files, "/source", copy: true);
        check(result is { Completed.Count: 3 } && fake.Calls[0].Destination == "/source/a (3).txt"
            && fake.Calls[1].Destination == "/source/b (2).txt" && fake.Calls.All(c => !c.Overwrite),
            "Same-directory copy uses remote occupied names and preserves extensions without overwrite");
        fake.Reset();
        fake.FailRefresh = true;
        result = await vm.TransferEntriesToDirectoryAsync(files, "/source", copy: true);
        check(result is { Failures.Count: 3 } && fake.Calls.Count == 0 && !vm.IsBusy,
            "Failed destination listing prevents copy mutation and releases busy state");
        fake.Reset();
        check(!vm.CanTransferEntriesToDirectory([files[0], FileEntry("/other/a.txt")], "/destination"), "Duplicate destination names reject whole batch before mutation");
        var folder = FileEntry("/source/folder", FileSystemEntryType.Directory);
        check(!vm.CanTransferEntriesToDirectory([folder, files[0]], "/source/folder/child"), "One invalid descendant target rejects whole mixed batch");
        result = await vm.TransferEntriesToDirectoryAsync([folder, FileEntry("/source/folder/child.txt"), folder], "/destination", copy: false);
        check(result is { Total: 1, Completed.Count: 1 } && fake.Calls.Count == 1, "Recursive parent selection deduplicates descendants and duplicate paths");
        fake.Reset();
        clipboard.Set(files, RemoteFileClipboardOperation.Cut);
        fake.BeforeMutation = _ => { vm.StopBatchCommand.Execute(null); return Task.CompletedTask; };
        await vm.PasteCommand.ExecuteAsync(null);
        check(clipboard.Entries.Select(e => e.Path).SequenceEqual(files.Skip(1).Select(e => e.Path)),
            "Stopped cut paste retains every unstarted clipboard item");
        fake.Reset();
        Select(files);
        vm.RequestConfirmAsync = (_, _, _) => Task.FromResult(true);
        fake.FailPaths.Add(files[1].Path);
        await vm.DeleteCommand.ExecuteAsync(null);
        check(fake.Calls.Count == 3 && vm.LastOperationDetails.Contains(files[1].Path), "Delete continues after individual failures and retains failure details");
        IReadOnlyList<string>? picked = null;
        var picker = new ExplorerViewModel(client, new(AllowMultiple: true), paths => picked = paths);
        await picker.NavigateToAsync("/source");
        picker.SelectedEntry = files[0];
        picker.UpdatePickerSelection(files);
        check(picker.OpenCommand.CanExecute(null), "Multi-file picker keeps Open confirmation available");
        await picker.OpenCommand.ExecuteAsync(null);
        check(picked?.Count == 3, "Multi-file picker confirms all selected files");
        fake.Reset();
        fake.FailRefresh = true;
        result = await vm.TransferEntriesToDirectoryAsync([files[0]], "/destination", copy: true);
        check(result is { Completed.Count: 1 } && vm.LastOperationDetails.Contains("load_failed"), "Refresh failure does not hide successful mutation or its refresh error");
    }
}

public class BatchClientFake : DispatchProxy
{
    public record Call(string Action, string Path, string? Destination, bool Overwrite);
    public List<Call> Calls { get; } = [];
    public HashSet<string> FailPaths { get; } = [];
    public Func<string, Task>? BeforeMutation { get; set; }
    public bool RequireElevation { get; set; }
    public bool FailRefresh { get; set; }
    public IReadOnlyList<FileSystemEntryDto> DirectoryEntries { get; set; } = [];
    public void Reset() { Calls.Clear(); FailPaths.Clear(); BeforeMutation = null; RequireElevation = false; FailRefresh = false; DirectoryEntries = []; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var path = (string)args![0]!;
        if (method!.Name == nameof(IExplorerClient.GetDirectoryAsync))
            return FailRefresh ? Task.FromException<DirectoryDto>(new IOException("Refresh failed"))
                : Task.FromResult(ExplorerFake.Directory(path) with { Directories = DirectoryEntries });
        if (method.Name is nameof(IExplorerClient.CopyAsync) or nameof(IExplorerClient.MoveAsync) or nameof(IExplorerClient.DeleteAsync))
        {
            Calls.Add(new(method.Name, path, method.Name == nameof(IExplorerClient.DeleteAsync) ? null : (string)args[1]!,
                method.Name != nameof(IExplorerClient.DeleteAsync) && (bool)args[2]!));
            return MutateAsync(path);
        }
        throw new NotSupportedException(method.Name);
    }
    private async Task<FileSystemEntryDto> MutateAsync(string path)
    {
        if (BeforeMutation is not null) await BeforeMutation(path);
        if (RequireElevation) throw new RemoteOsAuthException(new ProblemDetails("test/elevation-required", "Elevation", 403, "Denied", null));
        if (FailPaths.Contains(path)) throw new IOException("Simulated conflict");
        return new(path, path, 0, FileSystemEntryType.File, null, null, null, false, false, null);
    }
}
