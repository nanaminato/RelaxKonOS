using System.Reflection;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.SystemUi;
using RelaxKonOS.Client.Services.WindowLayout;
using RelaxKonOS.Client.Views;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.WindowManager;

static class DesktopDisconnectChecks
{
    public static void Run(ShellSettings settings)
    {
        var previous = RelaxKonOS.Client.App.Services;
        try
        {
            RunCase(settings, ssh: true, throwNotification: false);
            RunCase(settings, ssh: true, throwNotification: true);
            RunCase(settings, ssh: false, throwNotification: false);
            RunCase(settings, ssh: false, throwNotification: false, stallSave: true);
        }
        finally { SetServices(previous); }
    }

    private static void RunCase(ShellSettings settings, bool ssh, bool throwNotification, bool stallSave = false)
    {
        var sshSession = new SshDesktopSession(null!);
        if (ssh)
            typeof(SshDesktopSession).GetProperty(nameof(SshDesktopSession.Endpoint))!.SetValue(
                sshSession, ServerCenterSshEndpoint.Create("localhost", 22, "test"));
        if (throwNotification)
            sshSession.Disconnected += (_, _) => throw new InvalidOperationException("Notification failed");
        var auth = DispatchProxy.Create<IAuthSession, DisconnectSessionProxy>();
        var identity = (DisconnectSessionProxy)auth;
        var client = new FailingLayoutClient(stallSave);
        using var layouts = new WindowLayoutStore(auth, client);
        layouts.RecordSize("test", new Size(800, 600));
        var manager = new WindowManager();
        using var overview = new WindowOverviewController(manager);
        using var coordinator = new SystemUiCoordinator(manager, overview);
        using var provider = new ServiceCollection()
            .AddSingleton(new LocalizationService(settings, sshSession))
            .AddSingleton(sshSession)
            .AddSingleton(auth)
            .AddSingleton(coordinator)
            // An SSH disconnect must not even resolve the workspace persistence service.
            .AddSingleton<WindowLayoutStore>(_ => ssh
                ? throw new InvalidOperationException("SSH has no workspace layout") : layouts)
            .BuildServiceProvider();
        SetServices(provider);
        var window = new MainWindow();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            var disconnect = (Task)typeof(MainWindow).GetMethod("DisconnectAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!disconnect.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            if (!disconnect.IsCompleted) throw new Exception("Desktop disconnect did not finish.");
            disconnect.GetAwaiter().GetResult();
            if (ssh && (!closed || sshSession.IsConnected || identity.LogoutCalls != 0))
                throw new Exception("SSH desktop did not close independently of workspace persistence.");
            if (!ssh && (identity.LogoutCalls != 1 || client.SaveCalls != 1))
                throw new Exception("A failed layout save prevented logout.");
            Console.WriteLine($"PASS: Desktop disconnect: ssh={ssh}, notificationFailure={throwNotification}, stalledSave={stallSave}");
        }
        finally { if (!closed) window.Close(); }
    }

    private static void SetServices(IServiceProvider services) =>
        typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, services);

    private sealed class FailingLayoutClient(bool stall) : IWindowLayoutClient
    {
        public int SaveCalls { get; private set; }
        public Task<WorkspaceWindowLayoutDto> GetAsync(string url, string token, Guid workspace, CancellationToken ct = default)
            => Task.FromResult(new WorkspaceWindowLayoutDto([]));
        public async Task<WorkspaceWindowLayoutDto> SaveAsync(string url, string token, Guid workspace,
            WorkspaceWindowLayoutDto layouts, CancellationToken ct = default)
        {
            SaveCalls++;
            if (stall) await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Layout save failed");
        }
    }
}

public class DisconnectSessionProxy : DispatchProxy
{
    public int LogoutCalls { get; private set; }
    private readonly WorkspaceDto _workspace = new(Guid.NewGuid(), Guid.NewGuid(), "test",
        WorkspaceState.Running, DateTimeOffset.UtcNow, null);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_State": return AuthSessionState.Authenticated;
            case "get_EffectiveBaseUrl": return "http://localhost/";
            case "get_Tokens": return new AuthTokens("access", "refresh", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
            case "get_CurrentWorkspace": return _workspace;
            case "add_StateChanged": case "remove_StateChanged": return null;
            case "LogoutAsync": LogoutCalls++; return Task.CompletedTask;
            default: throw new NotSupportedException(method.Name);
        }
    }
}
