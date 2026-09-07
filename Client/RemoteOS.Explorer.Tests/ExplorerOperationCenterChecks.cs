using System.Reflection;
using Client.Apps.Explorer;
using Client.Apps.Explorer.Models;
using Client.Apps.Explorer.ViewModels;
using RemoteOS.Protocol.Files;

public static class ExplorerOperationCenterChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var client = DispatchProxy.Create<IExplorerClient, OperationClientFake>();
        var fake = (OperationClientFake)(object)client;
        var center = new ExplorerOperationCenter(client);
        var clipboard = new RemoteFileClipboard();
        var vm = new ExplorerViewModel(client, fileClipboard: clipboard) { QueueOperationAsync = center.SubmitAsync };
        var file = new FileSystemEntryDto("/source/a.txt", "a.txt", 4, FileSystemEntryType.File, null, null, null, false, false, null);
        var shown = 0;
        var closed = 0;
        center.CloseRequested = () => closed++;
        center.Completed += vm.RefreshAfterOperation;
        center.ShowRequested = () => shown++;
        await vm.NavigateToAsync("/source");
        vm.SelectedEntry = file;
        vm.UpdatePickerSelection([file]);
        vm.CutCommand.Execute(null);
        await vm.NavigateToAsync("/target");
        await vm.PasteCommand.ExecuteAsync(null);
        check(center.Jobs.Count == 1 && shown == 1 && !vm.IsBusy && !vm.PasteCommand.IsRunning,
            "Background paste opens operation center and releases Explorer immediately");
        check(fake.Requests[0].Kind == FileOperationKind.Move && fake.Requests[0].Items[0].DestinationPath == "/target/a.txt",
            "Background job captures the committed source and destination");
        vm.SelectedEntry = file;
        vm.UpdatePickerSelection([file]);
        vm.RequestConfirmAsync = (_, _, _) => Task.FromResult(true);
        await vm.DeleteCommand.ExecuteAsync(null);
        check(center.Jobs.Count == 2 && fake.Requests[1].Kind == FileOperationKind.Delete,
            "Another operation can be submitted while the first remains active");
        await vm.NavigateToAsync("/next");
        check(vm.AddressbarPath == "/next" && !vm.IsBusy, "Navigation remains available during background operations");
        var deleteId = center.Jobs[1].Snapshot.Id;
        fake.Finish(center.Jobs[0].Snapshot.Id, [file.Path]);
        await Eventually(() => clipboard.Entries.Count == 0);
        check(clipboard.Entries.Count == 0, "Completed background moves consume only successful cut sources");
        check(center.Jobs.Count == 1 && closed == 0, "Completed cards clear without closing active operations");
        fake.Finish(deleteId, []);
        await Eventually(() => center.Jobs.Count == 0);
        check(closed > 0, "Window closes automatically when all operations finish");
        await center.RestoreAsync();
        check(center.Jobs.Count == 0, "Restoring does not resurrect automatically cleared operations");
        center.ClearCompletedCommand.Execute(null);
        check(center.Jobs.Count == 0, "Finished cards can be cleared");

        await vm.NavigateToAsync("/target");
        var reads = fake.DirectoryReads;
        vm.IsBusy = true;
        vm.RefreshAfterOperation(fake.Jobs[deleteId] with { Items = [new("/target/a.txt")] });
        check(fake.DirectoryReads == reads, "Completion waits while Explorer is busy");
        vm.IsBusy = false;
        await Eventually(() => fake.DirectoryReads > reads);
        check(fake.DirectoryReads == reads + 1, "Completion refreshes the affected folder after Explorer becomes idle");

        var callbackCount = 0;
        fake.LoseSubmissionResponse = true;
        try { await center.SubmitAsync(new(Guid.NewGuid(), FileOperationKind.Copy, [new("/a", "/b")]), _ => callbackCount++); }
        catch (IOException) { }
        check(center.Jobs.Count == 1 && fake.Requests.Count == 3,
            "Lost submission response recovers the existing job without resubmitting");
        fake.Finish(center.Jobs[0].Snapshot.Id, ["/a"]);
        await Eventually(() => callbackCount == 1);
        check(callbackCount == 1, "Recovered submission preserves its completion callback");

        fake.LoseSubmissionResponse = false;
        fake.HoldNextRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await center.SubmitAsync(new(Guid.NewGuid(), FileOperationKind.Copy, [new("/c", "/d")]), _ => { });
        await Eventually(() => fake.ReadHeld);
        var active = center.Jobs.Last();
        await active.CancelCommand.ExecuteAsync(null);
        fake.HoldNextRead.SetResult(active.Snapshot with { State = FileOperationState.Running, Revision = 1 });
        await Task.Delay(600);
        check(active.Snapshot.State == FileOperationState.Cancelled,
            "A delayed poll cannot overwrite a newer cancellation snapshot");
        var session = "first";
        center.SessionKey = () => session;
        center.SessionChanged();
        await center.SubmitAsync(new(Guid.NewGuid(), FileOperationKind.Copy, [new("/old", "/old-target")]), _ => callbackCount++);
        var oldId = center.Jobs.Single().Snapshot.Id;
        session = "second";
        center.SessionChanged();
        fake.Finish(oldId, ["/old"]);
        await Task.Delay(600);
        check(center.Jobs.Count == 0 && callbackCount == 1,
            "Changing login context clears old tasks and prevents old completion callbacks");
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}

public class OperationClientFake : DispatchProxy
{
    public List<StartFileOperationRequest> Requests { get; } = [];
    public Dictionary<Guid, FileOperationDto> Jobs { get; } = [];
    public int DirectoryReads { get; private set; }
    public bool LoseSubmissionResponse { get; set; }
    public bool ReadHeld { get; set; }
    public TaskCompletionSource<FileOperationDto>? HoldNextRead { get; set; }
    public void Finish(Guid id, IReadOnlyList<string> completed) => Jobs[id] = Jobs[id] with
        { State = FileOperationState.Completed, CompletedSources = completed, Revision = 3 };
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case nameof(IExplorerClient.GetDirectoryAsync): DirectoryReads++; return Task.FromResult(ExplorerFake.Directory((string)args![0]!));
            case nameof(IExplorerClient.StartOperationAsync):
                var request = (StartFileOperationRequest)args![0]!;
                Requests.Add(request);
                var dto = new FileOperationDto(Guid.NewGuid(), request.Kind, FileOperationState.Running, request.Items,
                    null, 0, 0, 0, 0, [], null, [], false, DateTimeOffset.UtcNow, request.RequestId, 1);
                Jobs[dto.Id] = dto;
                return LoseSubmissionResponse ? Task.FromException<FileOperationDto>(new IOException("Lost response")) : Task.FromResult(dto);
            case nameof(IExplorerClient.ListOperationsAsync): return Task.FromResult<IReadOnlyList<FileOperationDto>>(Jobs.Values.ToArray());
            case nameof(IExplorerClient.GetOperationAsync):
                if (HoldNextRead is not null && !ReadHeld) { ReadHeld = true; return HoldNextRead.Task; }
                return Task.FromResult(Jobs[(Guid)args![0]!]);
            case nameof(IExplorerClient.CancelOperationAsync):
                var id = (Guid)args![0]!;
                return Task.FromResult(Jobs[id] = Jobs[id] with { State = FileOperationState.Cancelled, Revision = 4 });
            default: throw new NotSupportedException(method.Name);
        }
    }
}
