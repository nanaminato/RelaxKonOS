namespace RelaxKonOS.ServerModule.CloudStorage;

public sealed partial class CloudStorageModule
{
    // Both methods run under _authorizationGate: cancellation/configuration replacement
    // and successful persistence have one ordering, and concurrent GETs cannot over-poll.
    private async Task StartBaiduAuthorizationAsync(AuthorizationSession session,
        AuthorizationConfiguration configuration, CancellationToken ct)
    {
        try
        {
            var grant = await new BaiduDeviceAuthorization(_transport).StartAsync(configuration.ClientId, ct);
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
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException)
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
            var result = await new BaiduDeviceAuthorization(_transport)
                .PollAsync(config.ClientId, config.Secret, session.DeviceCode, ct);
            if (session.Expires <= DateTimeOffset.UtcNow) { session.Status = "expired"; return; }
            switch (result.Status)
            {
                case BaiduDeviceAuthorization.PollStatus.Pending:
                    break;
                case BaiduDeviceAuthorization.PollStatus.SlowDown:
                    session.Interval += 5;
                    break;
                case BaiduDeviceAuthorization.PollStatus.Denied:
                    session.Status = "denied";
                    break;
                case BaiduDeviceAuthorization.PollStatus.Expired:
                    session.Status = "expired";
                    break;
                case BaiduDeviceAuthorization.PollStatus.Authorized:
                    // Consume this code before saving. A failed/aborted save requires a new flow,
                    // never a replay. The existing encrypted connection store owns the refresh token.
                    session.Status = "failed";
                    await PersistAuthorizationAsync(session, config, result.Tokens!.RefreshToken, ct);
                    break;
                default:
                    session.Status = "failed";
                    break;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException
            or OperationCanceledException or IOException or HttpRequestException)
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
