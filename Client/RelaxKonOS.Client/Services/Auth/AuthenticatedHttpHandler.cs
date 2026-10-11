using System.Net;
using System.Net.Http.Headers;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>
/// Adds the current bearer token to protected API requests, refreshes near-expiry tokens, and
/// retries a server-rejected request once after a coordinated refresh. It deliberately does not
/// retry transport failures, because a non-idempotent operation may already have reached server code.
/// </summary>
public sealed class AuthenticatedHttpHandler(IAuthSession session) : DelegatingHandler
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);
    private static readonly HttpRequestOptionsKey<Func<bool>> LoginGuard = new("RelaxKonOS.LoginGuard");

    public static void BindLogin(HttpRequestMessage request, IAuthSession session)
    {
        var service = session.ServiceId;
        var user = session.CurrentUser;
        var login = session.CurrentSession;
        request.Options.Set(LoginGuard, () => session.State == AuthSessionState.Authenticated
            && session.ServiceId == service && ReferenceEquals(session.CurrentUser, user)
            && ReferenceEquals(session.CurrentSession, login));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var service = session.ServiceId;
        var user = session.CurrentUser;
        var login = session.CurrentSession;
        void EnsureSameLogin()
        {
            if (session.State != AuthSessionState.Authenticated || session.ServiceId != service
                || (request.Options.TryGetValue(LoginGuard, out var bound) && !bound())
                || !ReferenceEquals(session.CurrentUser, user) || !ReferenceEquals(session.CurrentSession, login))
                throw new InvalidOperationException("The request belongs to an ended RelaxKonOS login.");
        }
        EnsureSameLogin();
        var accessToken = await session.GetAccessTokenAsync(RenewBefore, ct: cancellationToken)
            .ConfigureAwait(false);
        EnsureSameLogin();
        if (accessToken is null)
            throw session.State == AuthSessionState.Authenticated
                ? new HttpRequestException("Unable to refresh the RelaxKonOS session. Check the network connection.")
                : new InvalidOperationException("The RelaxKonOS session has expired. Sign in again.");

        // Buffer once before the initial send. Cloning a StreamContent directly advances its
        // underlying stream; without replacing the source content too, multipart uploads such
        // as custom wallpapers arrive at the server as an empty file on their first attempt.
        using var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSameLogin();
        SetAuthorization(request, accessToken);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        string? refreshedToken;
        try
        {
            EnsureSameLogin();
            refreshedToken = await session.GetAccessTokenAsync(TimeSpan.Zero, accessToken, cancellationToken)
                .ConfigureAwait(false);
            EnsureSameLogin();
        }
        catch { response.Dispose(); throw; }
        if (refreshedToken is null)
            return response;

        response.Dispose();
        SetAuthorization(retry, refreshedToken);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static void SetAuthorization(HttpRequestMessage request, string accessToken)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (source.Content is null)
            return clone;

        var originalContent = source.Content;
        var bytes = await originalContent.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        source.Content = CreateBufferedContent(bytes, originalContent);
        clone.Content = CreateBufferedContent(bytes, originalContent);
        return clone;
    }

    private static ByteArrayContent CreateBufferedContent(byte[] bytes, HttpContent source)
    {
        var buffered = new ByteArrayContent(bytes);
        foreach (var header in source.Headers)
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return buffered;
    }
}
