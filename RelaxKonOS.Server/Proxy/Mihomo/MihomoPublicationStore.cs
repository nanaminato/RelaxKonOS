using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoPendingPublication(Guid OperationId, MihomoPublishedRuntime Candidate, string Identity,
    [property: JsonRequired] RuntimeConfigurationPlan? ConfigurationPlan,
    [property: JsonRequired] RuntimeChangePlan? InstallationPlan,
    [property: JsonRequired] RuntimeChangePlan? RemovalPlan,
    [property: JsonRequired] MihomoSettingsPublication? Settings,
    [property: JsonRequired] MihomoProfilePublication? Profile);
public sealed record MihomoPublicationCompletion(Guid OperationId, string RequestHash, bool Confirmed, bool NotStarted);

/// <summary>Server-private encrypted state, never returned by ordinary management reads.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoPublicationState(int SchemaVersion, long Revision, MihomoPublishedRuntime? Published,
    string? Identity, Guid? LastOperationId, MihomoPendingPublication? Pending,
    [property: JsonRequired] MihomoPublicationCompletion? LastConfiguration,
    [property: JsonRequired] MihomoPublicationCompletion? LastInstallation,
    [property: JsonRequired] MihomoPublicationCompletion? LastRemoval)
{
    public static MihomoPublicationState Empty => new(1, 0, null, null, null, null, null, null, null);
}

