using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Settings;

internal static class WorkspaceEnvironmentTransportChecks
{
    public static async Task RunAsync()
    {
        var session = DispatchProxy.Create<IAuthSession, SessionProxy>();
        var stub = (SessionProxy)session;
        var calls = 0;
        using var http = new HttpClient(new ResponseHandler(request =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new WorkspaceEnvironmentSnapshot(stub.Workspace.Id, "1", DateTimeOffset.UtcNow, [], EnvironmentPathMode.Append), options: RelaxKonOSJsonOptions.Default) });
        }));
        var client = new WorkspaceEnvironmentClient(http, session);
        var target = client.CaptureConnection();
        await client.ReadAsync(target);
        if (calls != 1) throw new Exception("Workspace read was not dispatched once.");
        stub.OnAcquire = () => stub.Workspace = stub.Workspace with { Id = Guid.NewGuid() };
        try { await client.SaveAsync(target, new("1", new([]), EnvironmentPathMode.Replace)); }
        catch (RelaxKonOSAuthException error) when (error.Status == 409)
        {
            if (calls != 1) throw new Exception("Workspace change during token acquisition dispatched a stale write.");
            Console.WriteLine("PASS: Workspace environment connection target is rechecked after token acquisition; stale writes are not sent.");
            return;
        }
        throw new Exception("Expected Workspace switch refusal.");
    }
}
