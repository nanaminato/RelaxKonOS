using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>The SSH desktop's in-memory connection identity. Each terminal opens its own SSH channel.</summary>
public sealed class SshDesktopSession(IServerCenterConnectionResolver connections)
{
    public ServerCenterSshEndpoint? Endpoint { get; private set; }
    public string? Password { get; private set; }
    public string? HostKeyFingerprint { get; private set; }
    public bool IsConnected => Endpoint is not null;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(ServerHostTarget target, string password, CancellationToken cancellationToken)
    {
        var endpoint = ServerCenterSshEndpoint.Create(target.SshHost, target.SshPort, target.SshUserName);
        await using var verification = await connections.ConnectAsync(
            target, new ServerCenterSshCredential.Password(password),
            await connections.PrepareHostKeyGuardAsync(target, cancellationToken), cancellationToken);
        var observed = verification.ObservedHostKey
            ?? throw new InvalidOperationException("SSH did not return a host key.");
        Endpoint = endpoint;
        Password = password;
        HostKeyFingerprint = observed.Fingerprint;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        if (Endpoint is null) return;
        Endpoint = null;
        Password = null;
        HostKeyFingerprint = null;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
