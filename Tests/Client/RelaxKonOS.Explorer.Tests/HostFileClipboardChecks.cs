using System.Reflection;
using Avalonia.Input;
using Avalonia.Input.Platform;
using RelaxKonOS.Client.Apps.Explorer;

public static class HostFileClipboardChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var clipboard = DispatchProxy.Create<IClipboard, ClipboardFake>();
        var fake = (ClipboardFake)(object)clipboard;
        await HostFileClipboard.MarkRemoteCopyAsync(clipboard);
        check((await HostFileClipboard.ReadAsync(clipboard)).IsRemoteCopy,
            "Remote copy writes an identifiable marker to the system clipboard");

        var hostData = new DataTransfer();
        hostData.Add(DataTransferItem.CreateText("new host copy"));
        fake.Data = hostData;
        check(!(await HostFileClipboard.ReadAsync(clipboard)).IsRemoteCopy,
            "A later host copy replaces the remote marker");
    }
}

public class ClipboardFake : DispatchProxy
{
    public IAsyncDataTransfer? Data { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        nameof(IClipboard.SetDataAsync) => SetData(args!),
        nameof(IClipboard.TryGetDataAsync) => Task.FromResult(Data),
        _ => throw new NotSupportedException(method.Name),
    };

    private Task SetData(object?[] args)
    {
        Data = (IAsyncDataTransfer)args[0]!;
        return Task.CompletedTask;
    }
}
