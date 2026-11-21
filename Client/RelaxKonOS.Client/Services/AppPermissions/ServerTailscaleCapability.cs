using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerTailscaleCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, IServerTailscale? controlClient, IServerTailscaleLogin? loginClient,
    IHostElevationBroker elevation, CancellationToken lifetime) : IExternalTailscale, IServerTailscaleLogin
{
    private IServerTailscale Control => controlClient ?? throw new InvalidOperationException("Tailscale is unavailable.");
    private IServerTailscaleLogin Login => loginClient ?? throw new InvalidOperationException("Tailscale login is unavailable.");

    public event EventHandler? SessionChanged
    {
        add { if (controlClient is not null) controlClient.SessionChanged += value; }
        remove { if (controlClient is not null) controlClient.SessionChanged -= value; }
    }

    public HostSettingsConnection CaptureConnection()
    {
        lifetime.ThrowIfCancellationRequested();
        if (!connection.IsCurrent || connection.Session.ServiceId is not { } host
            || connection.Session.CurrentSession is not { } login || connection.Session.CurrentUser is not { } user)
            throw Problem(409, "tailscale.session_changed");
        var target = new HostSettingsConnection(host, login.Id, user.Id);
        CheckAccess(target, manage: false, lifetime);
        return target;
    }

    public bool IsCurrent(HostSettingsConnection target) =>
        !lifetime.IsCancellationRequested && connection.IsCurrent
        && connection.Session.ServiceId == target.ServiceId
        && connection.Session.CurrentSession?.Id == target.SessionId
        && connection.Session.CurrentUser?.Id == target.UserId;

    public async Task<TailscaleStatusDto> GetStatusAsync(HostSettingsConnection target, CancellationToken ct)
    {
        var result = await InvokeAsync(target, token => Control.GetStatusAsync(target, token), false, false, ct);
        return CanManage ? result : result with
        {
            Peers = null, PeersRestricted = true, Account = null, TailnetName = null,
            MagicDnsSuffix = null, ExitNode = null,
            Self = result.Self is { } self ? self with { Owner = null, Tags = null } : null,
        };
    }

    public Task<bool> RequestPeerAccessAsync(HostSettingsConnection target, CancellationToken ct) =>
        InvokeAsync(target, token => elevation.RequestGrantAsync(HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, token),
            manage: true, elevate: false, ct);

    public Task<TailscaleControlSnapshot> GetControlAsync(HostSettingsConnection target, CancellationToken ct) =>
        WithGrantAsync(target, token => Control.GetControlAsync(target, token), ct);

    public Task<TailscaleOperation> PrepareAsync(HostSettingsConnection target, TailscalePrepareRequest request, CancellationToken ct) =>
        WithGrantAsync(target, token => Control.PrepareAsync(target, request, token), ct);

    public Task<TailscaleOperation> ExecuteAsync(HostSettingsConnection target, Guid operationId, CancellationToken ct) =>
        WithGrantAsync(target, token => Control.ExecuteAsync(target, operationId, token), ct);

    public Task<TailscaleOperation> GetOperationAsync(HostSettingsConnection target, Guid operationId, CancellationToken ct) =>
        InvokeAsync(target, token => Control.GetOperationAsync(target, operationId, token), false, false, ct);

    public Task<TailscaleRecoverySnapshot> ReadRecoveryAsync(HostSettingsConnection target, Guid operationId, CancellationToken ct) =>
        WithGrantAsync(target, token => Control.ReadRecoveryAsync(target, operationId, token), ct);

    public Task<TailscaleOperation> CloseAsync(HostSettingsConnection target, TailscaleCloseRequest request, CancellationToken ct) =>
        WithGrantAsync(target, token => Control.CloseAsync(target, request, token), ct);

    Task<TailscaleLoginOperation> IServerTailscaleLogin.PrepareAsync(HostSettingsConnection target, Guid id, CancellationToken ct) =>
        WithGrantAsync(target, token => Login.PrepareAsync(target, id, token), ct);

    Task<TailscaleLoginOperation> IServerTailscaleLogin.ExecuteAsync(HostSettingsConnection target, Guid id, CancellationToken ct) =>
        WithGrantAsync(target, token => Login.ExecuteAsync(target, id, token), ct);

    Task<TailscaleLoginOperation> IServerTailscaleLogin.GetAsync(HostSettingsConnection target, Guid id, CancellationToken ct) =>
        InvokeAsync(target, token => Login.GetAsync(target, id, token), false, false, ct);

    Task<TailscaleLoginChallengeResponse> IServerTailscaleLogin.ChallengeAsync(HostSettingsConnection target, Guid id, CancellationToken ct) =>
        WithGrantAsync(target, token => Login.ChallengeAsync(target, id, token), ct);

    Task<TailscaleLoginRecoverySnapshot> IServerTailscaleLogin.RecoveryAsync(HostSettingsConnection target, Guid id, CancellationToken ct) =>
        WithGrantAsync(target, token => Login.RecoveryAsync(target, id, token), ct);

    Task<TailscaleLoginOperation> IServerTailscaleLogin.CloseAsync(HostSettingsConnection target, TailscaleLoginCloseRequest request, CancellationToken ct) =>
        WithGrantAsync(target, token => Login.CloseAsync(target, request, token), ct);

    private bool CanManage => permissions.IsGranted(appId, CoreAppPermissions.ServerTailscaleManage) && connection.IsController;

    private Task<T> WithGrantAsync<T>(HostSettingsConnection target, Func<CancellationToken, Task<T>> request, CancellationToken ct) =>
        InvokeAsync(target, request, manage: true, elevate: true, ct);

    private async Task<T> InvokeAsync<T>(HostSettingsConnection target, Func<CancellationToken, Task<T>> request,
        bool manage, bool elevate, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        var token = linked.Token;
        CheckAccess(target, manage, token);
        using var observation = connection.ObserveInvalidation(linked.Cancel);
        var result = elevate
            ? await elevation.ExecuteAsync(HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource, RunAsync, token)
            : await RunAsync();
        CheckAccess(target, manage, token);
        return result;

        Task<T> RunAsync()
        {
            CheckAccess(target, manage, token);
            return request(token);
        }
    }

    private void CheckAccess(HostSettingsConnection target, bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrent(target)) throw Problem(409, "tailscale.session_changed");
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerTailscaleRead) || manage && !CanManage)
            throw Problem(403, "tailscale.permission_denied");
    }

    private static RelaxKonOSAuthException Problem(int status, string code) =>
        new(new ProblemDetails("https://relaxkonos.app/problems/" + code, code, status, null, null));
}
