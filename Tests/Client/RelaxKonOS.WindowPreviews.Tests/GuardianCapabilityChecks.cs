using System.Runtime.CompilerServices;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ProcessGuardian;

internal static class GuardianCapabilityChecks
{
    public static async Task RunAsync()
    {
        var id = new AppId("relaxkonos.processguardian");
        var grants = new CertificateCapabilityChecks.Permission(id.Value);
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "guardian-memory.json"));
        var client = DispatchProxy.Create<IServerGuardian, GuardianCapabilityClient>();
        var calls = (GuardianCapabilityClient)(object)client;
        using var lifetime = new CancellationTokenSource();
        IServerGuardian guardian = new ServerGuardianCapability(id, grants,
            new ExternalServerConnection(memory.Capture(auth)), client, lifetime.Token);

        await RejectAsync<UnauthorizedAccessException>(() => guardian.ListWorkloadsAsync());
        await using (var denied = guardian.WatchLogsAsync("worker").GetAsyncEnumerator())
            await RejectAsync<UnauthorizedAccessException>(() => denied.MoveNextAsync().AsTask());

        grants.Granted.Add(AppPermissions.ServerGuardianManage);
        await RejectAsync<UnauthorizedAccessException>(() => guardian.ApplyActionAsync("worker", "stop"));
        grants.Granted.Remove(AppPermissions.ServerGuardianManage);
        grants.Granted.Add(AppPermissions.ServerGuardianRead);
        await guardian.ListWorkloadsAsync();
        await RejectAsync<UnauthorizedAccessException>(() => guardian.ApplyActionAsync("worker", "stop"));
        if (calls.Reads != 1 || calls.Writes != 0) throw new InvalidOperationException("Guardian grant check failed.");

        grants.Granted.Add(AppPermissions.ServerGuardianManage);
        await guardian.ApplyActionAsync("worker", "stop");
        if (calls.Writes != 1) throw new InvalidOperationException("Guardian management call was not delegated.");

        await using var logs = guardian.WatchLogsAsync("worker").GetAsyncEnumerator();
        if (!await logs.MoveNextAsync() || logs.Current.Count != 0)
            throw new InvalidOperationException("Guardian did not forward the log snapshot.");
        var nextSnapshot = logs.MoveNextAsync().AsTask();

        var session = (EventAlertsAuthFixture)(object)auth;
        session.ServiceId = "http://replacement.invalid";
        session.NotifyStateChanged();
        await RejectAsync<InvalidOperationException>(() => guardian.ListWorkloadsAsync());
        await RejectAsync<OperationCanceledException>(async () => await nextSnapshot.WaitAsync(TimeSpan.FromSeconds(5)));
        lifetime.Cancel();
        await RejectAsync<OperationCanceledException>(() => guardian.ListWorkloadsAsync());
        if (calls.Reads != 1) throw new InvalidOperationException("Guardian read reached an invalidated connection.");
        Console.WriteLine("Guardian SDK permission and lifetime checks passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

public class GuardianCapabilityClient : DispatchProxy
{
    public int Reads { get; private set; }
    public int Writes { get; private set; }

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case nameof(IServerGuardian.ListWorkloadsAsync):
                Reads++;
                return Task.FromResult<IReadOnlyList<GuardianWorkloadDto>>([]);
            case nameof(IServerGuardian.ApplyActionAsync):
                Writes++;
                return Task.FromResult(new GuardianAgentResponse(true, string.Empty));
            case nameof(IServerGuardian.WatchLogsAsync):
                return WatchAsync((CancellationToken)args![1]!);
            default:
                throw new NotSupportedException(method?.Name);
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<GuardianLogEntryDto>> WatchAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        yield return [];
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
