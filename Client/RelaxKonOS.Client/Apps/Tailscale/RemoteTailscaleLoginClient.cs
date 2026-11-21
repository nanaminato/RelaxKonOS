using HostSettingsConnection = RelaxKonOS.AppSDK.HostSettingsConnection;
using IServerTailscaleLogin = RelaxKonOS.AppSDK.IServerTailscaleLogin;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Protocol.Tailscale;

namespace RelaxKonOS.Client.Apps.Tailscale;



public sealed class RemoteTailscaleLoginClient(HttpClient http, IAuthSession session) : HostSettingsService(http, session), IServerTailscaleLogin
{
    public Task<TailscaleLoginOperation> PrepareAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
        => SendAsync<TailscaleLoginOperation>(connection, HttpMethod.Post, TailscaleEndpoints.LoginOperations, new TailscaleLoginPrepareRequest(id), ct);
    public Task<TailscaleLoginOperation> ExecuteAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
        => SendAsync<TailscaleLoginOperation>(connection, HttpMethod.Post, Route(TailscaleEndpoints.LoginExecute, id), new TailscaleLoginExecuteRequest(true), ct);
    public Task<TailscaleLoginOperation> GetAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
        => SendAsync<TailscaleLoginOperation>(connection, HttpMethod.Get, Route(TailscaleEndpoints.LoginOperation, id), null, ct);
    public Task<TailscaleLoginChallengeResponse> ChallengeAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
        => SendAsync<TailscaleLoginChallengeResponse>(connection, HttpMethod.Get, Route(TailscaleEndpoints.LoginChallenge, id), null, ct);
    public Task<TailscaleLoginRecoverySnapshot> RecoveryAsync(HostSettingsConnection connection, Guid id, CancellationToken ct)
        => SendAsync<TailscaleLoginRecoverySnapshot>(connection, HttpMethod.Get, Route(TailscaleEndpoints.LoginRecovery, id), null, ct);
    public Task<TailscaleLoginOperation> CloseAsync(HostSettingsConnection connection, TailscaleLoginCloseRequest request, CancellationToken ct)
        => SendAsync<TailscaleLoginOperation>(connection, HttpMethod.Post, Route(TailscaleEndpoints.LoginClose, request.OperationId), request, ct);
    private static string Route(string template, Guid id) => template.Replace("{operationId:guid}", id.ToString("D"), StringComparison.Ordinal);
}
