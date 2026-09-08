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
    Task SetLastUpdatedAsync(Guid id, DateTimeOffset updatedAt, CancellationToken cancellationToken);
}

public sealed record ProxySubscriptionRecord(ProxySubscriptionDto Subscription, string Url, ProxySubscriptionDownloadRoute DownloadRoute);

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

    public async Task SetLastUpdatedAsync(Guid id, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE proxy_subscriptions SET last_updated_at=$updated,updated_at=$updated WHERE subscription_id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
