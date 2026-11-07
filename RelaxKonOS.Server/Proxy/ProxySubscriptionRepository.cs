using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Host-global subscription metadata. Subscription URLs are encrypted before they reach SQLite.</summary>
public interface IProxySubscriptionRepository
{
    Task<IReadOnlyList<ProxySubscriptionDto>> ListAsync(CancellationToken cancellationToken);
    Task<ProxySubscriptionRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ProxySubscriptionDto> CreateAsync(string name, Guid profileId, string url, ProxySubscriptionDownloadRoute downloadRoute, CancellationToken cancellationToken);
    Task<bool> PrepareDraftRefreshAsync(ProxyDraftRefreshIntent intent, CancellationToken ct);
    Task<bool> CommitPreparedDraftRefreshAsync(ProxyDraftRefreshIntent intent, CancellationToken ct);
    Task<bool> BeginDraftAbandonmentAsync(ProxyDraftRefreshIntent intent, CancellationToken ct);
    Task<bool> CompleteDraftAbandonmentAsync(ProxyDraftRefreshIntent intent, DateTimeOffset completedAt, CancellationToken ct);
    Task<ProxyDraftRefreshIntent?> ReadDraftRefreshIntentAsync(Guid operationId, CancellationToken ct);
    Task<ProxyDraftRefreshReceipt?> ReadDraftRefreshReceiptAsync(Guid operationId, Guid? subscriptionId, CancellationToken ct);
    Task<bool> HasPendingDraftForProfileAsync(Guid profileId, CancellationToken ct);
}

public sealed record ProxySubscriptionRecord(ProxySubscriptionDto Subscription, string Url, ProxySubscriptionDownloadRoute DownloadRoute);
public sealed record ProxyDraftRefreshIntent(Guid SubscriptionId, Guid ProfileId, Guid OperationId,
    string Identity, DateTimeOffset RetrievedAt, string? PreviousIdentity = null, bool Abandoning = false);
public sealed record ProxyDraftRefreshReceipt(bool Applied, DateTimeOffset At);

