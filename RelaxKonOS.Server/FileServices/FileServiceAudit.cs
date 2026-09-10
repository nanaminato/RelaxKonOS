using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.FileServices;

public interface IFileServiceAudit
{
    Task WriteAsync(ClaimsPrincipal actor, string action, string resource, bool succeeded, string? problemCode, Guid operationId, CancellationToken ct);
}

/// <summary>Host-global, non-secret SMB management audit. The ledger is evidence, never desired state.</summary>
public sealed class FileServiceAudit(IConfiguration configuration, IHostEnvironment environment) : IFileServiceAudit
{
    private readonly StorageOptions _options = configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
    private readonly ConcurrentQueue<AuditFallback> _fallback = new();
    private string ConnectionString => $"Data Source={Path.Combine(environment.ContentRootPath, _options.DatabasePath)}";
    public async Task WriteAsync(ClaimsPrincipal actor, string action, string resource, bool succeeded, string? problemCode, Guid operationId, CancellationToken ct)
    {
        // Hash exact resource and token id before persistence. Never persist a path, SID, display name, or password.
        var resourceHash = Hash(resource); var tokenReference = actor.FindFirstValue(JwtRegisteredClaimNames.Jti) is { Length: > 0 } jti ? Hash(jti) : null;
        var actorName = actor.FindFirstValue(JwtRegisteredClaimNames.Name) ?? actor.Identity?.Name;
        if (!string.Equals(_options.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            _fallback.Enqueue(new(operationId, action, resourceHash, succeeded, problemCode, DateTimeOffset.UtcNow));
            while (_fallback.Count > 10_000) _fallback.TryDequeue(out _);
            return;
        }
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct); await using var command = connection.CreateCommand();
        await using (var prune = connection.CreateCommand()) { prune.CommandText = "DELETE FROM smb_audit_entries WHERE created_at < $cutoff;"; prune.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToString("O")); await prune.ExecuteNonQueryAsync(ct); }
        command.CommandText = "INSERT INTO smb_audit_entries(audit_id,operation_id,actor,token_reference,action,resource_hash,succeeded,problem_code,helper_protocol_version,created_at) VALUES($audit,$operation,$actor,$token,$action,$resource,$succeeded,$problem,$version,$time);";
        command.Parameters.AddWithValue("$audit", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$operation", operationId.ToString("N")); command.Parameters.AddWithValue("$actor", (object?)actorName ?? DBNull.Value); command.Parameters.AddWithValue("$token", (object?)tokenReference ?? DBNull.Value); command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$resource", resourceHash); command.Parameters.AddWithValue("$succeeded", succeeded); command.Parameters.AddWithValue("$problem", (object?)problemCode ?? DBNull.Value); command.Parameters.AddWithValue("$version", PrivilegedOperationProtocol.Version); command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    private sealed record AuditFallback(Guid OperationId, string Action, string ResourceHash, bool Succeeded, string? ProblemCode, DateTimeOffset CreatedAt);
}
