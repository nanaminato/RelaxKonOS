using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Browser;

internal static class BrowserCapabilityChecks
{
    public static async Task RunAsync()
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var transport = DispatchProxy.Create<IBrowserData, BrowserCapabilityTransport>();
        var fixture = (BrowserCapabilityTransport)(object)transport;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "browser-memory.json"));
        IBrowserData data = new ExternalBrowserData(transport, new ExternalServerConnection(memory.Capture(auth)));
        await data.ClearHistoryAsync();
        var pending = data.ListHistoryAsync();
        session.ServiceId = "https://replacement.invalid";
        fixture.Pending.SetResult([]);
        await RejectAsync(() => pending);
        await RejectAsync(() => data.ClearHistoryAsync());
        if (fixture.Requests != 2) throw new InvalidOperationException("The old Browser contacted the replacement server.");
        Console.WriteLine("Browser SDK rejects stale reads and writes after a server change.");
    }

    private static async Task RejectAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the Browser connection to be rejected.");
    }
}

public class BrowserCapabilityTransport : DispatchProxy
{
    internal int Requests { get; private set; }
    internal TaskCompletionSource<IReadOnlyList<HistoryEntryDto>> Pending { get; } = new();

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        Requests++;
        return method?.Name switch
        {
            nameof(IBrowserData.ListHistoryAsync) => Pending.Task,
            nameof(IBrowserData.ClearHistoryAsync) => Task.CompletedTask,
            _ => throw new NotSupportedException(method?.Name),
        };
    }
}
