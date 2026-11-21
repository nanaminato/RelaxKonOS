using RelaxKonOS.Client.Services.ServerCenter;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

internal sealed class SshDesktopCredentialProvider(SshDesktopSession session) : ISshCredentialProvider
{
    private readonly ServerCenterSshEndpoint? _endpoint = session.Endpoint;

    public ValueTask<SshResolvedCredentials> ResolveAsync(SshCredentialRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SshResolvedCredentials(
            Password: _endpoint is null ? null : session.GetFileConnection(_endpoint).Password,
            PrivateKeyPemOrPath: Array.Empty<string>(),
            UseAgent: false));
    }
}
