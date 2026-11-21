using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.SystemMonitor;

internal static class ServerMonitorCapabilityChecks
{
    public static async Task RunAsync()
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var client = DispatchProxy.Create<ITaskManagerClient, PerformanceHistoryFixture>();
        var transport = (PerformanceHistoryFixture)(object)client;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "monitor-memory.json"));
        var permissions = new Permission();
        var monitor = new ServerMonitorCapability(new AppId("relaxkonos.taskmanager"), permissions, memory.Capture(auth), client);

        Check(await monitor.GetPerformanceHistoryAsync() is { Status: AppCapabilityResult.PermissionDenied, Data: null }
            && transport.Requests == 0, "Performance history must require the metrics read grant.");
        permissions.Granted = true;
        var read = monitor.GetPerformanceHistoryAsync(int.MaxValue);
        Check(transport.Requests == 1 && transport.Seconds == 60, "Performance history must be capped at 60 seconds.");
        transport.Pending.SetResult([]);
        Check(await read is { Status: AppCapabilityResult.Succeeded, Data.Count: 0 }, "The authenticated performance result was lost.");

        transport.Pending = new();
        read = monitor.GetPerformanceHistoryAsync(int.MinValue);
        Check(transport.Requests == 2 && transport.Seconds == 1, "Performance history must request at least one second.");
        session.ServiceId = "https://replacement.invalid";
        transport.Pending.SetResult([]);
        Check(await read is { Status: AppCapabilityResult.Unavailable, Data: null }, "The former server's response must be discarded.");
        Check(await monitor.GetSnapshotAsync() is { Status: AppCapabilityResult.Unavailable, Snapshot: null }
            && transport.Requests == 2, "The old monitoring context must not contact the replacement server.");
        Console.WriteLine("Server monitoring permission, sampling bounds and connection isolation passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Permission : IAppPermissionManager
    {
        public bool Granted { get; set; }
        public bool IsGranted(AppId id, string permission) =>
            Granted && id.Value == "relaxkonos.taskmanager" && permission == AppPermissions.ServerMetricsRead;
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
    }
}

public class PerformanceHistoryFixture : DispatchProxy
{
    internal TaskCompletionSource<IReadOnlyList<PerformanceRealtimeSnapshotDto>> Pending { get; set; } = new();
    internal int Requests { get; private set; }
    internal int Seconds { get; private set; }

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        Requests++;
        if (method?.Name != nameof(ITaskManagerClient.GetPerformanceHistoryAsync))
            throw new NotSupportedException(method?.Name);
        Seconds = (int)args![0]!;
        return Pending.Task;
    }
}