public sealed class SqliteProxySubscriptionRepository(
    IHostEnvironment environment,
    IOptions<StorageOptions> storage,
    IDataProtectionProvider dataProtection) : IProxySubscriptionRepository
{
    private readonly string _connectionString = $"Data Source={Path.Combine(environment.ContentRootPath, storage.Value.DatabasePath)}";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("RelaxKonOS.Proxy.SubscriptionSecretStore.v1");

    public async Task<IReadOnlyList<ProxySubscriptionDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.subscription_id,s.name,s.profile_id,p.is_active,s.last_updated_at,s.created_at,s.updated_at FROM proxy_subscriptions s JOIN proxy_profiles p ON p.profile_id=s.profile_id ORDER BY s.name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var subscriptions = new List<ProxySubscriptionDto>();
        while (await reader.ReadAsync(cancellationToken)) subscriptions.Add(ReadDto(reader));
        return subscriptions;
    }

    public async Task<ProxySubscriptionRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.subscription_id,s.name,s.profile_id,p.is_active,s.last_updated_at,s.created_at,s.updated_at,s.protected_url,s.download_route FROM proxy_subscriptions s JOIN proxy_profiles p ON p.profile_id=s.profile_id WHERE s.subscription_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        try
        {
            var route = reader.GetInt64(8) is (long)ProxySubscriptionDownloadRoute.Direct or (long)ProxySubscriptionDownloadRoute.SystemProxy
                ? (ProxySubscriptionDownloadRoute)reader.GetInt64(8)
                : throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
            return new ProxySubscriptionRecord(ReadDto(reader), _protector.Unprotect(reader.GetString(7)), route);
        }
        catch (CryptographicException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid); }
    }

    public async Task<ProxySubscriptionDto> CreateAsync(string name, Guid profileId, string url, ProxySubscriptionDownloadRoute downloadRoute, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || !Enum.IsDefined(downloadRoute))
            throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO proxy_subscriptions(subscription_id,name,profile_id,protected_url,download_route,last_updated_at,created_at,updated_at) VALUES($id,$name,$profile,$url,$route,$last,$created,$updated);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.AddWithValue("$url", _protector.Protect(url));
        command.Parameters.AddWithValue("$route", (int)downloadRoute);
        command.Parameters.AddWithValue("$last", now.ToString("O"));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException) { throw new ProxySubscriptionException(ProxyProblemCodes.SubscriptionInvalid); }
        return new ProxySubscriptionDto(id, name.Trim(), profileId, false, now, now, now);
    }

    public Task<bool> PrepareDraftRefreshAsync(ProxyDraftRefreshIntent intent, CancellationToken ct) =>
        WriteDraftRefreshAsync(intent, """
            UPDATE proxy_subscriptions
            SET pending_draft_operation_id=$operation,pending_draft_identity=$identity,pending_draft_retrieved_at=$retrieved,
                pending_draft_previous_identity=$previous,pending_draft_abandoning=0
            WHERE subscription_id=$id AND profile_id=$profile AND pending_draft_operation_id IS NULL
              AND EXISTS (SELECT 1 FROM proxy_profiles WHERE profile_id=$profile AND is_active=0);
            """, ct);

    public Task<bool> CommitPreparedDraftRefreshAsync(ProxyDraftRefreshIntent intent, CancellationToken ct) =>
        WriteDraftRefreshAsync(intent, """
            UPDATE proxy_subscriptions
            SET last_updated_at=$retrieved,updated_at=$retrieved,last_draft_refresh_operation_id=$operation,last_draft_not_applied_at=NULL,
                pending_draft_operation_id=NULL,pending_draft_identity=NULL,pending_draft_retrieved_at=NULL,pending_draft_previous_identity=NULL
            WHERE subscription_id=$id AND profile_id=$profile AND pending_draft_operation_id=$operation
              AND pending_draft_identity=$identity AND pending_draft_retrieved_at=$retrieved
              AND pending_draft_previous_identity IS $previous AND pending_draft_abandoning=0
              AND EXISTS (SELECT 1 FROM proxy_profiles WHERE profile_id=$profile AND is_active=0);
            """, ct);

    public Task<bool> BeginDraftAbandonmentAsync(ProxyDraftRefreshIntent intent, CancellationToken ct) =>
        WriteDraftRefreshAsync(intent, """
            UPDATE proxy_subscriptions SET pending_draft_abandoning=1
            WHERE subscription_id=$id AND profile_id=$profile AND pending_draft_operation_id=$operation
              AND pending_draft_identity=$identity AND pending_draft_retrieved_at=$retrieved
              AND pending_draft_previous_identity=$previous AND pending_draft_abandoning=0
              AND EXISTS (SELECT 1 FROM proxy_profiles WHERE profile_id=$profile AND is_active=0);
            """, ct);

    public Task<bool> CompleteDraftAbandonmentAsync(ProxyDraftRefreshIntent intent, DateTimeOffset completedAt, CancellationToken ct) =>
        WriteDraftRefreshAsync(intent, """
            UPDATE proxy_subscriptions
            SET last_draft_refresh_operation_id=$operation,last_draft_not_applied_at=$abandoned,
                pending_draft_operation_id=NULL,pending_draft_identity=NULL,pending_draft_retrieved_at=NULL,
                pending_draft_previous_identity=NULL,pending_draft_abandoning=0
            WHERE subscription_id=$id AND profile_id=$profile AND pending_draft_operation_id=$operation
              AND pending_draft_identity=$identity AND pending_draft_retrieved_at=$retrieved
              AND pending_draft_previous_identity=$previous AND pending_draft_abandoning=1
              AND EXISTS (SELECT 1 FROM proxy_profiles WHERE profile_id=$profile AND is_active=0);
            """, ct, completedAt);

    private async Task<bool> WriteDraftRefreshAsync(ProxyDraftRefreshIntent intent, string sql, CancellationToken ct,
        DateTimeOffset? abandoned = null)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", intent.SubscriptionId.ToString("D"));
        command.Parameters.AddWithValue("$profile", intent.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$identity", intent.Identity);
        command.Parameters.AddWithValue("$retrieved", intent.RetrievedAt.ToString("O"));
        command.Parameters.AddWithValue("$previous", (object?)intent.PreviousIdentity ?? DBNull.Value);
        if (abandoned is { } completedAt) command.Parameters.AddWithValue("$abandoned", completedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<ProxyDraftRefreshIntent?> ReadDraftRefreshIntentAsync(Guid operationId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT subscription_id,profile_id,pending_draft_identity,pending_draft_retrieved_at,
                pending_draft_previous_identity,pending_draft_abandoning
            FROM proxy_subscriptions WHERE pending_draft_operation_id=$operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        if (!Guid.TryParse(row.GetString(0), out var subscription) || subscription == Guid.Empty
            || !Guid.TryParse(row.GetString(1), out var profile) || profile == Guid.Empty
            || row.IsDBNull(2) || row.IsDBNull(3)
            || !DateTimeOffset.TryParseExact(row.GetString(3), "O", null, System.Globalization.DateTimeStyles.None, out var retrieved))
            throw new InvalidDataException("Invalid draft refresh checkpoint.");
        var identity = row.GetString(2);
        var previous = row.IsDBNull(4) ? null : row.GetString(4);
        var abandoning = row.GetInt64(5);
        if (!IsDigest(identity) || previous is not null && !IsDigest(previous) || abandoning is not (0 or 1)
            || abandoning == 1 && previous is null)
            throw new InvalidDataException("Invalid draft abandonment checkpoint.");
        var intent = new ProxyDraftRefreshIntent(subscription, profile, operationId, identity, retrieved, previous, abandoning == 1);
        if (await row.ReadAsync(ct)) throw new InvalidDataException("Duplicate draft refresh checkpoint.");
        return intent;
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    public async Task<ProxyDraftRefreshReceipt?> ReadDraftRefreshReceiptAsync(Guid operationId, Guid? subscriptionId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(last_draft_not_applied_at,last_updated_at),last_draft_not_applied_at IS NULL FROM proxy_subscriptions
            WHERE last_draft_refresh_operation_id=$operation AND ($id IS NULL OR subscription_id=$id);
            """;
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$id", (object?)subscriptionId?.ToString("D") ?? DBNull.Value);
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        if (row.IsDBNull(0) || !DateTimeOffset.TryParseExact(row.GetString(0), "O", null,
            System.Globalization.DateTimeStyles.None, out var retrieved))
            throw new InvalidDataException("Invalid draft refresh receipt.");
        var receipt = new ProxyDraftRefreshReceipt(row.GetInt64(1) != 0, retrieved);
        if (await row.ReadAsync(ct)) throw new InvalidDataException("Duplicate draft refresh receipt.");
        return receipt;
    }

    public async Task<bool> HasPendingDraftForProfileAsync(Guid profileId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM proxy_subscriptions WHERE profile_id=$profile AND pending_draft_operation_id IS NOT NULL;";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static ProxySubscriptionDto ReadDto(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)), reader.GetInt64(3) != 0,
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind));
}

public sealed class ProxySubscriptionException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
