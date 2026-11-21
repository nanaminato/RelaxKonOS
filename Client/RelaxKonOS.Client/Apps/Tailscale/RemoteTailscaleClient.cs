using HostSettingsConnection = RelaxKonOS.AppSDK.HostSettingsConnection;
using IServerTailscale = RelaxKonOS.AppSDK.IServerTailscale;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.Client.Apps.Tailscale;



/// <summary>Every request and response is bound to the original host, user and login session.</summary>
public sealed class RemoteTailscaleClient(HttpClient http, IAuthSession session) : HostSettingsService(http, session), IServerTailscale
{
    public Task<TailscaleStatusDto> GetStatusAsync(HostSettingsConnection connection, CancellationToken ct)
        => SendAsync<TailscaleStatusDto>(connection, HttpMethod.Get, TailscaleEndpoints.Status, null, ct);
    public Task<TailscaleControlSnapshot> GetControlAsync(HostSettingsConnection connection, CancellationToken ct)
        => SendAsync<TailscaleControlSnapshot>(connection, HttpMethod.Get, TailscaleEndpoints.Control, null, ct);
    public Task<TailscaleOperation> PrepareAsync(HostSettingsConnection connection, TailscalePrepareRequest request, CancellationToken ct)
        => SendAsync<TailscaleOperation>(connection, HttpMethod.Post, TailscaleEndpoints.Operations, request, ct);
    public Task<TailscaleOperation> ExecuteAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct)
        => SendAsync<TailscaleOperation>(connection, HttpMethod.Post, OperationRoute(TailscaleEndpoints.Execute, operationId), new TailscaleExecuteRequest(true), ct);
    public Task<TailscaleOperation> GetOperationAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct)
        => SendAsync<TailscaleOperation>(connection, HttpMethod.Get, OperationRoute(TailscaleEndpoints.Operation, operationId), null, ct);
    public Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(HostSettingsConnection connection, Guid operationId, CancellationToken ct)
        => SendAsync<TailscaleRecoverySnapshot>(connection, HttpMethod.Get, OperationRoute(TailscaleEndpoints.Recovery, operationId), null, ct);
    public Task<TailscaleOperation> CloseAsync(HostSettingsConnection connection, TailscaleCloseRequest request, CancellationToken ct)
        => SendAsync<TailscaleOperation>(connection, HttpMethod.Post, OperationRoute(TailscaleEndpoints.Close, request.OperationId), request, ct);
    private static string OperationRoute(string template, Guid operationId) => template.Replace("{operationId:guid}", operationId.ToString("D"), StringComparison.Ordinal);
}
