using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;

namespace RelaxKonOS.Server.ProxyServer;

/// <summary>Published-state reconciliation and expiry. No engine registry, drafts API, certificate loader or management facade.</summary>
public sealed class ProxyServerPublicationService(ProxyServerStore store,
    IPrivilegedOperationTransport helper, IServerModeResolver mode) : IProxyServerExpiryExecutor
{
    public async Task<ProxyServerStatus> StatusAsync(CancellationToken ct)
    {
        if (!mode.Supports(ServerHostFeature.Proxy) || mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System)
            return new(false, false, false, false, null, null, false, "proxy_server.platform_unsupported");
        try { return await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerStatus, null, ct); }
        catch (ProxyServerException ex) { return new(true, false, false, false, null, null, false, ex.Message); }
    }

    public async Task ReconcileExpiryAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await ReadStateAsync(ct);
            var problem = await ReconcilePublishedAsync(state, ct);
            if (problem is not null) throw new ProxyServerException(problem);
        }
        finally { store.Gate.Release(); }
    }

    internal async Task<string?> ReconcilePublishedAsync(ProxyServerState state, CancellationToken ct)
    {
        if (state.Published is null) return null;
        var now = DateTimeOffset.UtcNow;
        var live = state.Published.Credentials.Where(grant =>
            ProxyServerStore.FindEnabledCredential(state, grant) is { } credential
            && (credential.ExpiresAt is null || credential.ExpiresAt > now)).ToArray();
        if (live.Length == state.Published.Credentials.Count) return null;
        // Expiry must never publish unrelated, saved-but-unpublished inbound edits or newly created credentials.
        return await PublishAsync(state, state.Published with { Credentials = live }, ct);
    }

    internal async Task<string?> PublishAsync(ProxyServerState state, ProxyServerMaterial material, CancellationToken ct, bool rollback = false)
    {
        var status = await StatusAsync(ct);
        if (!status.Supported) return "proxy_server.platform_unsupported";
        if (!status.Installed) return "proxy_server.runtime_unavailable";
        if (status.RecoveryRequired) return "proxy_server.recovery_required";
        if (status.ProblemCode == "proxy_server.configuration_drift") return status.ProblemCode;
        var rendered = SingBoxConfiguration.Render(material);
        var operationId = Guid.NewGuid();
        // Persist the candidate before the Helper can commit it. A Server crash after the Helper
        // completes must not leave its applied revision permanently detached from the domain state.
        await store.WriteAsync(state with { PendingPublication = material, PendingRollback = rollback,
            PendingPublicationOperationId = operationId, PendingPreviousHash = status.ConfigurationHash }, ct);
        // A durable candidate is accepted work. Finish the original Helper call and commit its
        // result even when the expiry worker or the management caller is stopping.
        var updated = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerApply, new(Material: material),
            CancellationToken.None, operationId);
        if (updated.ConfigurationHash != Hash(rendered) || updated.RecoveryRequired) return "proxy_server.publication_unconfirmed";
        await store.WriteAsync(state with { PreviousPublished = rollback ? null : state.Published, Published = material,
            PendingPublication = null, PendingRollback = false, PendingPublicationOperationId = null, PendingPreviousHash = null }, CancellationToken.None);
        return null;
    }

    // Called with the domain gate held. Reconcile only a confirmed committed or compensated
    // Helper transaction; uncertain state remains blocked until explicit recovery succeeds.
    internal async Task<ProxyServerState> ReadStateAsync(CancellationToken ct, bool recoverPublication = false)
    {
        var state = await store.ReadAsync(ct);
        if (state.PendingPublication is not { } candidate) return state;
        if (state.PendingPublicationOperationId is not { } operationId || operationId == Guid.Empty)
            throw new ProxyServerException("proxy_server.recovery_required");
        ProxyServerPublicationReceipt receipt;
        try { receipt = await CallAsync<ProxyServerPublicationReceipt>(PrivilegedOperationKind.ProxyServerPublicationReceipt, null, ct, operationId); }
        catch (ProxyServerException) { throw new ProxyServerException("proxy_server.recovery_required"); }
        var candidateHash = Hash(SingBoxConfiguration.Render(candidate, false));
        if (receipt.OperationId != operationId || receipt.CandidateHash != candidateHash || receipt.PreviousHash != state.PendingPreviousHash)
            throw new ProxyServerException("proxy_server.recovery_required");
        if (recoverPublication && receipt.Outcome == ProxyServerPublicationOutcome.InFlight)
        {
            ct.ThrowIfCancellationRequested();
            var compensated = await CallAsync<ProxyServerPublicationReceipt>(PrivilegedOperationKind.ProxyServerRecoverPublication,
                null, CancellationToken.None, operationId);
            if (compensated != (receipt with { Outcome = ProxyServerPublicationOutcome.Compensated }))
                throw new ProxyServerException("proxy_server.recovery_required");
            receipt = compensated;
            ct = CancellationToken.None;
        }
        var status = await StatusAsync(ct);
        if (!status.Installed || status.RecoveryRequired || status.ProblemCode is not null)
            throw new ProxyServerException("proxy_server.recovery_required");
        if (receipt.Outcome == ProxyServerPublicationOutcome.Committed && status.ConfigurationHash == candidateHash)
            state = state with { PreviousPublished = state.PendingRollback ? null : state.Published, Published = candidate };
        else if (receipt.Outcome is not (ProxyServerPublicationOutcome.Compensated or ProxyServerPublicationOutcome.Rejected)
            || status.ConfigurationHash != receipt.PreviousHash)
            throw new ProxyServerException("proxy_server.recovery_required");
        var completed = state with { PendingPublication = null, PendingRollback = false,
            PendingPublicationOperationId = null, PendingPreviousHash = null };
        await store.WriteAsync(completed, CancellationToken.None);
        return completed;
    }

    private async Task<T> CallAsync<T>(PrivilegedOperationKind operation, ProxyServerHelperRequest? request, CancellationToken ct, Guid? operationId = null)
    {
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System || !mode.Supports(ServerHostFeature.Proxy))
            throw new ProxyServerException("proxy_server.platform_unsupported");
        var result = await helper.ExecuteAsync(new(operation, ProxyServer: request, OperationId: operationId), ct);
        if (!result.Success || result.OutputBase64 is null) throw new ProxyServerException(result.Error?.StartsWith("proxy_server.", StringComparison.Ordinal) == true
            ? result.Error : "proxy_server.helper_unavailable");
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
                ?? throw new ProxyServerException("proxy_server.helper_invalid_response"); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new ProxyServerException("proxy_server.helper_invalid_response"); }
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
