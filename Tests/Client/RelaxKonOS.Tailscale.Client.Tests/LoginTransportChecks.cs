using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Apps.Tailscale;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Tailscale;

internal static class LoginTransportChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new Exception("Login transport assertion " + count); count++; }
        var session = DispatchProxy.Create<IAuthSession, TransportChecks.Session>();
        var state = (TransportChecks.Session)(object)session;
        var id = Guid.NewGuid(); var paths = new List<string>(); var methods = new List<HttpMethod>();
        var operation = new TailscaleLoginOperation(id,TailscaleLoginState.Prepared,new('A',64),DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow);
        var mode = "normal";
        using var http = new HttpClient(new Handler(async request =>
        {
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-token");
            paths.Add(request.RequestUri!.AbsolutePath); methods.Add(request.Method);
            if (mode == "switch") state.Service = "other-host";
            if (mode == "denied") return new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new ProblemDetails("https://relaxkonos.app/problems/elevation-required","tailscale.elevation_required",403,null,null)) };
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            Check(!body.Contains("actor",StringComparison.OrdinalIgnoreCase) && !body.Contains("session",StringComparison.OrdinalIgnoreCase));
            if (request.RequestUri.AbsolutePath.EndsWith("/execute")) Check(body.Contains("true",StringComparison.Ordinal));
            if (request.RequestUri.AbsolutePath.EndsWith("/challenge")) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new TailscaleLoginChallengeResponse(id,LoginFake.Url,operation.ExpiresAt)) };
            if (request.RequestUri.AbsolutePath.EndsWith("/recovery")) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new TailscaleLoginRecoverySnapshot(id,new(new('B',64),new('C',64),"Running",false),true,ExpiresAt:operation.ExpiresAt)) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(operation) };
        }));
        var client = new RemoteTailscaleLoginClient(http,session); var connection = client.CaptureConnection();
        await client.PrepareAsync(connection,id,default);
        await client.ExecuteAsync(connection,id,default);
        await client.GetAsync(connection,id,default);
        Check((await client.ChallengeAsync(connection,id,default)).AuthenticationUrl == LoginFake.Url);
        Check((await client.RecoveryAsync(connection,id,default)).CanClose);
        await client.CloseAsync(connection,new(id,new('B',64),true),default);
        var root = TailscaleEndpoints.LoginOperations + "/" + id.ToString("D");
        Check(paths.SequenceEqual(new[] { TailscaleEndpoints.LoginOperations,root+"/execute",root,root+"/challenge",root+"/recovery",root+"/recovery/close" }));
        Check(methods.SequenceEqual(new[] { HttpMethod.Post,HttpMethod.Post,HttpMethod.Get,HttpMethod.Get,HttpMethod.Get,HttpMethod.Post }));
        using var diagnostics = new NetworkDiagnosticsService(new DeveloperModeService(),() => session);
        var capture = typeof(NetworkDiagnosticsService).GetMethod("ShouldCapture",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Check(!(bool)capture.Invoke(diagnostics,[new Uri("http://test.invalid"+root+"/challenge")])!);
        Check((bool)capture.Invoke(diagnostics,[new Uri("http://test.invalid"+root)])!);
        mode = "denied";
        try { await client.ExecuteAsync(connection,id,default); throw new Exception("Denied login accepted"); }
        catch (RelaxKonOSAuthException ex) { Check(ex.Status == 403); }
        mode = "switch";
        try { await client.ChallengeAsync(connection,id,default); throw new Exception("Stale challenge accepted"); }
        catch (RelaxKonOSAuthException ex) { Check(ex.Type.EndsWith("/settings.connection_changed")); }
        var before = paths.Count;
        try { await client.ExecuteAsync(connection,id,default); throw new Exception("Stale login submitted"); }
        catch (RelaxKonOSAuthException) { Check(paths.Count == before); }
        return count;
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => handle(request);
    }
}
