using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.TaskManager;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.SystemMonitor;
using RelaxKonOS.Protocol.Privileged;

internal static class ServerMonitorCapabilityChecks
{
    public static async Task RunAsync()
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var client = DispatchProxy.Create<ITaskManagerClient, PerformanceHistoryFixture>();
        var transport = (PerformanceHistoryFixture)(object)client;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "monitor-memory.json"));
        var permissions = new Permission(AppPermissions.ServerMetricsRead);
        var monitor = new ServerMonitorCapability(new AppId("relaxkonos.taskmanager"), permissions, memory.Capture(auth), client);

        Check(await monitor.GetPerformanceHistoryAsync() is { Status: AppCapabilityResult.PermissionDenied, Data: null }
            && transport.Requests == 0, "Performance history must require the metrics read grant.");
        await using var stream = monitor.CreatePerformanceStream();
        Check(await stream.StartAsync() == AppCapabilityResult.PermissionDenied,
            "The real-time subscription must require the same metrics read grant.");
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
        Check(await stream.StartAsync() == AppCapabilityResult.Unavailable,
            "A stream created for the former server must not open the replacement connection.");
        Console.WriteLine("Server monitoring permission, sampling bounds and connection isolation passed.");
        await RunProcessesAsync(memory);
    }

    private static async Task RunProcessesAsync(UsageMemoryStore memory)
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var client = DispatchProxy.Create<ITaskManagerClient, ProcessTransportFixture>();
        var transport = (ProcessTransportFixture)(object)client;
        var permissions = new Permission(AppPermissions.ServerProcessesManage);
        var broker = new ProcessBroker();
        IServerProcesses processes = new ServerProcessesCapability(new AppId("relaxkonos.taskmanager"), permissions,
            memory.Capture(auth), client, broker);
        var start = DateTimeOffset.UtcNow;

        await RejectAsync<UnauthorizedAccessException>(() => processes.QueryAsync());
        await RejectAsync<UnauthorizedAccessException>(() => processes.GetIconAsync(123, start));
        await RejectAsync<UnauthorizedAccessException>(() => processes.AuthorizeAdministratorAsync());
        await RejectAsync<UnauthorizedAccessException>(() => processes.TerminateAsync(123, start));
        Check(transport.Reads.Count == 0 && transport.Icons.Count == 0 && transport.Terminations.Count == 0 && broker.Requests == 0,
            "Denied process calls must reach neither transport nor the host authorization broker.");

        permissions.Granted = true;
        await processes.QueryAsync(page: 2, pageSize: 50);
        await processes.GetIconAsync(123, start);
        await processes.AuthorizeAdministratorAsync();
        Check(transport.Reads.SequenceEqual([(2, 50, false), (1, 1, true)]) && transport.Icons.SequenceEqual([(123, start)])
            && broker.Requests == 1,
            "Administrator authorization must probe a read through the existing process grant.");
        var terminated = await processes.TerminateAsync(123, start);
        Check(terminated.Success && transport.Terminations.SequenceEqual([(123, start, false), (123, start, true)])
            && broker.Requests == 2, "Explicit permission failure may escalate only the same process instance.");

        broker.BeforeOperation = () => session.ServiceId = "https://replacement.invalid";
        await RejectAsync<OperationCanceledException>(() => processes.TerminateAsync(123, start));
        await RejectAsync<InvalidOperationException>(() => processes.QueryAsync());
        Check(transport.Terminations.Count == 3 && !transport.Terminations.Last().Elevated && transport.Reads.Count == 2,
            "A changed server must not receive a retry or a read from the former context.");
        Console.WriteLine("Server process permission, administrator probe and termination connection isolation passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Permission(string permitted) : IAppPermissionManager
    {
        public bool Granted { get; set; }
        public bool IsGranted(AppId id, string permission) =>
            Granted && id.Value == "relaxkonos.taskmanager" && permission == permitted;
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
    }

    private sealed class ProcessBroker : IHostElevationBroker
    {
        public int Requests { get; private set; }
        public Action? BeforeOperation { get; set; }

        public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
            CancellationToken cancellationToken)
        {
            Check(capability == HostElevationCapability.ProcessManage && target == "host/processes",
                "Process elevation must request the server's process-management grant.");
            Requests++;
            BeforeOperation?.Invoke();
            return operation();
        }

        public Task<bool> RequestGrantAsync(HostElevationCapability capability, string target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

public class ProcessTransportFixture : DispatchProxy
{
    internal List<(int Page, int PageSize, bool Elevated)> Reads { get; } = [];
    internal List<(int Id, DateTimeOffset Start)> Icons { get; } = [];
    internal List<(int Id, DateTimeOffset Start, bool Elevated)> Terminations { get; } = [];

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(ITaskManagerClient.QueryProcessesAsync))
        {
            Reads.Add(((int)args![0]!, (int)args[1]!, (bool)args[5]!));
            return Task.FromResult(new ProcessPageDto([], 0, DateTimeOffset.UtcNow));
        }
        if (method?.Name == nameof(ITaskManagerClient.KillProcessAsync))
        {
            var elevated = (bool)args![2]!;
            Terminations.Add(((int)args[0]!, (DateTimeOffset)args[1]!, elevated));
            return Task.FromResult(new KillProcessResultDto(elevated, !elevated,
                elevated ? string.Empty : "process.permission_denied", null));
        }
        if (method?.Name == nameof(ITaskManagerClient.GetProcessIconAsync))
        {
            Icons.Add(((int)args![0]!, (DateTimeOffset)args[1]!));
            return Task.FromResult<(byte[] Data, string ContentType)?>(null);
        }
        throw new NotSupportedException(method?.Name);
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
