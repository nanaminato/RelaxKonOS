using System.Collections.Concurrent;
using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Settings;

public sealed class HostNetworkOperationStore
{
    internal readonly ConcurrentDictionary<Guid, (string Actor, DateTimeOffset Deadline, string? Checkpoint, bool Ready)> Pending = new();
}

public sealed class HostNetworkService(IPrivilegedOperationTransport transport, IHostElevationSessionStore grants,
    HostNetworkOperationStore store, ILogger<HostNetworkService> logger)
{
    public const string Resource = "host/network";
    public async Task<HostNetworkSnapshot> ReadAsync(CancellationToken ct)
    {
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.HostNetworkRead, OperationId: Guid.NewGuid()), ct);
        if (!result.Success || result.HostNetwork is null) throw new SettingsException(503, "settings.network.read_failed");
        return result.HostNetwork;
    }

    public async Task<HostNetworkApplyResult> ApplyAsync(ClaimsPrincipal actor, HostNetworkApplyRequest request, CancellationToken ct)
    {
        Authorize(actor);
        if (request.OperationId == Guid.Empty || string.IsNullOrEmpty(request.ExpectedRevision)
            || request.Change is null || !HostNetworkValidation.IsValid(request.Change))
            throw new SettingsException(400, "settings.network.invalid_configuration");
        foreach (var old in store.Pending.Where(pair => pair.Value.Deadline < DateTimeOffset.UtcNow.AddMinutes(-5)))
            store.Pending.TryRemove(old.Key, out _);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        if (!store.Pending.TryAdd(request.OperationId, (Identity(actor), deadline, null, false)))
            throw new SettingsException(409, "settings.network.operation_exists");
        logger.LogInformation("Remote network change started. OperationId={OperationId}", request.OperationId);
        // A disconnected HTTP caller cannot cancel the Helper's independent recovery task.
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.HostNetworkApply,
            ExpectedRevision: request.ExpectedRevision, OperationId: request.OperationId, NetworkChange: request.Change), CancellationToken.None);
        store.Pending[request.OperationId] = (Identity(actor), deadline, result.NetworkCheckpoint, result.Success);
        if (!result.Success)
        {
            logger.LogWarning("Remote network change failed. OperationId={OperationId} Problem={Problem}", request.OperationId, result.ProblemCode);
            throw new SettingsException(result.ProblemCode == PrivilegedProblemCode.Conflict ? 409 : 503, "settings.network.apply_failed");
        }
        return new(request.OperationId, deadline);
    }

    public async Task<HostNetworkConfirmed> ConfirmAsync(ClaimsPrincipal actor, Guid operationId, CancellationToken ct)
    {
        Authorize(actor);
        if (!store.Pending.TryGetValue(operationId, out var pending) || pending.Actor != Identity(actor)
            || !pending.Ready || pending.Deadline <= DateTimeOffset.UtcNow) throw new SettingsException(409, "settings.network.confirm_expired");
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.HostNetworkConfirm, OperationId: operationId,
            NetworkCheckpoint: pending.Checkpoint), ct);
        if (!result.Success) throw new SettingsException(409, "settings.network.confirm_expired");
        // Keep the receipt until pruning; a repeated apply with this ID must never mutate the NIC again.
        store.Pending[operationId] = (pending.Actor, pending.Deadline, pending.Checkpoint, false);
        logger.LogInformation("Remote network change confirmed. OperationId={OperationId}", operationId);
        return new(true);
    }
    private void Authorize(ClaimsPrincipal actor)
    {
        if (!grants.IsGranted(actor, HostElevationCapability.HostNetworkChange, Resource))
            throw new SettingsException(403, "settings.elevation_required");
    }
    private static string Identity(ClaimsPrincipal actor) => actor.FindFirst("sub")?.Value
        ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new SettingsException(401, "settings.unauthenticated");
}
