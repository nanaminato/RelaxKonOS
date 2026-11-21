using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class PortForwardingCapability(AppId appId, IAppPermissionManager permissions,
    IPortForwardingService service, CancellationToken lifetime) : IPortForwardingService
{
    public event EventHandler? ForwardsChanged
    {
        add { CheckAccess(); service.ForwardsChanged += value; }
        remove => service.ForwardsChanged -= value;
    }

    public IReadOnlyList<PortForwardInfo> List()
    {
        CheckAccess();
        return service.List();
    }

    public PortForwardingSettings GetSettings()
    {
        CheckAccess();
        return service.GetSettings();
    }

    public void SaveSettings(PortForwardingSettings settings)
    {
        CheckAccess();
        service.SaveSettings(settings);
    }

    public Task<PortForwardInfo> StartAsync(PortForwardRequest request, CancellationToken cancellationToken) =>
        StartAsync(request, null, cancellationToken);

    public Task<PortForwardInfo> StartAsync(PortForwardRequest request, string? password, CancellationToken cancellationToken) =>
        InvokeAsync(token => service.StartAsync(request, password, token), cancellationToken);

    public Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, CancellationToken cancellationToken) =>
        UpdateAsync(id, request, null, cancellationToken);

    public Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, string? password, CancellationToken cancellationToken) =>
        InvokeAsync(token => service.UpdateAsync(id, request, password, token), cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken) =>
        InvokeAsync(token => service.RemoveAsync(id, token), cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        CheckAccess(linked.Token);
        var result = await operation(linked.Token);
        CheckAccess(linked.Token);
        return result;
    }

    private async Task InvokeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        CheckAccess(linked.Token);
        await operation(linked.Token);
    }

    private void CheckAccess(CancellationToken cancellationToken = default)
    {
        lifetime.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        if (!permissions.IsGranted(appId, CoreAppPermissions.DesktopPortForwardingManage))
            throw new UnauthorizedAccessException();
    }
}
