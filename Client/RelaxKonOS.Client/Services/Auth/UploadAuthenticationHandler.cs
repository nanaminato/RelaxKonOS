using System.Net.Http.Headers;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>
/// Adds the current bearer token to upload-data-plane requests, and does nothing else.
/// </summary>
/// <remarks>
/// This handler exists because <see cref="AuthenticatedHttpHandler"/> cannot be used for an upload body:
/// it reads the entire request content into a managed byte array before the first send so it can replay
/// the request after a 401. For a multi-gigabyte file that is an out-of-memory failure, and it also makes
/// progress reporting lie, because the bytes counted are the ones written into memory rather than the ones
/// on the wire. A 401 is deliberately returned to the caller here: the resumable protocol retries by
/// re-reading the session offset, which needs no copy of the body at all.
/// </remarks>
public sealed class UploadAuthenticationHandler(IAuthSession session) : DelegatingHandler
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var accessToken = await session.GetAccessTokenAsync(RenewBefore, ct: cancellationToken).ConfigureAwait(false);
        if (accessToken is null)
            throw session.State == AuthSessionState.Authenticated
                ? new HttpRequestException("Unable to refresh the RelaxKonOS session. Check the network connection.")
                : new InvalidOperationException("The RelaxKonOS session has expired. Sign in again.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
