using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Client.Foundation.Auth;

public enum AuthSessionState { Unauthenticated, Connecting, Authenticated }

public interface IAuthSession
{
    AuthSessionState State { get; }
    string? ServerUrl { get; }
    LoginResponse? Login { get; }
    Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, bool rememberConnection, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

/// <summary>Memory-only token holder. A rejected or unavailable server never causes locally saved connection metadata to become a credential store.</summary>
public sealed class AuthSession(IRelaxKonOSClient client, IConnectionProfileStore profiles) : IAuthSession
{
    public AuthSessionState State { get; private set; } = AuthSessionState.Unauthenticated;
    public string? ServerUrl { get; private set; }
    public LoginResponse? Login { get; private set; }

    public async Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, bool rememberConnection, CancellationToken cancellationToken = default)
    {
        if (State == AuthSessionState.Connecting) throw new InvalidOperationException("A login request is already in progress.");
        State = AuthSessionState.Connecting;
        try
        {
            var response = await client.LoginAsync(serverUrl, request, cancellationToken).ConfigureAwait(false);
            ServerUrl = serverUrl;
            Login = response;
            State = AuthSessionState.Authenticated;
            if (rememberConnection)
                await profiles.SaveAsync(new ConnectionProfile(serverUrl, request.Identifier, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch
        {
            State = AuthSessionState.Unauthenticated;
            throw;
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (Login is { } login && ServerUrl is { } serverUrl)
            await client.LogoutAsync(serverUrl, login.Tokens.AccessToken, login.Tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        ServerUrl = null;
        Login = null;
        State = AuthSessionState.Unauthenticated;
    }
}

public interface IRelaxKonOSClient
{
    Task<LoginResponse> LoginAsync(string serverUrl, LoginRequest request, CancellationToken cancellationToken = default);
    Task LogoutAsync(string serverUrl, string accessToken, string refreshToken, CancellationToken cancellationToken = default);
}