/// <summary>
/// Publication authority for runtime switches. All domain writers and lifecycle callers must hold Gate.
/// Starting a publication never authorizes a binary switch; only an observed, confirmed identity does.
/// </summary>
public sealed class MihomoPublicationStore(IProxyPlatformPaths paths, IDataProtectionProvider protection)
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private readonly IDataProtector protector = protection.CreateProtector("RelaxKonOS.Mihomo.Publication.v1");
    private string StatePath => Path.Combine(paths.GetStateDirectory(), "mihomo-publication.protected");
    private const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>Uncoordinated file writers cannot bypass an established or unresolved authority. An
    /// unreadable authority is unknown, never evidence that the old direct-write path is available.</summary>
    public async Task<string?> UncoordinatedWriteProblemAsync(CancellationToken ct)
    {
        try
        {
            var state = await ReadAsync(ct);
            return state.Pending is not null ? RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.RecoveryRequired
                : state.Published is not null ? RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.NotSupported : null;
        }
        catch (Exception ex) when (ex is RuntimePolicyException or IOException or UnauthorizedAccessException)
        { return RelaxKonOS.Protocol.Proxy.ProxyProblemCodes.RecoveryRequired; }
    }

    public async Task<MihomoPublicationState> ReadAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(StatePath);
        if (!File.Exists(StatePath)) return MihomoPublicationState.Empty;
        await using var input = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is 0 or > MaximumBytes) throw new RuntimePolicyException("runtime.publication_state_invalid");
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, ct);
        try
        {
            var plain = protector.Unprotect(bytes);
            try
            {
                var state = RuntimeJson.Read<MihomoPublicationState>(plain);
                Validate(state);
                return state;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { throw new RuntimePolicyException("runtime.publication_state_invalid"); }
    }

    public Task<MihomoPublicationState> BeginAsync(Guid operationId, long expectedRevision,
        MihomoPublishedRuntime candidate, CancellationToken ct) => BeginCoreAsync(operationId, expectedRevision, candidate, null, null, null, ct);

    public Task<MihomoPublicationState> BeginConfigurationAsync(Guid operationId, long expectedRevision,
        MihomoPublishedRuntime candidate, RuntimeConfigurationPlan plan, CancellationToken ct, MihomoSettingsPublication? settings = null, MihomoProfilePublication? profile = null) =>
        BeginCoreAsync(operationId, expectedRevision, candidate, plan, null, null, ct, settings, profile);

    public Task<MihomoPublicationState> BeginInstallationAsync(Guid operationId, long expectedRevision,
        MihomoPublishedRuntime candidate, RuntimeChangePlan plan, CancellationToken ct) =>
        BeginCoreAsync(operationId, expectedRevision, candidate, null, plan, null, ct);

    public async Task<MihomoPublicationState> BeginRemovalAsync(Guid operationId, long expectedRevision,
        RuntimeChangePlan plan, MihomoSettingsPublication settings, CancellationToken ct)
    {
        MihomoSettingsPublicationFiles.ValidateRemoval(settings);
        var current = await ReadAsync(ct);
        if (current.Published is not { } published) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        return await BeginCoreAsync(operationId, expectedRevision, published, null, null, plan, ct, settings);
    }

    private async Task<MihomoPublicationState> BeginCoreAsync(Guid operationId, long expectedRevision,
        MihomoPublishedRuntime candidate, RuntimeConfigurationPlan? plan, RuntimeChangePlan? installationPlan,
        RuntimeChangePlan? removalPlan, CancellationToken ct, MihomoSettingsPublication? settings = null, MihomoProfilePublication? profile = null)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        MihomoRuntimeMaterial.Validate(candidate);
        var current = await ReadAsync(ct);
        var identity = Identity(candidate.Yaml);
        if (current.Pending is { } pending)
        {
            if (pending.OperationId == operationId && RuntimeJson.Serialize(pending.Candidate).AsSpan().SequenceEqual(RuntimeJson.Serialize(candidate))
                && RuntimeJson.Serialize(pending.ConfigurationPlan).AsSpan().SequenceEqual(RuntimeJson.Serialize(plan)))
            {
                if (RuntimeJson.Serialize(pending.InstallationPlan).AsSpan().SequenceEqual(RuntimeJson.Serialize(installationPlan))
                    && RuntimeJson.Serialize(pending.RemovalPlan).AsSpan().SequenceEqual(RuntimeJson.Serialize(removalPlan))
                    && RuntimeJson.Serialize(pending.Settings).AsSpan().SequenceEqual(RuntimeJson.Serialize(settings))
                    && RuntimeJson.Serialize(pending.Profile).AsSpan().SequenceEqual(RuntimeJson.Serialize(profile))) return current;
                throw new RuntimePolicyException("runtime.idempotency_conflict");
            }
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        }
        if (current.Revision != expectedRevision || current.LastOperationId == operationId)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        var next = current with { Revision = checked(current.Revision + 1), Pending = new(operationId, candidate, identity, plan, installationPlan, removalPlan, settings, profile) };
        await SaveAsync(next, ct);
        return next;
    }

    /// <summary>The caller supplies the identity proved by the fixed Helper, never a client acknowledgement.</summary>
    public async Task<MihomoPublicationState> ConfirmAsync(Guid operationId, string observedIdentity, CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current.Pending is null && current.LastOperationId == operationId && current.Identity == observedIdentity) return current;
        if (current.Pending is not { } pending || pending.OperationId != operationId || pending.Identity != observedIdentity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        if (pending.RemovalPlan is not null) throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var next = current with { Revision = checked(current.Revision + 1), Published = pending.Candidate,
            Identity = pending.Identity, LastOperationId = operationId, Pending = null,
            LastConfiguration = pending.ConfigurationPlan is null ? null : new(operationId, ConfigurationRequestHash(pending.ConfigurationPlan, pending.Candidate, pending.Settings, pending.Profile), true, false),
            LastInstallation = pending.InstallationPlan is null ? current.LastInstallation
                : new(operationId, InstallationRequestHash(pending.InstallationPlan, pending.Candidate), true, false) };
        await SaveAsync(next, ct);
        return next;
    }

    /// <summary>Rollback can close a publication only after the previous identity has been re-observed.</summary>
    public async Task<MihomoPublicationState> AbortAsync(Guid operationId, string? restoredIdentity, bool notStarted, CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current.Pending?.OperationId != operationId || restoredIdentity != current.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var next = current with { Revision = checked(current.Revision + 1), Pending = null,
            LastConfiguration = current.Pending.ConfigurationPlan is null ? current.LastConfiguration
                : new(operationId, ConfigurationRequestHash(current.Pending.ConfigurationPlan, current.Pending.Candidate, current.Pending.Settings, current.Pending.Profile), false, notStarted),
            LastInstallation = current.Pending.InstallationPlan is null ? current.LastInstallation
                : new(operationId, InstallationRequestHash(current.Pending.InstallationPlan, current.Pending.Candidate), false, notStarted),
            LastRemoval = current.Pending.RemovalPlan is null ? current.LastRemoval
                : new(operationId, RemovalRequestHash(current.Pending.RemovalPlan), false, notStarted) };
        await SaveAsync(next, ct);
        return next;
    }

    /// <summary>Called only after the exact successful removal receipt, absence and retained/deleted identity are measured.</summary>
    public async Task<MihomoPublicationState> CompleteRemovalAsync(Guid operationId, string? observedIdentity, CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current.Pending is not { RemovalPlan: { } plan } pending || pending.OperationId != operationId
            || observedIdentity != (plan.Request.KeepData ? current.Identity : null))
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var next = current with { Revision = checked(current.Revision + 1), Pending = null,
            Published = plan.Request.KeepData ? current.Published : null,
            Identity = plan.Request.KeepData ? current.Identity : null,
            LastOperationId = plan.Request.KeepData ? current.LastOperationId : null,
            LastConfiguration = plan.Request.KeepData ? current.LastConfiguration : null,
            LastInstallation = plan.Request.KeepData ? current.LastInstallation : null,
            LastRemoval = new(operationId, RemovalRequestHash(plan), true, false) };
        await SaveAsync(next, ct);
        return next;
    }

    private async Task SaveAsync(MihomoPublicationState state, CancellationToken ct)
    {
        Validate(state);
        var directory = Path.GetDirectoryName(StatePath)!;
        RuntimeSourceStore.RequireNoLinks(directory);
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".publication");
        var plain = RuntimeJson.Serialize(state);
        try
        {
            var bytes = protector.Protect(plain);
            if (bytes.Length > MaximumBytes) throw new RuntimePolicyException("runtime.publication_state_invalid");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, options)) { await output.WriteAsync(bytes, ct); output.Flush(true); }
            RuntimeSourceStore.RequireNoLinks(StatePath);
            File.Move(temporary, StatePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); File.Delete(temporary); }
    }

    public static string Identity(string yaml) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
    public static string ConfigurationRequestHash(RuntimeConfigurationPlan plan, MihomoPublishedRuntime candidate, MihomoSettingsPublication? settings = null, MihomoProfilePublication? profile = null) =>
        Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(new { Plan = plan, Candidate = candidate, Settings = settings, Profile = profile })));
    public static string InstallationRequestHash(RuntimeChangePlan plan, MihomoPublishedRuntime candidate) =>
        Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(new { Plan = plan, Candidate = candidate })));
    public static string RemovalRequestHash(RuntimeChangePlan plan) => Convert.ToHexString(SHA256.HashData(RuntimeJson.Serialize(plan)));

    private static void Validate(MihomoPublicationState state)
    {
        if (state.SchemaVersion != 1 || state.Revision < 0 || (state.Published is null) != (state.Identity is null)
            || (state.Published is null) != (state.LastOperationId is null) || state.LastOperationId == Guid.Empty)
            throw new RuntimePolicyException("runtime.publication_state_invalid");
        if (state.LastConfiguration is { } completed && (completed.OperationId == Guid.Empty
            || completed.RequestHash is not { Length: 64 } || !completed.RequestHash.All(Uri.IsHexDigit) || state.Published is null
            || completed.Confirmed && completed.NotStarted))
            throw new RuntimePolicyException("runtime.publication_state_invalid");
        if (state.LastInstallation is { } installed && (installed.OperationId == Guid.Empty
            || installed.RequestHash is not { Length: 64 } || !installed.RequestHash.All(Uri.IsHexDigit)
            || installed.Confirmed && (installed.NotStarted || state.Published is null)))
            throw new RuntimePolicyException("runtime.publication_state_invalid");
        if (state.LastRemoval is { } removed && (removed.OperationId == Guid.Empty
            || removed.RequestHash is not { Length: 64 } || !removed.RequestHash.All(Uri.IsHexDigit) || removed.Confirmed && removed.NotStarted))
            throw new RuntimePolicyException("runtime.publication_state_invalid");
        if (state.Published is { } published)
        {
            MihomoRuntimeMaterial.Validate(published);
            if (Identity(published.Yaml) != state.Identity) throw new RuntimePolicyException("runtime.publication_state_invalid");
        }
        if (state.Pending is { } pending)
        {
            MihomoRuntimeMaterial.Validate(pending.Candidate);
            if (pending.OperationId == Guid.Empty || pending.Identity != Identity(pending.Candidate.Yaml))
                throw new RuntimePolicyException("runtime.publication_state_invalid");
            if ((pending.ConfigurationPlan is null ? 0 : 1) + (pending.InstallationPlan is null ? 0 : 1)
                + (pending.RemovalPlan is null ? 0 : 1) > 1)
                throw new RuntimePolicyException("runtime.publication_state_invalid");
            if (pending.RemovalPlan is { } removal && (state.Published is null || removal.RuntimeId != "mihomo"
                || removal.Request is null || removal.Request.Change != RuntimeChange.Uninstall || removal.Release is not null
                || removal.Request.ReleaseId is not null || removal.Request.InstallationId == Guid.Empty
                || string.IsNullOrWhiteSpace(removal.PreviousReleaseId)
                || string.IsNullOrWhiteSpace(removal.Request.HostId) || removal.Request.ExpectedStateRevision < 0
                || string.IsNullOrWhiteSpace(removal.Request.IdempotencyKey) || removal.Request.IdempotencyKey.Length > 128
                || removal.Request.IdempotencyKey.Any(c => c < 33 || c > 126)
                || !RuntimeJson.Serialize(state.Published).AsSpan().SequenceEqual(RuntimeJson.Serialize(pending.Candidate))))
                throw new RuntimePolicyException("runtime.publication_state_invalid");
            if (pending.InstallationPlan is { } installation && (installation.RuntimeId != "mihomo"
                || installation.Request.Change != RuntimeChange.Install || installation.Release is null
                || installation.Release.RuntimeId != "mihomo" || installation.Request.InstallationId == Guid.Empty
                || string.IsNullOrWhiteSpace(installation.Request.HostId) || installation.Request.ExpectedStateRevision < 0
                || installation.Request.ExpectedTrustRevision < 1 || installation.Request.ExpectedCatalogRevision < 1
                || installation.Request.ReleaseId != installation.Release.ReleaseId || installation.PreviousReleaseId is not null
                || state.Published is not null && !RuntimeJson.Serialize(state.Published).AsSpan().SequenceEqual(RuntimeJson.Serialize(pending.Candidate))))
                throw new RuntimePolicyException("runtime.publication_state_invalid");
            if (pending.RemovalPlan is not null && pending.Settings is null)
                throw new RuntimePolicyException("runtime.publication_state_invalid");
            if (pending.Settings is { } settings)
            {
                MihomoSettingsPublicationFiles.Validate(settings);
                if (pending.RemovalPlan is not null) MihomoSettingsPublicationFiles.ValidateRemoval(settings);
                else if (pending.ConfigurationPlan is null || state.Published is null
                    || !RuntimeJson.Serialize(MihomoSettingsPublicationFiles.Candidate(state.Published, settings.Candidate))
                        .AsSpan().SequenceEqual(RuntimeJson.Serialize(pending.Candidate)))
                    throw new RuntimePolicyException("runtime.publication_state_invalid");
            }
            if (pending.Profile is { } profile)
            {
                profile.Validate();
                if (pending.ConfigurationPlan is null) throw new RuntimePolicyException("runtime.publication_state_invalid");
            }
            if (pending.ConfigurationPlan is { } plan && (state.Published is null || plan.RuntimeId != "mihomo"
                || plan.InstallationId == Guid.Empty || plan.ActiveRevisionId == Guid.Empty || string.IsNullOrWhiteSpace(plan.HostId)
                || plan.ExpectedStateRevision < 0 || plan.PreviousIdentity != state.Identity || plan.CandidateIdentity != pending.Identity
                || (plan.PreviousIdentity == plan.CandidateIdentity && pending.Settings is null && pending.Profile is null) || string.IsNullOrWhiteSpace(plan.IdempotencyKey)
                || plan.IdempotencyKey.Length > 128 || plan.IdempotencyKey.Any(c => c < 33 || c > 126)))
                throw new RuntimePolicyException("runtime.publication_state_invalid");
        }
    }
}
