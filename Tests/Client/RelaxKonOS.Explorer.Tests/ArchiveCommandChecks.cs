using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Protocol.Files;

public static class ArchiveCommandChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var client = DispatchProxy.Create<IExplorerClient, OperationClientFake>();
        using var vm = new ExplorerViewModel(client);
        var requests = new List<StartFileOperationRequest>();
        vm.QueueOperationAsync = (request, _) => { requests.Add(request); return Task.CompletedTask; };
        var scope = "session";
        vm.FileListCacheScope = () => scope;
        await vm.NavigateToAsync("/source");
        var first = new FileSystemEntryDto("/source/first.txt", "first.txt", 4, FileSystemEntryType.File, null, null, null, false, false, null);
        var second = first with { Path = "/source/second.txt", Name = "second.txt" };
        vm.SelectedEntry = first; vm.UpdatePickerSelection([first, second]);
        check(vm.CanCompress && !vm.CanExtract, "Multiple files enable ZIP compression only");
        vm.RequestTextInputAsync = async (_, _, _, _) =>
        {
            vm.SelectedEntry = second; vm.UpdatePickerSelection([second]);
            await vm.NavigateToAsync("/other");
            return "bundle";
        };
        await vm.CompressCommand.ExecuteAsync(null);
        check(requests.Count == 1 && requests[0].Kind == FileOperationKind.Compress
            && requests[0].Items.Select(item => item.SourcePath).SequenceEqual(new[] { first.Path, second.Path })
            && requests[0].Items.All(item => item.DestinationPath == "/source/bundle.zip"),
            "ZIP prompt freezes source selection and destination before navigation");
        var archive = first with { Name = "bundle.ZIP", Path = "/source/bundle.ZIP" };
        await vm.NavigateToAsync("/source"); vm.SelectedEntry = archive; vm.UpdatePickerSelection([archive]);
        check(vm.CanExtract, "Single ZIP selection enables extraction case-insensitively");
        vm.RequestTextInputAsync = (_, _, name, _) => Task.FromResult<string?>(name);
        await vm.ExtractCommand.ExecuteAsync(null);
        check(requests.Count == 2 && requests[1].Kind == FileOperationKind.Extract && requests[1].Items.Single().DestinationPath == "/source/bundle",
            "Extraction submits one archive into a named new directory");
        vm.RequestTextInputAsync = (_, _, _, _) => { scope = "new-session"; return Task.FromResult<string?>("stale"); };
        await vm.ExtractCommand.ExecuteAsync(null);
        check(requests.Count == 2, "Session change while prompting cannot submit stale archive work");
        vm.RequestTextInputAsync = (_, _, _, _) => Task.FromResult<string?>("../escape");
        await vm.ExtractCommand.ExecuteAsync(null);
        check(requests.Count == 2, "Archive prompt rejects path traversal before submission");
        var expectedCount = requests.Count;
        foreach (var suffix in new[] { ".tar", ".tar.gz", ".TGZ", ".7z", ".rar", ".iso", ".cab", ".xz", ".bz2", ".zst", ".wim" })
        {
            var item = archive with { Name = "bundle" + suffix, Path = "/source/bundle" + suffix };
            vm.SelectedEntry = item; vm.UpdatePickerSelection([item]);
            check(vm.CanExtract, suffix + " enables extraction");
            vm.RequestTextInputAsync = (_, _, name, _) => Task.FromResult<string?>(name);
            await vm.ExtractCommand.ExecuteAsync(null);
            check(requests[++expectedCount - 1].Items.Single().DestinationPath == "/source/bundle", suffix + " uses its full archive suffix for the output folder");
        }
        vm.RequestTextInputAsync = (_, _, name, _) => Task.FromResult<string?>(name);
        await vm.CompressTarCommand.ExecuteAsync(null);
        check(requests.Last().Items.Single().DestinationPath!.EndsWith(".tar", StringComparison.Ordinal), "TAR compression command selects TAR output");
        await vm.CompressTarGZipCommand.ExecuteAsync(null);
        check(requests.Last().Items.Single().DestinationPath!.EndsWith(".tar.gz", StringComparison.Ordinal), "TAR.GZ compression command selects compressed TAR output");
        vm.QueueOperationAsync = null;
        check(!vm.CanCompress && !vm.CanExtract, "Archive commands require the shared operation service");
    }
}
