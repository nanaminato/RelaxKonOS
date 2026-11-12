using System.Security.Claims;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Files;

internal interface ICloudFileOperationModule
{
    Task RunAsync(ClaimsPrincipal user, Func<HttpClient, CancellationToken, Task> transfer, CancellationToken ct);
}

internal sealed class CloudFileOperationModule(ModuleHostSupervisor hosts) : ICloudFileOperationModule
{
    public async Task RunAsync(ClaimsPrincipal user, Func<HttpClient, CancellationToken, Task> transfer, CancellationToken ct)
    {
        if (!hosts.CanAccess(CloudStorageApi.ModuleId, user))
            throw new InvalidOperationException("Cloud storage is unavailable to this user.");
        using var connection = hosts.AcquireConnection(CloudStorageApi.ModuleId)
            ?? throw new IOException("The cloud storage module is offline.");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Stopping);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = connection.Endpoint,
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.Add(ModuleHostProtocol.ServiceTokenHeader, connection.ServiceToken);
        client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-User",
            user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub")!);
        client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-Role",
            ServerModuleManagementEndpoints.CanManage(user) ? "admin" : "user");
        try { await transfer(client, stopping.Token); }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new IOException("The cloud module stopped or the request timed out.", error);
        }
    }
}
