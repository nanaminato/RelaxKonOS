using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;

internal static class TerminalAdministratorAuthorizationChecks
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSignalR();
        using var provider = services.BuildServiceProvider();
        var factory = new PassiveFactory();
        var context = provider.GetRequiredService<IHubContext<TerminalHub, ITerminalHubClient>>();
        var manager = new TerminalSessionManager(factory, context, null!, null!);
        var privileges = new TestHostAccountPrivilegeService { Level = HostAccountPrivilege.StandardUser };
        var caller = new Caller();
        var hub = new TerminalHub(manager, privileges) { Context = caller };
        var request = new StartTerminalRequest(80, 24, 0, 0, null, null);
        await Denied(() => hub.StartAdministrator(request, null));
        TestAssert.Assert(factory.Calls == 0, "An ordinary user reached the administrator PTY factory.");
        privileges.Level = HostAccountPrivilege.HostAdministrator;
        caller.Principal = Principal("alias");
        await Denied(() => hub.StartAdministrator(request, null));
        TestAssert.Assert(factory.Calls == 0, "An alias password authorized an administrator terminal.");

        var pty = new PassivePty();
        var session = new TerminalSession("elevated", "test-user", pty, context, _ => { }) { IsAdministrator = true };
        // Seed a real persistent session without starting a privileged OS process in the test runner.
        var sessions = (ConcurrentDictionary<string, TerminalSession>)typeof(TerminalSessionManager)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!;
        sessions[session.SessionId] = session;
        await Denied(() => hub.AttachExisting(session.SessionId));
        await Denied(() => hub.Start(request, session.SessionId));
        caller.Principal = Principal("system");
        await hub.AttachExisting(session.SessionId);
        await hub.Input([1]);
        TestAssert.Assert(pty.Writes == 1, "A verified administrator could not use its terminal.");
        privileges.Level = HostAccountPrivilege.StandardUser;
        await Denied(() => hub.Input([2]));
        await Denied(() => hub.Resize(100, 30, 0, 0));
        await Denied(() => hub.AttachExisting(session.SessionId));
        TestAssert.Assert(pty.Writes == 1, "Revoked administrator privileges reached the existing shell.");
        Console.WriteLine("Administrator terminal ordinary/alias denial and permission revocation checks passed.");
    }
    private static async Task Denied(Func<Task> operation)
    {
        try { await operation(); }
        catch (HubException exception) when (exception.Message.StartsWith("terminal.administrator_required:")) { return; }
        throw new Exception("Expected administrator terminal authorization denial.");
    }
    private static ClaimsPrincipal Principal(string method) => new(new ClaimsIdentity([
        new Claim("sub", "test-user"), new Claim("amr", method)], "test"));
    private sealed class Caller : HubCallerContext
    {
        internal ClaimsPrincipal Principal = TerminalAdministratorAuthorizationChecks.Principal("system");
        public override string ConnectionId => "test-connection";
        public override string UserIdentifier => "test-user";
        public override ClaimsPrincipal User => Principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
    private sealed class PassiveFactory : IPtyFactory
    {
        public int Calls;
        public IPty Create() { Calls++; return new PassivePty(); }
    }
    private sealed class PassivePty : IPty
    {
        public int Writes;
        public bool IsRunning { get; set; } = true;
        public int ChildPid { get; set; }
        public event Action<byte[], int>? DataReceived { add { } remove { } }
        public event Action<int>? ProcessExited { add { } remove { } }
        public void Start(string? shell, int columns, int rows, string? workingDirectory,
            Dictionary<string, string>? environment, IReadOnlyList<string>? arguments) { }
        public void Write(byte[] data, int offset, int count) { Writes++; }
        public void Write(string text) { Writes++; }
        public void Resize(int columns, int rows) { }
        public void Resize(int columns, int rows, int widthPixels, int heightPixels) { }
        public void Stop() { IsRunning = false; }
        public void Dispose() => Stop();
    }
}
