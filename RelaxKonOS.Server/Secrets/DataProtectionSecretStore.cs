using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Secrets;

/// <summary>Persists only data-protection ciphertext. It deliberately has no list or export API.</summary>
public sealed class DataProtectionSecretStore(RelaxKonOSDbContext db, IDataProtectionProvider protectionProvider) : ISecretStore
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("RelaxKonOS.Tunnels.SecretStore.v1");

    public async Task SetProfileTokenAsync(Guid profileId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) throw new SecretValidationException("tunnel.token_invalid");
        var existing = await db.TunnelSecrets.SingleOrDefaultAsync(x => x.ServerProfileId == profileId && x.Purpose == "token", ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
            db.TunnelSecrets.Add(new TunnelSecret { Id = Guid.NewGuid(), ServerProfileId = profileId, ProtectedValue = _protector.Protect(token), CreatedAt = now, UpdatedAt = now });
        else
        {
            existing.ProtectedValue = _protector.Protect(token);
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetProfileTokenAsync(Guid profileId, CancellationToken ct)
    {
        var record = await db.TunnelSecrets.AsNoTracking().SingleOrDefaultAsync(x => x.ServerProfileId == profileId && x.Purpose == "token", ct);
        if (record is null) return null;
        try { return _protector.Unprotect(record.ProtectedValue); }
        catch { throw new SecretValidationException("tunnel.secret_unavailable"); }
    }

    public Task<bool> HasProfileTokenAsync(Guid profileId, CancellationToken ct) =>
        db.TunnelSecrets.AsNoTracking().AnyAsync(x => x.ServerProfileId == profileId && x.Purpose == "token", ct);

    public async Task DeleteProfileSecretsAsync(Guid profileId, CancellationToken ct)
    {
        var records = await db.TunnelSecrets.Where(x => x.ServerProfileId == profileId).ToListAsync(ct);
        db.TunnelSecrets.RemoveRange(records);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SecretValidationException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
