using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoPendingPublication(Guid OperationId, MihomoPublishedRuntime Candidate, string Identity);

/// <summary>Server-private encrypted state, never returned by ordinary management reads.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoPublicationState(int SchemaVersion, long Revision, MihomoPublishedRuntime? Published,
    string? Identity, Guid? LastOperationId, MihomoPendingPublication? Pending)
{
    public static MihomoPublicationState Empty => new(1, 0, null, null, null, null);
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

    public async Task<MihomoPublicationState> ReadAsync(CancellationToken ct)
    {
        RuntimeSourceStore.RequireNoLinks(StatePath);
        if (!File.Exists(StatePath)) return MihomoPublicationState.Empty;
        await using var input = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    public async Task<MihomoPublicationState> BeginAsync(Guid operationId, long expectedRevision,
        MihomoPublishedRuntime candidate, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new RuntimePolicyException("runtime.operation_id_invalid");
        MihomoRuntimeMaterial.Validate(candidate);
        var current = await ReadAsync(ct);
        var identity = Identity(candidate.Yaml);
        if (current.Pending is { } pending)
        {
            if (pending.OperationId == operationId && RuntimeJson.Serialize(pending.Candidate).AsSpan().SequenceEqual(RuntimeJson.Serialize(candidate)))
                return current;
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        }
        if (current.Revision != expectedRevision || current.LastOperationId == operationId)
            throw new RuntimePolicyException("runtime.state_revision_conflict");
        var next = current with { Revision = checked(current.Revision + 1), Pending = new(operationId, candidate, identity) };
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
        var next = current with { Revision = checked(current.Revision + 1), Published = pending.Candidate,
            Identity = pending.Identity, LastOperationId = operationId, Pending = null };
        await SaveAsync(next, ct);
        return next;
    }

    /// <summary>Rollback can close a publication only after the previous identity has been re-observed.</summary>
    public async Task<MihomoPublicationState> AbortAsync(Guid operationId, string? restoredIdentity, CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (current.Pending?.OperationId != operationId || restoredIdentity != current.Identity)
            throw new RuntimePolicyException("runtime.publication_unconfirmed");
        var next = current with { Revision = checked(current.Revision + 1), Pending = null };
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

    private static void Validate(MihomoPublicationState state)
    {
        if (state.SchemaVersion != 1 || state.Revision < 0 || (state.Published is null) != (state.Identity is null)
            || (state.Published is null) != (state.LastOperationId is null) || state.LastOperationId == Guid.Empty)
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
        }
    }
}
