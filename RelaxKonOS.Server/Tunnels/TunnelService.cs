using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Tunnels;

/// <summary>Transactional desired-state service. It never starts a process while saving user input.</summary>
public sealed class TunnelService(RemoteOsDbContext db, ISecretStore secrets, ITunnelAudit audit) : ITunnelService
{
    public async Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(string userId, CancellationToken ct) =>
        (await db.TunnelServerProfiles.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Name).ToListAsync(ct))
            .Select(x => ToDto(x, db.TunnelSecrets.AsNoTracking().Any(s => s.ServerProfileId == x.Id && s.Purpose == "token"))).ToArray();

    public async Task<TunnelServerProfileDto?> GetProfileAsync(Guid id, string userId, CancellationToken ct)
    {
        var profile = await db.TunnelServerProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (profile is null) return null;

        return ToDto(profile, profile.AuthKind == TunnelAuthKind.Token && await secrets.HasProfileTokenAsync(profile.Id, ct));
    }

    public async Task<TunnelServerProfileDto> UpsertProfileAsync(Guid? id, UpsertTunnelServerProfileRequest request, string userId, CancellationToken ct)
    {
        var invalid = TunnelValidation.ValidateProfile(request.Name, request.Host, request.Port, request.AuthKind, request.RuntimeMode, request.ExternalExecutablePath);
        if (invalid is not null) throw new TunnelValidationException(invalid);
        var now = DateTimeOffset.UtcNow;
        TunnelServerProfile entity;
        if (id is null)
        {
            if (await db.TunnelServerProfiles.CountAsync(x => x.UserId == userId, ct) >= 32) throw new TunnelValidationException("tunnel.profile_limit_exceeded");
            entity = new TunnelServerProfile { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now, Revision = 1 };
            db.TunnelServerProfiles.Add(entity);
        }
        else
        {
            entity = await db.TunnelServerProfiles.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct) ?? throw new TunnelNotFoundException();
            EnsureRevision(request.ExpectedRevision, entity.Revision);
            entity.Revision++;
        }
        entity.Name = request.Name.Trim(); entity.Host = request.Host.TrimEnd('.'); entity.Port = request.Port;
        entity.AuthKind = request.AuthKind; entity.TlsMode = request.TlsMode; entity.RuntimeMode = request.RuntimeMode;
        entity.ExternalExecutablePath = request.RuntimeMode == TunnelRuntimeMode.External ? Path.GetFullPath(request.ExternalExecutablePath!) : null;
        entity.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new TunnelValidationException("tunnel.profile_conflict"); }
        if (entity.AuthKind != TunnelAuthKind.Token)
            await secrets.DeleteProfileSecretsAsync(entity.Id, ct);
        await audit.RecordAsync(userId, id is null ? "profile.create" : "profile.update", entity.Id, "succeeded", null, ct);
        return ToDto(entity, await secrets.HasProfileTokenAsync(entity.Id, ct));
    }

    public async Task<bool> DeleteProfileAsync(Guid id, string userId, CancellationToken ct)
    {
        var entity = await db.TunnelServerProfiles.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (entity is null) return false;
        if (await db.TunnelDefinitions.AnyAsync(x => x.ServerProfileId == id && x.UserId == userId, ct)) throw new TunnelValidationException("tunnel.profile_in_use");
        db.TunnelServerProfiles.Remove(entity);
        await db.SaveChangesAsync(ct);
        await secrets.DeleteProfileSecretsAsync(id, ct);
        await audit.RecordAsync(userId, "profile.delete", id, "succeeded", null, ct);
        return true;
    }

    public async Task SetProfileTokenAsync(Guid id, string token, string userId, CancellationToken ct)
    {
        var profile = await db.TunnelServerProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct) ?? throw new TunnelNotFoundException();
        if (profile.AuthKind != TunnelAuthKind.Token) throw new TunnelValidationException("tunnel.token_not_applicable");
        await secrets.SetProfileTokenAsync(id, token, ct);
        await audit.RecordAsync(userId, "profile.token.write", id, "succeeded", null, ct);
    }

    public async Task<IReadOnlyList<TunnelDefinitionDto>> ListTunnelsAsync(string userId, CancellationToken ct) =>
        (await db.TunnelDefinitions.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.Name).ToListAsync(ct)).Select(ToDto).ToArray();

    public async Task<TunnelDefinitionDto?> GetTunnelAsync(Guid id, string userId, CancellationToken ct) =>
        (await db.TunnelDefinitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)) is { } entity ? ToDto(entity) : null;

    public async Task<TunnelDefinitionDto> UpsertTunnelAsync(Guid? id, UpsertTunnelDefinitionRequest request, string userId, CancellationToken ct)
    {
        var invalid = TunnelValidation.ValidateDefinition(request.Name, request.Protocol, request.LocalHost, request.LocalPort, request.RemotePort, request.Domain);
        if (invalid is not null) throw new TunnelValidationException(invalid);
        if (!await db.TunnelServerProfiles.AnyAsync(x => x.Id == request.ServerProfileId && x.UserId == userId, ct)) throw new TunnelValidationException("tunnel.profile_not_found");
        var now = DateTimeOffset.UtcNow;
        TunnelDefinition entity;
        if (id is null)
        {
            if (await db.TunnelDefinitions.CountAsync(x => x.ServerProfileId == request.ServerProfileId && x.UserId == userId, ct) >= 128) throw new TunnelValidationException("tunnel.definition_limit_exceeded");
            entity = new TunnelDefinition { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now, Revision = 1 };
            db.TunnelDefinitions.Add(entity);
        }
        else
        {
            entity = await db.TunnelDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct) ?? throw new TunnelNotFoundException();
            EnsureRevision(request.ExpectedRevision, entity.Revision);
            entity.Revision++;
        }
        entity.ServerProfileId = request.ServerProfileId; entity.Name = request.Name.Trim(); entity.Protocol = request.Protocol;
        entity.LocalHost = request.LocalHost.TrimEnd('.'); entity.LocalPort = request.LocalPort; entity.RemotePort = request.RemotePort;
        entity.Domain = string.IsNullOrWhiteSpace(request.Domain) ? null : request.Domain.TrimEnd('.').ToLowerInvariant();
        entity.Enabled = request.Enabled; entity.Encryption = request.Encryption; entity.Compression = request.Compression; entity.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new TunnelValidationException("tunnel.endpoint_conflict"); }
        await audit.RecordAsync(userId, id is null ? "tunnel.create" : "tunnel.update", entity.Id, "succeeded", null, ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteTunnelAsync(Guid id, string userId, CancellationToken ct)
    {
        var entity = await db.TunnelDefinitions.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (entity is null) return false;
        db.TunnelDefinitions.Remove(entity); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(userId, "tunnel.delete", id, "succeeded", null, ct);
        return true;
    }

    private static void EnsureRevision(long? expected, long actual)
    {
        if (expected is null || expected != actual) throw new TunnelRevisionConflictException();
    }
    private static TunnelServerProfileDto ToDto(TunnelServerProfile x, bool tokenConfigured) => new(x.Id, x.Name, x.Host, x.Port, x.AuthKind, tokenConfigured, x.TlsMode, x.RuntimeMode, x.ExternalExecutablePath, x.Revision, x.CreatedAt, x.UpdatedAt);
    private static TunnelDefinitionDto ToDto(TunnelDefinition x) => new(x.Id, x.ServerProfileId, x.Name, x.ProviderId, x.Protocol, x.LocalHost, x.LocalPort, x.RemotePort, x.Domain, x.Enabled, x.Encryption, x.Compression, x.Revision, x.CreatedAt, x.UpdatedAt);
}

public sealed class TunnelValidationException(string problemCode) : Exception(problemCode) { public string ProblemCode { get; } = problemCode; }
public sealed class TunnelRevisionConflictException() : Exception("tunnel.revision_conflict");
public sealed class TunnelNotFoundException() : Exception("tunnel.not_found");
