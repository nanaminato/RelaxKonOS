using RelaxKonOS.Client.Services.ServerCenter;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Client.Apps.Terminal;

internal sealed class SshDesktopCredentialProvider(SshDesktopSession session) : ISshCredentialProvider
{
    public ValueTask<SshResolvedCredentials> ResolveAsync(SshCredentialRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SshResolvedCredentials(
            Password: session.Password,
            PrivateKeyPemOrPath: Array.Empty<string>(),
            UseAgent: false));
    }
}
