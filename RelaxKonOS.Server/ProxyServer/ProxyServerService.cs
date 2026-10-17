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

/// <summary>
/// Domain service for the Proxy Server application. It owns drafts, principals, credentials,
/// subscription tokens and the publication transaction; the platform adapter (Helper) owns the
/// managed kernel, its service identity and its files.
/// </summary>
public sealed class ProxyServerService(
    ProxyServerStore store,
    IPrivilegedOperationTransport helper,
    IServerModeResolver mode,
    IProxyEngineRegistry engines,
    IProxyServerCertificateSource? certificates = null)
{
    private static readonly TimeSpan ReceiptLifetime = TimeSpan.FromDays(7);

    public ProxyServerCapabilities Capabilities()
    {
        var supported = mode.Mode == RelaxKonOS.Protocol.Common.ServerMode.System
            && mode.Supports(ServerHostFeature.Proxy) && SingBoxManifest.Current is not null;
        return new(supported, SingBoxManifest.PlatformName, SingBoxManifest.ArchitectureName, SingBoxManifest.Version,
            supported ? SingBoxManifest.ProtocolCapabilities : [],
            Statistics: false, Logs: false, ServiceControl: supported, BootAutostart: supported,
            Uninstall: supported, SubscriptionHosting: supported);
    }

    public async Task<ProxyServerSnapshot> ReadAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var status = await StatusAsync(ct);
            var hash = state.Published is null ? null : Hash(SingBoxConfiguration.Render(state.Published, false));
            var applied = status.Applied && status.ConfigurationHash == hash;
            var credentials = state.Credentials.Select(c => new ProxyServerCredential(c.Id, c.PrincipalId, c.Name,
                c.InboundIds, c.Enabled, c.ExpiresAt, applied && state.Published!.Credentials.Any(p =>
                    p.Id == c.Id && p.Value == c.Value && p.InboundIds.Order().SequenceEqual(c.InboundIds.Order())))).ToArray();
            var principals = state.Principals.Select(p => new ProxyServerPrincipal(p.Id, p.DisplayName, p.RelaxKonOSUserId,
                credentials.Count(c => c.PrincipalId == p.Id))).ToArray();
            var subscriptions = state.Subscriptions.Select(s => new ProxyServerSubscription(s.Id, s.CredentialId,
                s.Formats, s.CreatedAt, s.ExpiresAt, s.Revoked)).ToArray();
            return new(state.Configuration, principals, credentials, subscriptions,
                status with { Applied = applied }, Capabilities(), state.Published?.Configuration.Revision,
                state.PreviousPublished?.Configuration.Revision);
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

    public async Task<IReadOnlyList<ProxyServerRuntimeRelease>> ReleasesAsync(CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        return SingBoxManifest.Releases.Select(release => new ProxyServerRuntimeRelease(
                SingBoxManifest.Version, release.Platform, release.Architecture, release.Sha256,
                status.Installed && status.Version == SingBoxManifest.Version
                    && release.Platform == SingBoxManifest.PlatformName && release.Architecture == SingBoxManifest.ArchitectureName,
                SingBoxManifest.VerifiedProtocols))
            .ToArray();
    }

    public async Task<string?> SaveAsync(SaveProxyServerConfiguration request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (request.ExpectedRevision != state.Configuration.Revision) return "proxy_server.revision_conflict";
            var next = new ProxyServerConfiguration(checked(state.Configuration.Revision + 1), request.Inbounds ?? [], request.SubscriptionBaseUrl);
            var validation = SingBoxConfiguration.Validate(next);
            if (!validation.Valid) return validation.Problems[0];
            foreach (var credential in state.Credentials)
            {
                // A credential outlives the draft only while every inbound it names still exists with the same secret shape.
                if (credential.InboundIds.Any(id => next.Inbounds.All(i => i.Id != id))) return "proxy_server.inbound_has_credentials";
                var protocols = credential.InboundIds.Select(id => next.Inbounds.Single(i => i.Id == id).Protocol).ToArray();
                if (!ProxyServerPresets.Shareable(protocols)) return "proxy_server.inbound_has_credentials";
            }
            var keys = state.Keys.Where(k => next.Inbounds.Any(i => i.Id == k.InboundId)).ToList();
            foreach (var inbound in next.Inbounds.Where(i => i.Protocol == ProxyServerProtocol.VlessReality && !keys.Any(k => k.InboundId == i.Id)))
                keys.Add((await CallAsync<ProxyServerRealityKey>(PrivilegedOperationKind.ProxyServerGenerateKeys, null, ct)) with { InboundId = inbound.Id });
            await store.WriteAsync(state with { Configuration = next, Keys = keys.ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> SavePrincipalAsync(SaveProxyServerPrincipal request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var name = request.DisplayName?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 128 || request.RelaxKonOSUserId is { Length: > 256 }) return "proxy_server.principal_invalid";
            var state = await store.ReadAsync(ct);
            var id = request.Id ?? Guid.NewGuid();
            if (request.Id is not null && state.Principals.All(p => p.Id != id)) return "proxy_server.principal_not_found";
            var principal = new StoredPrincipal(id, name, string.IsNullOrWhiteSpace(request.RelaxKonOSUserId) ? null : request.RelaxKonOSUserId.Trim());
            await store.WriteAsync(state with { Principals = state.Principals.Where(p => p.Id != id).Append(principal).ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> DeletePrincipalAsync(Guid id, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Principals.All(p => p.Id != id)) return "proxy_server.principal_not_found";
            if (state.Credentials.Any(c => c.PrincipalId == id)) return "proxy_server.principal_has_credentials";
            await store.WriteAsync(state with { Principals = state.Principals.Where(p => p.Id != id).ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> SaveCredentialAsync(ProxyServerCredentialRequest request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var name = request.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 128 || state.Credentials.Length >= 1024 && request.Id is null)
                return "proxy_server.credential_invalid";
            if (state.Principals.All(p => p.Id != request.PrincipalId)) return "proxy_server.principal_not_found";
            var inboundIds = (request.InboundIds ?? []).Distinct().ToArray();
            if (inboundIds.Length == 0 || inboundIds.Any(id => state.Configuration.Inbounds.All(i => i.Id != id)))
                return "proxy_server.credential_invalid";
            var protocols = inboundIds.Select(id => state.Configuration.Inbounds.Single(i => i.Id == id).Protocol).ToArray();
            if (!ProxyServerPresets.Shareable(protocols)) return "proxy_server.credential_invalid";

            var prior = request.Id is null ? null : state.Credentials.SingleOrDefault(c => c.Id == request.Id);
            if (request.Id is not null && prior is null) return "proxy_server.credential_not_found";
            var value = prior is not null && prior.InboundIds.Order().SequenceEqual(inboundIds.Order())
                && ProxyServerPresets.CredentialKind(protocols[0]) == SecretKind(prior.Value)
                ? prior.Value
                : Generate(ProxyServerPresets.CredentialKind(protocols[0]));
            var credential = new StoredCredential(prior?.Id ?? Guid.NewGuid(), request.PrincipalId, name, inboundIds,
                value, request.Enabled, request.ExpiresAt?.ToUniversalTime());
            await store.WriteAsync(state with { Credentials = state.Credentials.Where(c => c.Id != credential.Id).Append(credential).ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> RotateCredentialAsync(Guid id, ProxyServerCredentialRotation? rotation, CancellationToken ct)
    {
        rotation ??= new();
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var prior = state.Credentials.SingleOrDefault(c => c.Id == id);
            if (prior is null) return "proxy_server.credential_not_found";
            var protocols = prior.InboundIds.Select(inboundId => state.Configuration.Inbounds.Single(i => i.Id == inboundId).Protocol).ToArray();
            var rotated = prior with
            {
                Value = Generate(ProxyServerPresets.CredentialKind(protocols[0])),
                Enabled = rotation.Enabled ?? prior.Enabled,
                ExpiresAt = rotation.ClearExpiry ? null : rotation.ExpiresAt?.ToUniversalTime() ?? prior.ExpiresAt
            };
            await store.WriteAsync(state with { Credentials = state.Credentials.Where(c => c.Id != id).Append(rotated).ToArray() }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> DeleteCredentialAsync(Guid id, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Credentials.All(c => c.Id != id)) return "proxy_server.credential_not_found";
            await store.WriteAsync(state with
            {
                Credentials = state.Credentials.Where(c => c.Id != id).ToArray(),
                // Deleting a credential revokes every subscription that handed it out.
                Subscriptions = state.Subscriptions.Select(s => s.CredentialId == id ? s with { Revoked = true } : s).ToArray()
            }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ProxyServerValidation> ValidateAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var problems = SingBoxConfiguration.Validate(state.Configuration).Problems.ToList();
            ProxyServerMaterial? material = null;
            string preview;
            try
            {
                material = await MaterialAsync(state, ct);
                preview = SingBoxConfiguration.Projection(material);
            }
            catch (ProxyServerException ex)
            {
                problems.Add(ex.Message);
                preview = SingBoxConfiguration.Projection(state.Configuration);
            }
            if (problems.Count > 0) return new(false, problems.Distinct().ToArray(), preview);
            if (!(await StatusAsync(ct)).Installed) return new(true, [], preview);
            try
            {
                var native = await CallAsync<ProxyServerValidation>(PrivilegedOperationKind.ProxyServerValidate, new(Material: material), ct);
                return native with { Preview = preview };
            }
            catch (ProxyServerException ex) { return new(false, [ex.Message], preview); }
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> ApplyAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            await CheckNetworkAsync(ct);
            return await PublishAsync(state, await MaterialAsync(state, ct), ct);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> RollbackAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Published is null || state.PreviousPublished is null) return "proxy_server.rollback_unavailable";
            var status = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerAction,
                new(Action: ProxyServerAction.Rollback), ct);
            if (status.ConfigurationHash != Hash(SingBoxConfiguration.Render(state.PreviousPublished, false)) || status.RecoveryRequired)
                return "proxy_server.publication_unconfirmed";
            await store.WriteAsync(state with { Published = state.PreviousPublished, PreviousPublished = null }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> RecoverAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var status = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerAction,
                new(Action: ProxyServerAction.Recover), ct);
            return status.RecoveryRequired ? "proxy_server.recovery_required" : null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> ActionAsync(ProxyServerAction action, CancellationToken ct)
    {
        if (!Enum.IsDefined(action)) return "proxy_server.action_invalid";
        if (action is ProxyServerAction.Install or ProxyServerAction.Uninstall or ProxyServerAction.Upgrade or ProxyServerAction.Rollback)
            return "proxy_server.action_invalid";
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (action is ProxyServerAction.Start or ProxyServerAction.Restart)
            {
                // Expired or revoked credentials are removed before a retained configuration is started again.
                var problem = await ReconcilePublishedAsync(state, ct);
                if (problem is not null) return problem;
                await CheckNetworkAsync(ct);
            }
            await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerAction, new(Action: action), ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    public Task<string?> InstallAsync(CancellationToken ct) => InstallCoreAsync(ProxyServerAction.Install, ct);

    public async Task<string?> UpgradeAsync(string version, CancellationToken ct)
    {
        if (version != SingBoxManifest.Version) return "proxy_server.release_unsupported";
        var problem = await InstallCoreAsync(ProxyServerAction.Upgrade, ct);
        if (problem is not null) return problem;
        // A version change must be re-validated against the current draft before it is trusted.
        return SingBoxConfiguration.Validate((await store.ReadAsync(ct)).Configuration).Valid ? null : "proxy_server.config_invalid";
    }

    public async Task<string?> UninstallAsync(bool keepData, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var status = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerAction,
                new(Action: ProxyServerAction.Uninstall, KeepData: keepData), ct);
            if (status.Installed) return "proxy_server.uninstall_failed";
            if (!keepData) await store.WriteAsync(ProxyServerState.Empty, ct);
            else if (state.Published is not null) await store.WriteAsync(state with { Published = null, PreviousPublished = null }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    private async Task<string?> InstallCoreAsync(ProxyServerAction action, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var status = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerAction, new(Action: action), ct);
            return status.Installed && status.Version == SingBoxManifest.Version ? null : "proxy_server.runtime_unavailable";
        }
        finally { store.Gate.Release(); }
    }

    public async Task<IReadOnlyList<ProxyServerExportFormatOption>> ExportFormatsAsync(Guid credentialId, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var material = state.Published ?? throw new ProxyServerException("proxy_server.configuration_not_applied");
            var secret = PublishedSecret(state, material, credentialId);
            return ProxyServerClientExport.Formats(material, secret);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ProxyServerExport> ExportAsync(ProxyServerExportRequest request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            var material = state.Published ?? throw new ProxyServerException("proxy_server.configuration_not_applied");
            var secret = PublishedSecret(state, material, request.CredentialId);
            await EnsureAppliedAsync(material, ct);
            var name = state.Credentials.Single(c => c.Id == request.CredentialId).Name;
            return ProxyServerClientExport.Create(material, secret, name, request.Format);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<IReadOnlyList<ProxyServerSubscription>> SubscriptionsAsync(CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            return state.Subscriptions.Select(s => new ProxyServerSubscription(s.Id, s.CredentialId, s.Formats, s.CreatedAt, s.ExpiresAt, s.Revoked)).ToArray();
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ProxyServerSubscriptionIssued> IssueSubscriptionAsync(IssueProxyServerSubscription request, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (!SingBoxConfiguration.BaseUrl(state.Configuration.SubscriptionBaseUrl) || state.Configuration.SubscriptionBaseUrl is null)
                throw new ProxyServerException("proxy_server.subscription_base_invalid");
            var credential = state.Credentials.SingleOrDefault(c => c.Id == request.CredentialId)
                ?? throw new ProxyServerException("proxy_server.credential_not_found");
            var formats = (request.Formats ?? []).Distinct().ToArray();
            if (formats.Length == 0 || formats.Any(f => !ProxyServerExportFormats.IsKnown(f)))
                throw new ProxyServerException("proxy_server.export_unsupported");
            var now = DateTimeOffset.UtcNow;
            if (request.ExpiresAt is { } expires && expires <= now) throw new ProxyServerException("proxy_server.subscription_expiry_invalid");
            var token = Token();
            var subscription = new StoredSubscription(Guid.NewGuid(), credential.Id, Hash(token), formats, now,
                request.ExpiresAt?.ToUniversalTime(), false);
            if (state.Subscriptions.Length >= 1024) throw new ProxyServerException("proxy_server.subscription_limit");
            await store.WriteAsync(state with { Subscriptions = state.Subscriptions.Append(subscription).ToArray() }, ct);
            var baseUrl = state.Configuration.SubscriptionBaseUrl.TrimEnd('/');
            var urls = formats.Select(format => $"{baseUrl}{ProxyServerRoutes.PublicSubscriptionRoot}/{token}/{format}").ToArray();
            return new(new ProxyServerSubscription(subscription.Id, subscription.CredentialId, subscription.Formats,
                subscription.CreatedAt, subscription.ExpiresAt, false), token, urls);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<string?> RevokeSubscriptionAsync(Guid id, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Subscriptions.All(s => s.Id != id)) return "proxy_server.subscription_not_found";
            await store.WriteAsync(state with
            {
                Subscriptions = state.Subscriptions.Select(s => s.Id == id ? s with { Revoked = true } : s).ToArray()
            }, ct);
            return null;
        }
        finally { store.Gate.Release(); }
    }

    /// <summary>Anonymous read. Every failure is the same problem code so a token cannot be probed.</summary>
    public async Task<ProxyServerExport> SubscriptionContentAsync(string token, string format, CancellationToken ct)
    {
        if (!ProxyServerExportFormats.IsKnown(format) || token.Length is < 40 or > 64)
            throw new ProxyServerException("proxy_server.subscription_invalid");
        var hash = Hash(token);
        await store.Gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var state = await store.ReadAsync(ct);
            var subscription = state.Subscriptions.SingleOrDefault(s => s.TokenHash == hash && !s.Revoked)
                ?? throw new ProxyServerException("proxy_server.subscription_invalid");
            if (subscription.ExpiresAt is { } expires && expires <= now) throw new ProxyServerException("proxy_server.subscription_invalid");
            if (!subscription.Formats.Contains(format)) throw new ProxyServerException("proxy_server.subscription_invalid");
            var credential = state.Credentials.SingleOrDefault(c => c.Id == subscription.CredentialId)
                ?? throw new ProxyServerException("proxy_server.subscription_invalid");
            if (!credential.Enabled || credential.ExpiresAt is { } credentialExpiry && credentialExpiry <= now)
                throw new ProxyServerException("proxy_server.credential_unavailable");
            var material = state.Published ?? throw new ProxyServerException("proxy_server.subscription_invalid");
            var secret = PublishedSecret(state, material, credential.Id);
            // A subscription never publishes nodes the runtime has not confirmed.
            await EnsureAppliedAsync(material, ct);
            return ProxyServerClientExport.Create(material, secret, credential.Name, format);
        }
        finally { store.Gate.Release(); }
    }

    public Task<ProxyOperationDto?> OperationAsync(Guid id, ProxyOperationStore operations, CancellationToken ct) =>
        operations.GetAsync(id, ct);

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
                if (DateTimeOffset.UtcNow - receipt.IssuedAt > ReceiptLifetime || receipt.Fingerprint != fingerprint)
                    throw new ProxyServerException("proxy_server.idempotency_conflict");
                if (receipt.OperationId != Guid.Empty)
                    return await operations.GetAsync(receipt.OperationId, ct) ?? throw new ProxyServerException("proxy_server.operation_unavailable");
            }
            else
            {
                if (state.Receipts.Count >= 10000) throw new ProxyServerException("proxy_server.operation_limit");
                state.Receipts.Add(scope, new(fingerprint, Guid.Empty, DateTimeOffset.UtcNow));
                await store.WriteAsync(state, CancellationToken.None);
            }
            var item = await operations.EnqueueAsync("proxy-server:" + scope, "proxy_server." + kind, async (_, token) =>
            {
                try { return await action(token); }
                catch (ProxyServerException ex) { return ex.Message; }
            }, ct);
            // Re-read under the same gate: the queued action may already have committed a newer revision.
            var latest = await store.ReadAsync(ct);
            latest.Receipts[scope] = new(fingerprint, item.OperationId, DateTimeOffset.UtcNow);
            await store.WriteAsync(latest, CancellationToken.None);
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
            var problem = await ReconcilePublishedAsync(state, ct);
            if (problem is not null) throw new ProxyServerException(problem);
        }
        finally { store.Gate.Release(); }
    }

    private async Task<string?> ReconcilePublishedAsync(ProxyServerState state, CancellationToken ct)
    {
        if (state.Published is null) return null;
        var now = DateTimeOffset.UtcNow;
        var live = state.Published.Credentials.Where(s => state.Credentials.Any(c => c.Id == s.Id && c.Enabled && c.Value == s.Value
            && (c.ExpiresAt is null || c.ExpiresAt > now))).ToArray();
        if (live.Length == state.Published.Credentials.Count) return null;
        // Expiry must never publish unrelated, saved-but-unpublished inbound edits or newly created credentials.
        return await PublishAsync(state, state.Published with { Credentials = live }, ct);
    }

    private async Task<string?> PublishAsync(ProxyServerState state, ProxyServerMaterial material, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (!status.Supported) return "proxy_server.platform_unsupported";
        if (!status.Installed) return "proxy_server.runtime_unavailable";
        if (status.RecoveryRequired) return "proxy_server.recovery_required";
        if (status.ProblemCode == "proxy_server.configuration_drift") return status.ProblemCode;
        var rendered = SingBoxConfiguration.Render(material);
        var updated = await CallAsync<ProxyServerStatus>(PrivilegedOperationKind.ProxyServerApply, new(Material: material), ct);
        if (updated.ConfigurationHash != Hash(rendered) || updated.RecoveryRequired) return "proxy_server.publication_unconfirmed";
        await store.WriteAsync(state with { PreviousPublished = state.Published, Published = material }, ct);
        return null;
    }

    private async Task EnsureAppliedAsync(ProxyServerMaterial material, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (!status.Applied || status.ConfigurationHash != Hash(SingBoxConfiguration.Render(material, false)))
            throw new ProxyServerException("proxy_server.configuration_not_applied");
    }

    private static ProxyServerSecret PublishedSecret(ProxyServerState state, ProxyServerMaterial material, Guid credentialId)
    {
        var credential = state.Credentials.SingleOrDefault(c => c.Id == credentialId)
            ?? throw new ProxyServerException("proxy_server.credential_not_found");
        var now = DateTimeOffset.UtcNow;
        if (!credential.Enabled || credential.ExpiresAt is { } expires && expires <= now)
            throw new ProxyServerException("proxy_server.credential_unavailable");
        // The applied grant set wins: editing a draft must not widen what a client can download.
        return material.Credentials.SingleOrDefault(s => s.Id == credentialId && s.Value == credential.Value)
            ?? throw new ProxyServerException("proxy_server.configuration_not_applied");
    }

    private async Task<ProxyServerMaterial> MaterialAsync(ProxyServerState state, CancellationToken ct)
    {
        var tls = new List<ProxyServerTlsMaterial>();
        foreach (var inbound in state.Configuration.Inbounds.Where(i => i.Enabled && ProxyServerPresets.RequiresCertificate(i.Protocol)))
            tls.Add(await (certificates ?? throw new ProxyServerException("proxy_server.certificate_invalid")).ReadAsync(inbound, ct));
        var now = DateTimeOffset.UtcNow;
        return new(state.Configuration, state.Credentials
            .Where(c => c.Enabled && (c.ExpiresAt is null || c.ExpiresAt > now))
            .Select(c => new ProxyServerSecret(c.Id, c.InboundIds, c.Value)).ToArray(), state.Keys, tls);
    }

    private async Task CheckNetworkAsync(CancellationToken ct)
    {
        var engine = engines.Find("mihomo");
        if (engine is not null && (await engine.GetHealthAsync(ct)).TunState != ProxyTunState.Disabled)
            throw new ProxyServerException("proxy_server.network_conflict");
    }

    private static ProxyServerCredentialKind SecretKind(string value) =>
        Guid.TryParseExact(value, "D", out _) ? ProxyServerCredentialKind.Uuid : ProxyServerCredentialKind.Password;

    private static string Generate(ProxyServerCredentialKind kind) => kind == ProxyServerCredentialKind.Uuid
        ? Guid.NewGuid().ToString("D")
        : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<T> CallAsync<T>(PrivilegedOperationKind operation, ProxyServerHelperRequest? request, CancellationToken ct)
    {
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.System || !mode.Supports(ServerHostFeature.Proxy))
            throw new ProxyServerException("proxy_server.platform_unsupported");
        var result = await helper.ExecuteAsync(new(operation, ProxyServer: request), ct);
        if (!result.Success || result.OutputBase64 is null) throw new ProxyServerException(result.Error?.StartsWith("proxy_server.", StringComparison.Ordinal) == true
            ? result.Error : "proxy_server.helper_unavailable");
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(result.OutputBase64), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
                ?? throw new ProxyServerException("proxy_server.helper_invalid_response"); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new ProxyServerException("proxy_server.helper_invalid_response"); }
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
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
            catch (ProxyServerException) { logger.LogInformation("Proxy server credential expiry is not applied yet; the runtime or a pending transaction reported a problem."); }
            catch (Exception) { logger.LogWarning("Proxy server credential expiry could not be applied; inspect runtime and recovery state."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
