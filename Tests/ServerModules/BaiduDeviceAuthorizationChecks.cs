using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using RelaxKonOS.ServerModule.CloudStorage;

internal static class BaiduDeviceAuthorizationChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        const string grant = """{"device_code":"private-device-code","user_code":"ABCD-EFGH","verification_url":"http://openapi.baidu.com/device","qrcode_url":"https://openapi.baidu.com/device/qrcode/test","expires_in":300,"interval":5}""";
        Uri? requested = null;
        var provider = Create(grant, uri => requested = uri);
        var result = await provider.StartAsync("application-id", default);
        var query = QueryHelpers.ParseQuery(requested!.Query);
        check(requested.AbsolutePath == "/oauth/2.0/device/code" && query["response_type"] == "device_code"
            && query["scope"] == "basic,netdisk", "Baidu device-code request uses official parameters");
        check(result.Interval == 5 && result.ExpiresIn == 300 && result.VerificationUri.Scheme == "https"
            && !result.ToString().Contains(result.DeviceCode), "Baidu preserves provider timing and upgrades official HTTP verification link");
        provider = Create("""{"access_token":"private-access","refresh_token":"private-refresh","expires_in":2592000}""", uri => requested = uri);
        var poll = await provider.PollAsync("application-id", "secret&=?", "code&=?", default);
        query = QueryHelpers.ParseQuery(requested!.Query);
        check(query["grant_type"] == "device_token" && query["code"] == "code&=?"
            && query["client_secret"] == "secret&=?" && !query.ContainsKey("device_code"),
            "Baidu token exchange uses code parameter with safe query encoding");
        check(poll.Status == BaiduDeviceAuthorization.PollStatus.Authorized
            && poll.Tokens!.RefreshToken == "private-refresh" && !poll.ToString().Contains("private-access"),
            "Baidu authorization returns validated tokens without diagnostic disclosure");
        foreach (var (error, status) in new[] {
            ("authorization_pending", BaiduDeviceAuthorization.PollStatus.Pending),
            ("slow_down", BaiduDeviceAuthorization.PollStatus.SlowDown),
            ("authorization_declined", BaiduDeviceAuthorization.PollStatus.Denied),
            ("access_denied", BaiduDeviceAuthorization.PollStatus.Denied),
            ("expired_token", BaiduDeviceAuthorization.PollStatus.Expired),
            ("invalid_client", BaiduDeviceAuthorization.PollStatus.Failed) })
        {
            poll = await Create($$"""{"error":"{{error}}","error_description":"secret must not escape"}""", status: HttpStatusCode.BadRequest)
                .PollAsync("id", "secret", "code", default);
            check(poll.Status == status && poll.Tokens is null, "Baidu handles " + error);
        }
        foreach (var invalid in new[] {
            grant.Replace("http://openapi.baidu.com/device", "https://evil.example/device"),
            grant.Replace("http://openapi.baidu.com/device", "https://openapi.baidu.com.evil.example/device"),
            grant.Replace("http://openapi.baidu.com/device", "https://user@openapi.baidu.com/device"),
            grant.Replace("http://openapi.baidu.com/device", "https://openapi.baidu.com:444/device"),
            grant.Replace("http://openapi.baidu.com/device", "https://openapi.baidu.com/other"),
            grant.Replace("https://openapi.baidu.com/device/qrcode/test", "https://evil.example/qr"),
            grant.Replace("\"interval\":5", "\"interval\":0"),
            grant.Replace("\"expires_in\":300", "\"expires_in\":-1"),
            grant.Replace("ABCD-EFGH", "ABCD\\nEFGH"), "[]", "{", new string('x', 65537) })
            await RejectAsync(() => Create(invalid).StartAsync("id", default), check, "Baidu rejects unsafe or malformed device grants");
        await RejectAsync(() => Create(grant, status: HttpStatusCode.Redirect).StartAsync("id", default), check,
            "Baidu does not accept redirects");
        await RejectAsync(() => Create("""{"access_token":"secret","expires_in":300}""")
            .PollAsync("id", "secret", "code", default), check, "Baidu requires a refresh token before successful authorization");
        var networkFailure = new BaiduDeviceAuthorization((_, _) => new HttpClient(new ReplyHandler((_, _) =>
            throw new HttpRequestException("https://openapi.baidu.com?client_secret=private"))));
        await RejectAsync(() => networkFailure.StartAsync("id", default), check, "Baidu sanitizes transport exceptions");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await Create(grant).StartAsync("id", canceled.Token); check(false, "Baidu propagates cancellation"); }
        catch (OperationCanceledException) { check(true, "Baidu propagates cancellation"); }
    }

    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string message)
    {
        try { await action(); check(false, message); }
        catch (InvalidOperationException e)
        {
            check(e.Message == "Baidu authorization request failed." && e.InnerException is null, message);
        }
    }

    private static BaiduDeviceAuthorization Create(string json, Action<Uri>? observe = null,
        HttpStatusCode status = HttpStatusCode.OK) => new((uri, allowPrivate) =>
    {
        if (allowPrivate || uri.Scheme != "https" || uri.Host != "openapi.baidu.com")
            throw new Exception("Unexpected OAuth destination");
        observe?.Invoke(uri);
        return new HttpClient(new ReplyHandler((request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get) throw new Exception("Unexpected OAuth method");
            return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }));
    });

    private sealed class ReplyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => reply(request, cancellationToken);
    }
}
