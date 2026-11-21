using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.ServerCenter;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Builds transport options from the active desktop session.</summary>
internal sealed class TerminalConnectionOptions(ExternalServerConnection? connection, SshDesktopSession? sshDesktop,
    NetworkDiagnosticsService? diagnostics)
{
    public (ITerminalTransportOptions Options, bool Remote) Create(
        TerminalSessionDimensions dimensions, string? sessionId, string? workingDirectory, bool isAdministrator)
    {
        if (sshDesktop is { IsConnected: true, Endpoint: { } endpoint, HostKeyFingerprint: { } fingerprint })
        {
            return (new SshTransportOptions(
                Endpoint: new SshEndpointOptions(endpoint.Host, endpoint.Port, endpoint.UserName),
                RequestPty: true,
                TerminalType: "xterm-256color",
                InitialCommand: null,
                Authentication: new SshAuthenticationOptions(
                    UsePassword: true,
                    PasswordSecretId: "ssh-desktop-session",
                    PrivateKeySecretIds: Array.Empty<string>(),
                    UseAgent: false),
                Dimensions: dimensions)
            { ExpectedHostKeyFingerprintSha256 = fingerprint }, true);
        }
        if (connection is { IsCurrent: true } && connection.Session is
            { State: AuthSessionState.Authenticated, EffectiveBaseUrl: { } url })
        {
            return (new SignalRTransportOptions(
                hubUrl: url.TrimEnd('/') + "/hubs/terminals",
                dimensions: dimensions,
                tokenProvider: async () =>
                {
                    if (!connection.IsCurrent) return null;
                    var token = await connection.Session.GetAccessTokenAsync(TimeSpan.FromMinutes(1));
                    return connection.IsCurrent ? token : null;
                },
                sessionId: sessionId,
                workingDirectory: workingDirectory,
                diagnostics: diagnostics,
                isAdministrator: isAdministrator), true);
        }
        return (new PtyTransportOptions(
            Command: null,
            WorkingDirectory: workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment: null,
            Dimensions: dimensions), false);
    }
}
