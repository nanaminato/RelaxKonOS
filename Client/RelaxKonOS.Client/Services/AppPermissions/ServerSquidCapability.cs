using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ServerSquidCapability(AppId appId, IAppPermissionManager permissions,
    ExternalServerConnection connection, Func<IServerSquid>? createClient, CancellationToken lifetime) : IServerSquid
{
    private readonly Lazy<IServerSquid> _client = new(() =>
        createClient?.Invoke() ?? throw new InvalidOperationException("Squid is unavailable."));

    public bool CanManage => !lifetime.IsCancellationRequested && connection.IsCurrent
        && permissions.IsGranted(appId, CoreAppPermissions.ServerSquidRead)
        && permissions.IsGranted(appId, CoreAppPermissions.ServerSquidManage)
        && connection.Session.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller;

    public Task<SquidAccount[]> ReadAccountsAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ReadAccountsAsync(token), manage: false, cancellationToken);

    public Task<SquidConfigurationSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ReadAsync(token), manage: false, cancellationToken);

    public Task<SquidConfigurationPreview> PreviewAsync(SquidConfiguration configuration, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.PreviewAsync(configuration, token), manage: true, cancellationToken);

    public Task<SquidNativeValidation> ValidateAsync(SquidConfiguration configuration, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ValidateAsync(configuration, token), manage: true, cancellationToken);

    public Task<ProxyOperationAcceptedDto> ApplyAsync(ApplySquidConfigurationRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ApplyAsync(request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<ProxyOperationAcceptedDto> ChangeAccountAsync(SquidAccountMutation request, string idempotencyKey, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.ChangeAccountAsync(request, idempotencyKey, token), manage: true, cancellationToken);

    public Task<ProxyOperationAcceptedDto> RunLifecycleAsync(SquidLifecycleRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
        InvokeAsync((client, token) => client.RunLifecycleAsync(request, idempotencyKey, token), manage: true, cancellationToken);

    public async Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken cancellationToken)
    {
        var operation = await InvokeAsync((client, token) => client.OperationAsync(id, token), manage: true, cancellationToken);
        if (operation.OperationId != id || !operation.Kind.StartsWith("squid.", StringComparison.Ordinal))
            throw new InvalidDataException("The server returned an invalid Squid operation.");
        return operation;
    }

    private async Task<T> InvokeAsync<T>(Func<IServerSquid, CancellationToken, Task<T>> operation, bool manage, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        var token = linked.Token;
        CheckAccess(manage, token);
        using var observation = connection.ObserveInvalidation(linked.Cancel);
        var client = _client.Value;
        CheckAccess(manage, token);
        var result = await operation(client, token);
        CheckAccess(manage, token);
        return result;
    }

    private void CheckAccess(bool manage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!connection.IsCurrent)
            throw new InvalidOperationException("The application's Squid connection has changed.");
        if (!permissions.IsGranted(appId, CoreAppPermissions.ServerSquidRead)
            || manage && !CanManage)
            throw new UnauthorizedAccessException();
    }
}
