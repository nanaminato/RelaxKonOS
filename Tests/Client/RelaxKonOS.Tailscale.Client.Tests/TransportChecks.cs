using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Apps.Tailscale;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Protocol.Workspace;

internal static class TransportChecks
{
    internal static async Task<int> RunAsync()
    {
        var count=0;
        void Check(bool condition) { if(!condition) throw new Exception("Tailscale transport assertion " + count); count++; }
        var session=DispatchProxy.Create<IAuthSession,Session>(); var state=(Session)(object)session;
        var mode="normal"; var requests=0; var paths=new List<string>();
        var operationId=Guid.NewGuid();
        var operation=new TailscaleOperation(operationId,TailscaleAction.Connect,TailscaleOperationState.Prepared,new('A',64),DateTimeOffset.UtcNow.AddMinutes(5),DateTimeOffset.UtcNow);
        using var http=new HttpClient(new Handler(async request=>
        {
            requests++; paths.Add(request.RequestUri!.AbsolutePath);
            Check(request.Headers.Authorization?.Scheme=="Bearer" && request.Headers.Authorization.Parameter=="test-token");
            if(mode=="switch") state.Service="different-host";
            if(mode=="denied") return new(HttpStatusCode.Forbidden) { Content=JsonContent.Create(new ProblemDetails("https://relaxkonos.app/problems/elevation-required","tailscale.elevation_required",403,null,null)) };
            if(request.Method==HttpMethod.Post)
            {
                var body=await request.Content!.ReadAsStringAsync();
                Check(!body.Contains("command") && !body.Contains("password"));
                if(request.RequestUri.AbsolutePath.EndsWith("/execute")) Check(body.Contains("confirmed",StringComparison.OrdinalIgnoreCase) && body.Contains("true"));
                if(request.RequestUri.AbsolutePath.EndsWith("/recovery/close"))
                {
                    Check(!body.Contains("actor",StringComparison.OrdinalIgnoreCase));
                    return new(HttpStatusCode.OK) {Content=JsonContent.Create(operation with { State=TailscaleOperationState.Unknown,
                        Closure=new(operationId,new(new('A',64),false,true,new(true,false),true),DateTimeOffset.UtcNow,session.CurrentUser!.Id.ToString("D")) })};
                }
                return new(HttpStatusCode.OK) {Content=JsonContent.Create(operation)};
            }
            if(request.RequestUri.AbsolutePath.EndsWith("/recovery"))
                return new(HttpStatusCode.OK) {Content=JsonContent.Create(new TailscaleRecoverySnapshot(operationId,new(new('A',64),false,true,new(true,false),true),true,ExpiresAt:DateTimeOffset.UtcNow.AddMinutes(5)))};
            return new(HttpStatusCode.OK) {Content= request.RequestUri.AbsolutePath==TailscaleEndpoints.Status
                ? JsonContent.Create(new TailscaleStatusDto("observed")) : request.RequestUri.AbsolutePath==TailscaleEndpoints.Control
                ? JsonContent.Create(new TailscaleControlSnapshot(new('A',64),false,true,new(true,false),true)) : JsonContent.Create(operation)};
        }));
        var client=new RemoteTailscaleClient(http,session);
        var connection=client.CaptureConnection();
        Check((await client.GetStatusAsync(connection,default)).State=="observed");
        Check((await client.GetControlAsync(connection,default)).CanControl);
        Check((await client.PrepareAsync(connection,new(operationId,new('A',64),TailscaleAction.Connect),default)).OperationId==operationId);
        Check((await client.ExecuteAsync(connection,operationId,default)).OperationId==operationId);
        Check((await client.GetOperationAsync(connection,operationId,default)).OperationId==operationId);
        Check(http.BaseAddress is null && paths.SequenceEqual(new[]{TailscaleEndpoints.Status,TailscaleEndpoints.Control,TailscaleEndpoints.Operations,
            TailscaleEndpoints.Operations+"/"+operationId.ToString("D")+"/execute",TailscaleEndpoints.Operations+"/"+operationId.ToString("D")}));
        Check((await client.ReadRecoveryAsync(connection,operationId,default)).CanClose);
        var closed=await client.CloseAsync(connection,new(operationId,new('A',64),true),default);
        Check(closed.State==TailscaleOperationState.Unknown && closed.Closure?.Actor==connection.UserId.ToString("D"));
        Check(paths.TakeLast(2).SequenceEqual(new[]{TailscaleEndpoints.Operations+"/"+operationId.ToString("D")+"/recovery",TailscaleEndpoints.Operations+"/"+operationId.ToString("D")+"/recovery/close"}));
        mode="denied";
        try { await client.GetControlAsync(connection,default); throw new Exception("Denied request accepted"); }
        catch(RelaxKonOSAuthException ex) { Check(ex.Status==403 && ex.Type.EndsWith("/elevation-required")); }
        mode="switch";
        try { await client.GetStatusAsync(connection,default); throw new Exception("Stale response accepted"); }
        catch(RelaxKonOSAuthException ex) { Check(ex.Type.EndsWith("/settings.connection_changed")); }
        var before=requests;
        try { await client.ExecuteAsync(connection,operationId,default); throw new Exception("Stale write accepted"); }
        catch(RelaxKonOSAuthException) { Check(requests==before); }
        return count;
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handle):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>handle(request);
    }
    public class Session:DispatchProxy
    {
        internal string Service="test-host";
        private readonly Guid userId=Guid.NewGuid();
        private readonly SessionDto login=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,SessionStatus.Active,Guid.NewGuid(),"test",DateTimeOffset.UtcNow);
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name switch
        {
            "get_State"=>AuthSessionState.Authenticated,"get_ServiceId"=>Service,"get_EffectiveBaseUrl"=>"http://test.invalid/",
            "get_CurrentSession"=>login,"get_CurrentUser"=>new UserDto(userId,"test",HostPlatformKind.Windows,"test",DateTimeOffset.UtcNow,null),
            "GetAccessTokenAsync"=>Task.FromResult<string?>("test-token"),
            "add_StateChanged" or "remove_StateChanged"=>null,
            _=>throw new NotSupportedException(method.Name)
        };
    }
}
