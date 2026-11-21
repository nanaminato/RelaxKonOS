using System.Net;
using System.Net.Http.Json;
using RelaxKonOS.ModuleHost.Protocol;

namespace RelaxKonOS.ServerModule.CloudStorage;

public sealed partial class CloudStorageModule
{
    private async Task<T> RequestBaiduAuthorizationAsync<T>(CloudProviderAction action, string clientId,
        string secret, string? token, CancellationToken ct)
    {
        using var response = await _context.CloudProviders.SendAsync(CloudFileProvider.Baidu,
            new(action, "https://pan.baidu.com", clientId, secret, false, "", SessionToken: token), null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("cloud-baidu-authorization-response");
    }

    // Both methods run under _authorizationGate: cancellation/configuration replacement
    // and successful persistence have one ordering, and concurrent GETs cannot over-poll.
    private async Task StartBaiduAuthorizationAsync(AuthorizationSession session,
        AuthorizationConfiguration configuration, CancellationToken ct)
    {
        try
        {
            var grant = await RequestBaiduAuthorizationAsync<CloudBaiduDeviceGrant>(CloudProviderAction.StartAuthorization,
                configuration.ClientId, "", null, ct);
            var now = DateTimeOffset.UtcNow;
            session.DeviceCode = grant.DeviceCode;
            session.UserCode = grant.UserCode;
            session.Url = grant.VerificationUri.AbsoluteUri;
            session.QrCodeUrl = grant.QrCodeUri?.AbsoluteUri;
            session.Interval = grant.Interval;
            var providerExpiry = now.AddSeconds(grant.ExpiresIn);
            if (providerExpiry < session.Expires) session.Expires = providerExpiry;
            session.NextPoll = now.AddSeconds(session.Interval);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { throw; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException
            or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            session.Status = "failed";
            ClearAuthorizationSecrets(session);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        }
    }

    private async Task PollBaiduAuthorizationAsync(AuthorizationSession session, CancellationToken ct)
    {
        if (session.Status != "pending" || DateTimeOffset.UtcNow < session.NextPoll) return;
        if (session.Expires <= DateTimeOffset.UtcNow)
        {
            session.Status = "expired";
            ClearAuthorizationSecrets(session);
            return;
        }
        if (!_authorizationConfigurations.TryGetValue(session.Protocol, out var config)
            || config.Revision != session.Revision || session.DeviceCode is null)
        {
            session.Status = "expired";
            ClearAuthorizationSecrets(session);
            return;
        }
        // Set before network I/O so failing requests cannot bypass the provider interval.
        session.NextPoll = DateTimeOffset.UtcNow.AddSeconds(session.Interval);
        try
        {
            var result = await RequestBaiduAuthorizationAsync<CloudBaiduPollResult>(CloudProviderAction.PollAuthorization,
                config.ClientId, config.Secret, session.DeviceCode, ct);
            if (session.Expires <= DateTimeOffset.UtcNow) { session.Status = "expired"; return; }
            switch (result.Status)
            {
                case CloudBaiduPollStatus.Pending:
                    break;
                case CloudBaiduPollStatus.SlowDown:
                    session.Interval += 5;
                    break;
                case CloudBaiduPollStatus.Denied:
                    session.Status = "denied";
                    break;
                case CloudBaiduPollStatus.Expired:
                    session.Status = "expired";
                    break;
                case CloudBaiduPollStatus.Authorized when result.Tokens is not null:
                    // Consume this code before saving. A failed/aborted save requires a new flow,
                    // never a replay. The existing encrypted connection store owns the refresh token.
                    session.Status = "failed";
                    await PersistAuthorizationAsync(session, config, result.Tokens.RefreshToken, ct);
                    break;
                default:
                    session.Status = "failed";
                    break;
            }
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { throw; }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or OperationCanceledException or IOException or HttpRequestException or System.Text.Json.JsonException)
        {
            session.Status = "failed";
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        }
        finally
        {
            if (session.Status != "pending") ClearAuthorizationSecrets(session);
            else session.NextPoll = DateTimeOffset.UtcNow.AddSeconds(session.Interval);
        }
    }
}
