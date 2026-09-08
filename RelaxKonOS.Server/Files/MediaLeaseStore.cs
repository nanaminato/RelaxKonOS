using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Files;

/// <summary>
/// Keeps opaque, short-lived URLs bound to one server file. The lease id is a bearer capability,
/// so only the host-authenticated management endpoints may create, renew, or revoke it.
/// </summary>
public sealed class MediaLeaseStore
{
    private readonly ConcurrentDictionary<string, MediaLease> _leases = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _maximumLifetime;

    public MediaLeaseStore(IOptions<JwtOptions> options)
    {
        _ttl = options.Value.MediaLeaseTtl;
        _maximumLifetime = options.Value.MediaLeaseMaximumLifetime;
    }

    public MediaLease Create(Guid userId, Guid workspaceId, Guid deviceId, string appId, string path)
    {
        RemoveExpired();
        var now = DateTimeOffset.UtcNow;
        var lease = new MediaLease(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            userId,
            workspaceId,
            deviceId,
            appId,
            path,
            now,
            now.Add(_ttl),
            now.Add(_maximumLifetime));
        _leases[lease.Id] = lease;
        return lease;
    }

    public bool TryGetActive(string leaseId, out MediaLease lease)
    {
        if (_leases.TryGetValue(leaseId, out lease!) && lease.ExpiresAt > DateTimeOffset.UtcNow)
            return true;

        _leases.TryRemove(leaseId, out _);
        lease = default!;
        return false;
    }

    public bool TryRenew(string leaseId, Guid userId, Guid workspaceId, Guid deviceId, out MediaLease lease)
    {
        if (!TryGetActive(leaseId, out var existing)
            || existing.UserId != userId
            || existing.WorkspaceId != workspaceId
            || existing.DeviceId != deviceId)
        {
            lease = default!;
            return false;
        }

        var renewedUntil = DateTimeOffset.UtcNow.Add(_ttl);
        lease = existing with { ExpiresAt = renewedUntil < existing.MaximumExpiresAt ? renewedUntil : existing.MaximumExpiresAt };
        if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _leases.TryRemove(leaseId, out _);
            lease = default!;
            return false;
        }

        _leases[leaseId] = lease;
        return true;
    }

    public void Revoke(string leaseId, Guid userId, Guid workspaceId, Guid deviceId)
    {
        if (_leases.TryGetValue(leaseId, out var lease)
            && lease.UserId == userId
            && lease.WorkspaceId == workspaceId
            && lease.DeviceId == deviceId)
            _leases.TryRemove(leaseId, out _);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _leases.Where(pair => pair.Value.ExpiresAt <= now))
            _leases.TryRemove(pair.Key, out _);
    }
}

public sealed record MediaLease(
    string Id,
    Guid UserId,
    Guid WorkspaceId,
    Guid DeviceId,
    string AppId,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset MaximumExpiresAt);
