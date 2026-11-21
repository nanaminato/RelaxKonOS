using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalTerminal(ExternalServerConnection? connection, SshDesktopSession ssh,
    NetworkDiagnosticsService? diagnostics) : IExternalTerminal
{
    private readonly ServerCenterSshEndpoint? _endpoint = ssh.Endpoint;

    public bool CanOpenAdministratorTerminal => _endpoint is null && connection?.IsCurrent == true
        && connection.Session.CurrentServer is { Platform: HostPlatformKind.Windows, Host.Capabilities.PrivilegedOperations: true };

    public IExternalTerminalSession CreateSession(string vtEngine, string? sessionId, string? workingDirectory, bool isAdministrator)
    {
        RequireCurrentConnection();
        var factory = new TerminalSurfaceFactory(new SshDesktopCredentialProvider(ssh), new KnownHostsSshHostKeyValidator());
        var (options, remote) = new TerminalConnectionOptions(connection, ssh, diagnostics)
            .Create(new TerminalSessionDimensions(80, 24, 800, 480), sessionId, workingDirectory, isAdministrator);
        var surface = factory.Create(Enum.Parse<VtProcessorPreference>(vtEngine));
        return new Session(this, factory, surface, options, remote);
    }

    private void RequireCurrentConnection()
    {
        if (!ReferenceEquals(_endpoint, ssh.Endpoint) || connection is { IsCurrent: false })
            throw new InvalidOperationException("The terminal connection is no longer current.");
    }

    private sealed class Session(ExternalTerminal terminal, TerminalSurfaceFactory factory, BuiltInTerminalSurface surface,
        ITerminalTransportOptions options, bool remote) : IExternalTerminalSession
    {
        private bool _disposed;

        public ITerminalSurface Surface => surface;
        public bool IsRemote => remote;
        public bool HasAuthenticatedServer => terminal.HasAuthenticatedServer;
        public string? SessionId => factory.TransportFactory.CurrentSessionId;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            terminal.RequireCurrentConnection();
            return surface.StartSessionAsync(options, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ = HasAuthenticatedServer ? factory.TransportFactory.KillActiveAsync() : factory.TransportFactory.StopActiveAsync();
            surface.StopSession();
            surface.Dispose();
        }
    }

    private bool HasAuthenticatedServer => connection?.IsCurrent == true;
}
