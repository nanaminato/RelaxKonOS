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

public sealed class ProxyServerException(string code) : Exception(code);

public sealed class ProxyServerService(ProxyServerStore store, IPrivilegedOperationTransport helper,
    IServerModeResolver mode, IProxyEngineRegistry engines, IProxyServerCertificateSource? certificates = null)
{
    public async Task<ProxyServerSnapshot> ReadAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var status = await StatusAsync(ct);
            var hash = state.Published is null ? null : Hash(SingBoxConfiguration.Render(state.Published, false));
            var applied = status.Applied && status.ConfigurationHash == hash;
            return new(state.Configuration, state.Credentials.Select(c => new ProxyServerCredential(c.Id, c.Name, c.InboundId,
                c.Enabled, c.ExpiresAt, applied && state.Published!.Credentials.Any(p => p.Id == c.Id && p.Value == c.Value))).ToArray(),
                status with { Applied = applied }, state.Published?.Configuration.Revision);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ProxyServerStatus> StatusAsync(CancellationToken ct)
    {
        if (!mode.Supports(ServerHostFeature.Proxy) || mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System)
            return new(false, false, false, false, null, null, false, "proxy_server.platform_unsupported");
        try { return await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerStatus, null, ct); }
        catch (ProxyServerException ex) { return new(true, false, false, false, null, null, false, ex.Message); }
    }

    public async Task<string?> SaveAsync(SaveProxyServerConfiguration request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (request.ExpectedRevision != state.Configuration.Revision) return "proxy_server.revision_conflict";
            var next = new ProxyServerConfiguration(checked(state.Configuration.Revision + 1), request.Inbounds);
            if (!SingBoxConfiguration.Validate(next).Valid) return "proxy_server.config_invalid";
            if (state.Credentials.Any(c => !next.Inbounds.Any(i => i.Id == c.InboundId))) return "proxy_server.inbound_has_credentials";
            if (state.Credentials.Any(c => state.Configuration.Inbounds.Single(i => i.Id == c.InboundId).Protocol != next.Inbounds.Single(i => i.Id == c.InboundId).Protocol))
                return "proxy_server.inbound_has_credentials";
            var keys = state.Keys.Where(k => next.Inbounds.Any(i => i.Id == k.InboundId)).ToList();
            foreach (var inbound in next.Inbounds.Where(i => i.Protocol == ProxyServerProtocol.VlessReality && !keys.Any(k => k.InboundId == i.Id)))
                keys.Add((await CallAsync<ProxyServerRealityKey>(PrivilegedOperationKind.ProxyServerGenerateKeys, null, ct)) with { InboundId = inbound.Id });
            await store.WriteAsync(state with { Configuration = next, Keys = keys.ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> CredentialAsync(ProxyServerCredentialRequest request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || state.Credentials.Length >= 1024 && request.Id is null
                || !state.Configuration.Inbounds.Any(i => i.Id == request.InboundId)) return "proxy_server.credential_invalid";
            var prior = request.Id is null ? null : state.Credentials.SingleOrDefault(c => c.Id == request.Id);
            if (request.Id is not null && prior is null) return "proxy_server.credential_not_found";
            if (prior is not null && prior.InboundId != request.InboundId) return "proxy_server.credential_invalid";
            var protocol = state.Configuration.Inbounds.Single(i => i.Id == request.InboundId).Protocol;
            var value = protocol is ProxyServerProtocol.Trojan or ProxyServerProtocol.Hysteria2
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : Guid.NewGuid().ToString("D");
            var credential = new StoredCredential(prior?.Id ?? Guid.NewGuid(), request.Name.Trim(), request.InboundId,
                prior is null || request.Rotate ? value : prior.Value, request.Enabled, request.ExpiresAt);
            await store.WriteAsync(state with { Credentials = state.Credentials.Where(c => c.Id != credential.Id).Append(credential).ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    private async Task<ProxyServerMaterial> MaterialAsync(ProxyServerState state, CancellationToken ct)
    {
        var tls = new List<ProxyServerTlsMaterial>();
        foreach (var inbound in state.Configuration.Inbounds.Where(i => i.Enabled && i.Protocol != ProxyServerProtocol.VlessReality))
            tls.Add(await (certificates ?? throw new ProxyServerException("proxy_server.certificate_invalid")).ReadAsync(inbound, ct));
        return new(state.Configuration, state.Credentials.Where(c => c.Enabled && (c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow))
            .Select(c => new ProxyServerSecret(c.Id, c.InboundId, c.Value)).ToArray(), state.Keys, tls);
    }

    public async Task<ProxyServerValidation> ValidateAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try { return await CallAsync<ProxyServerValidation>(PrivilegedOperationKind.ProxyServerValidate, new(Material: await MaterialAsync(await store.ReadAsync(ct), ct)), ct); }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> ApplyAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try { return await ApplyCoreAsync(await store.ReadAsync(ct), ct); }
        finally { store.Gate.Release(); }
    }

    private async Task<string?> ApplyCoreAsync(ProxyServerState state, CancellationToken ct)
    {
        await CheckNetworkAsync(ct);
        var material = await MaterialAsync(state, ct);
        var status = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerApply, new(Material: material), ct);
        var hash = Hash(SingBoxConfiguration.Render(material));
        if (status.ConfigurationHash != hash || status.RecoveryRequired) return "proxy_server.publication_unconfirmed";
        await store.WriteAsync(state with { Published = material }, ct);
        return null;
    }

    public async Task<string?> ActionAsync(ProxyServerAction action, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            if (!Enum.IsDefined(action)) return "proxy_server.action_invalid";
            if (action is ProxyServerAction.Start or ProxyServerAction.Restart)
            {
                // Remove expired/revoked users before restarting a retained configuration.
                var problem = await ApplyCoreAsync(await store.ReadAsync(ct), ct);
                if (problem is not null) return problem;
            }
            await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerAction, new(action), ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    private async Task CheckNetworkAsync(CancellationToken ct)
    {
        var engine = engines.Find("mihomo");
        if (engine is not null && (await engine.GetHealthAsync(ct)).TunState != ProxyTunState.Disabled)
            throw new ProxyServerException("proxy_server.network_conflict");
    }

    public async Task<ProxyServerExport> ExportAsync(ProxyServerExportRequest request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var credential = state.Credentials.SingleOrDefault(c => c.Id == request.CredentialId && c.Enabled
                && (c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow)) ?? throw new ProxyServerException("proxy_server.credential_unavailable");
            var material = state.Published ?? throw new ProxyServerException("proxy_server.configuration_not_applied");
            var published = material.Credentials.SingleOrDefault(c => c.Id == credential.Id && c.Value == credential.Value)
                ?? throw new ProxyServerException("proxy_server.configuration_not_applied");
            var status = await StatusAsync(ct);
            if (!status.Applied || status.ConfigurationHash != Hash(SingBoxConfiguration.Render(material, false))) throw new ProxyServerException("proxy_server.configuration_not_applied");
            return ProxyServerClientExport.Create(material, published, credential.Name, request.Format);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ProxyOperationDto> EnqueueAsync(string actor, string key, string kind, object request,
        ProxyOperationStore operations, Func<CancellationToken, Task<string?>> action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ProxyServerException("proxy_server.idempotency_key_required");
        var scope = Hash(actor + "\n" + key);
        var fingerprint = Hash(kind + "\n" + JsonSerializer.Serialize(request));
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Receipts.TryGetValue(scope, out var receipt))
            {
                if (receipt.Fingerprint != fingerprint) throw new ProxyServerException("proxy_server.idempotency_conflict");
                if (receipt.OperationId != Guid.Empty)
                    return await operations.GetAsync(receipt.OperationId, ct) ?? throw new ProxyServerException("proxy_server.operation_unavailable");
            }
            else
            {
                if (state.Receipts.Count >= 10000) throw new ProxyServerException("proxy_server.operation_limit");
                state.Receipts.Add(scope, new(fingerprint, Guid.Empty));
                await store.WriteAsync(state, CancellationToken.None);
            }
            var item = await operations.EnqueueAsync("proxy-server:" + scope, "proxy_server." + kind, async (_, token) =>
            {
                // Yielding inside the ledger gives us time to persist the receipt while still holding the domain lock.
                try { return await action(token); }
                catch (ProxyServerException ex) { return ex.Message; }
            }, ct);
            state.Receipts[scope] = new(fingerprint, item.OperationId);
            await store.WriteAsync(state, CancellationToken.None);
            return item;
        }
        finally { store.Gate.Release(); }
    }

    public async Task ReconcileExpiryAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Published is null || !state.Published.Credentials.Any(p => !state.Credentials.Any(c => c.Id == p.Id && c.Enabled && (c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow)))) return;
            // Expiry must never publish unrelated, saved-but-unpublished inbound edits or newly created credentials.
            var expired = state.Published.Credentials.Where(p => state.Credentials.Any(c => c.Id == p.Id && c.Enabled && c.Value == p.Value
                && (c.ExpiresAt is null || c.ExpiresAt > DateTimeOffset.UtcNow))).ToArray();
            var material = state.Published with { Credentials = expired };
            var status = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerApply, new(Material: material), ct);
            if (status.ConfigurationHash != Hash(SingBoxConfiguration.Render(material))) throw new ProxyServerException("proxy_server.publication_unconfirmed");
            await store.WriteAsync(state with { Published = material }, ct);
        }
        finally { store.Gate.Release(); }
    }

    private async Task<T> CallAsync<T>(PrivilegedOperationKind operation, ProxyServerHelperRequest? request, CancellationToken ct)
    {
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System || !mode.Supports(ServerHostFeature.Proxy)) throw new ProxyServerException("proxy_server.platform_unsupported");
        var result = await helper.ExecuteAsync(new(operation, ProxyServer: request), ct);
        if (!result.Success || result.OutputBase64 is null) throw new ProxyServerException(result.Error?.StartsWith("proxy_server.", StringComparison.Ordinal) == true
            ? result.Error : "proxy_server.helper_unavailable");
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64)) ?? throw new ProxyServerException("proxy_server.helper_invalid_response"); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new ProxyServerException("proxy_server.helper_invalid_response"); }
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed class ProxyServerExpiryService(ProxyServerService service, ILogger<ProxyServerExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await service.ReconcileExpiryAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Proxy server credential expiry could not be applied; inspect runtime and recovery state."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
