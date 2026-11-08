using System.Net;
using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.Proxy;

public enum ProxyTunOperation { Enable, Disable, EmergencyDisable }

public sealed class ProxyLifecycleOperations(ProxyOperationStore operations, IServiceScopeFactory scopes)
{
    public Task<ProxyOperationDto> EnqueueLifecycleAsync(string key, ProxyLifecycleAction action, string actor, CancellationToken ct)
    {
        if (!Enum.IsDefined(action)) throw new ProxyOperationValidationException(ProxyProblemCodes.NotSupported);
        var kind = "lifecycle." + action.ToString().ToLowerInvariant();
        return operations.EnqueueAsync(key, kind, (_, token) => ProxyOperationScope.RunAsync(scopes, actor, kind,
            (services, execution) => services.GetRequiredService<IProxyLifecycleService>().ExecuteLifecycleAsync(action, execution), token), ct);
    }

    public Task<ProxyOperationDto> EnqueueTunAsync(string key, ProxyTunOperation operation, Guid profileId,
        IPAddress? managementAddress, string actor, CancellationToken ct)
    {
        var kind = operation switch { ProxyTunOperation.Enable => "tun.enable", ProxyTunOperation.Disable => "tun.disable",
            ProxyTunOperation.EmergencyDisable => "tun.emergency-disable", _ => throw new ProxyOperationValidationException(ProxyProblemCodes.NotSupported) };
        // The value comes from HttpContext.Connection, not from the request payload.
        var capturedAddress = managementAddress is null ? null : IPAddress.Parse(managementAddress.ToString());
        var requestIdentity = Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(new { kind, profileId, actor })));
        return operations.EnqueueIdentifiedAsync(key, kind, (id, stage, _, token) => ProxyOperationScope.RunAsync(scopes, actor, kind,
            async (services, execution) =>
            {
                if (services.GetService<MihomoManagedTunOperations>() is { } managed)
                {
                    var result = await managed.TryExecuteAsync(id, operation, profileId, capturedAddress, stage, execution);
                    if (result.Managed) return result.Problem;
                }
                var tun = services.GetRequiredService<IProxyTunSafetyService>();
                return await (operation switch
                {
                    ProxyTunOperation.Enable => tun.EnableAsync(profileId, capturedAddress, execution),
                    ProxyTunOperation.Disable => tun.DisableAsync(execution),
                    ProxyTunOperation.EmergencyDisable => tun.EmergencyDisableAsync(execution),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                });
            }, token), ct, requestIdentity);
    }
}
