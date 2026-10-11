using System.Reflection;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;

internal static class SshDesktopCancellationChecks
{
    public static async Task RunAsync()
    {
        var resolver = DispatchProxy.Create<IServerCenterConnectionResolver, DelayedDesktopResolver>();
        var stub = (DelayedDesktopResolver)(object)resolver;
        var desktop = new SshDesktopSession(resolver);
        var target = ServerHostTargetRules.Create("test-host", 22, "test-user", null, DateTimeOffset.UtcNow);
        var notifications = 0;
        desktop.Connected += (_, _) => notifications++;
        using var cancellation = new CancellationTokenSource();
        var connecting = desktop.ConnectAsync(target, "test-password", cancellation.Token);
        cancellation.Cancel();
        var verification = Verification(target);
        stub.Pending.SetResult(verification.Session);
        await ExpectCancellation(connecting);
        Check(!desktop.IsConnected && desktop.Password is null && notifications == 0 && verification.Transport.Disposed,
            "Canceled SSH verification does not publish credentials and disposes its transport.");

        stub.Pending = new();
        connecting = desktop.ConnectAsync(target, "test-password", default);
        desktop.Disconnect();
        verification = Verification(target);
        stub.Pending.SetResult(verification.Session);
        await ExpectCancellation(connecting);
        Check(!desktop.IsConnected && notifications == 0 && verification.Transport.Disposed,
            "Disconnect invalidates a pending SSH handshake even before an endpoint exists.");

        stub.Pending = new();
        var firstCompletion = stub.Pending;
        connecting = desktop.ConnectAsync(target, "old-password", default);
        stub.Pending = new();
        var newConnection = desktop.ConnectAsync(target, "new-password", default);
        verification = Verification(target);
        stub.Pending.SetResult(verification.Session);
        await newConnection;
        var oldVerification = Verification(target);
        firstCompletion.SetResult(oldVerification.Session);
        await ExpectCancellation(connecting);
        Check(desktop.IsConnected && desktop.Password == "new-password" && notifications == 1 && oldVerification.Transport.Disposed,
            "Late SSH verification cannot replace a newer connection to the same host.");
        desktop.Disconnect();
    }

    private static (ServerCenterHostSession Session, DesktopVerificationTransport Transport) Verification(ServerHostTarget target)
    {
        var transport = DispatchProxy.Create<IServerCenterSshTransport, DesktopVerificationTransport>();
        var session = (ServerCenterHostSession)Activator.CreateInstance(typeof(ServerCenterHostSession),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [target, transport], null)!;
        return (session, (DesktopVerificationTransport)(object)transport);
    }
    private static async Task ExpectCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected canceled SSH desktop publication.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class DelayedDesktopResolver : DispatchProxy
{
    public TaskCompletionSource<ServerCenterHostSession> Pending = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "PrepareHostKeyGuardAsync" => Task.FromResult<Func<ServerCenterHostKeyObservation, ServerHostKeyTrust>>(_ => ServerHostKeyTrust.Trusted),
        "ConnectAsync" => Pending.Task,
        _ => throw new NotSupportedException(method.Name)
    };
}

public class DesktopVerificationTransport : DispatchProxy
{
    public bool Disposed;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "get_ObservedHostKey") return new ServerCenterHostKeyObservation("test-host", 22, "ssh-ed25519", [1, 2, 3]);
        if (method.Name == "DisposeAsync") { Disposed = true; return ValueTask.CompletedTask; }
        throw new NotSupportedException(method.Name);
    }
}
