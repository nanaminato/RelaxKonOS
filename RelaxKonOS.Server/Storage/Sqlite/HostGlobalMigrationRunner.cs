using Microsoft.Data.Sqlite;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>Versioned host-global schema migrations. These tables intentionally do not use the
/// user/workspace DbContext: certificates and web servers are machine resources, not tenants.</summary>
internal static class HostGlobalMigrationRunner
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS relaxkonos_host_schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """, cancellationToken);
        if (!await IsAppliedAsync(connection, transaction, 1, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE certificate_operations (
                    operation_id TEXT NOT NULL PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    certificate_id TEXT NULL,
                    kind TEXT NOT NULL,
                    state TEXT NOT NULL,
                    stage TEXT NOT NULL,
                    problem_code TEXT NOT NULL,
                    started_at TEXT NULL,
                    completed_at TEXT NULL
                );
                CREATE INDEX ix_certificate_operations_certificate_id ON certificate_operations(certificate_id);
                CREATE TABLE certificate_records (
                    certificate_id TEXT NOT NULL PRIMARY KEY,
                    primary_domain TEXT NOT NULL UNIQUE,
                    domains_json TEXT NOT NULL,
                    challenge_type TEXT NOT NULL,
                    key_algorithm TEXT NULL,
                    issuer TEXT NULL,
                    serial_number TEXT NULL,
                    thumbprint TEXT NULL,
                    not_before TEXT NULL,
                    not_after TEXT NULL,
                    current_version TEXT NULL,
                    previous_version TEXT NULL,
                    status TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                    ,contact_email TEXT NULL,
                    renewal_window_start TEXT NULL,
                    renewal_window_end TEXT NULL
                );
                CREATE TABLE acme_account_records (
                    account_id TEXT NOT NULL PRIMARY KEY,
                    directory_url TEXT NOT NULL UNIQUE,
                    account_url TEXT NULL,
                    contact_email TEXT NOT NULL,
                    key_reference TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE certificate_deployment_records (
                    deployment_id TEXT NOT NULL PRIMARY KEY,
                    certificate_id TEXT NOT NULL,
                    target_type TEXT NOT NULL,
                    target_name TEXT NOT NULL,
                    current_version TEXT NULL,
                    last_successful_version TEXT NULL,
                    last_health_check_at TEXT NULL,
                    problem_code TEXT NULL,
                    revision INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(certificate_id, target_type, target_name)
                );
                CREATE TABLE certificate_renewal_attempts (
                    attempt_id TEXT NOT NULL PRIMARY KEY,
                    certificate_id TEXT NOT NULL,
                    operation_id TEXT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT NULL,
                    problem_code TEXT NULL,
                    retry_after TEXT NULL
                );
                CREATE INDEX ix_certificate_renewal_attempts_certificate_id ON certificate_renewal_attempts(certificate_id);
                CREATE TABLE certificate_audit_entries (
                    audit_id TEXT NOT NULL PRIMARY KEY,
                    operation_id TEXT NULL,
                    certificate_id TEXT NULL,
                    actor TEXT NULL,
                    action TEXT NOT NULL,
                    result TEXT NOT NULL,
                    problem_code TEXT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE webserver_instances (
                    instance_id TEXT NOT NULL PRIMARY KEY,
                    provider_id TEXT NOT NULL,
                    management_mode TEXT NOT NULL,
                    executable_path TEXT NOT NULL,
                    configuration_path TEXT NULL,
                    detected_at TEXT NOT NULL,
                    revision INTEGER NOT NULL
                );
                CREATE TABLE webserver_sites (
                    site_id TEXT NOT NULL PRIMARY KEY,
                    instance_id TEXT NOT NULL,
                    normalized_domain TEXT NOT NULL,
                    ownership TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(instance_id, normalized_domain)
                );
                CREATE TABLE webserver_config_snapshots (
                    snapshot_id TEXT NOT NULL PRIMARY KEY,
                    instance_id TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    external_modified INTEGER NOT NULL,
                    recoverable INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE webserver_operations (
                    operation_id TEXT NOT NULL PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    instance_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    state TEXT NOT NULL,
                    stage TEXT NOT NULL,
                    problem_code TEXT NOT NULL,
                    snapshot_id TEXT NULL,
                    started_at TEXT NULL,
                    completed_at TEXT NULL
                );
                CREATE INDEX ix_webserver_operations_instance_id ON webserver_operations(instance_id);
                INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (1, CURRENT_TIMESTAMP);
                """, cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 2, cancellationToken))
        {
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "contact_email", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN contact_email TEXT NULL;", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (2, CURRENT_TIMESTAMP);", cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 3, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE webserver_audit_entries (
                    audit_id TEXT NOT NULL PRIMARY KEY,
                    operation_id TEXT NULL,
                    instance_id TEXT NULL,
                    actor TEXT NULL,
                    action TEXT NOT NULL,
                    result TEXT NOT NULL,
                    problem_code TEXT NULL,
                    created_at TEXT NOT NULL
                );
                INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (3, CURRENT_TIMESTAMP);
                """, cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 4, cancellationToken))
        {
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "renewal_window_start", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN renewal_window_start TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "renewal_window_end", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN renewal_window_end TEXT NULL;", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (4, CURRENT_TIMESTAMP);", cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 5, cancellationToken))
        {
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "key_algorithm", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN key_algorithm TEXT NULL;", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (5, CURRENT_TIMESTAMP);", cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 6, cancellationToken))
        {
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "last_renewal_at", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN last_renewal_at TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "last_renewal_problem_code", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN last_renewal_problem_code TEXT NULL;", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (6, CURRENT_TIMESTAMP);", cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 7, cancellationToken))
        {
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "kind", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN kind TEXT NULL;", cancellationToken);
            if (!await HasColumnAsync(connection, transaction, "certificate_records", "fingerprint_sha256", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE certificate_records ADD COLUMN fingerprint_sha256 TEXT NULL;", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (7, CURRENT_TIMESTAMP);", cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 8, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE proxy_profiles (
                    profile_id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    engine_id TEXT NOT NULL,
                    is_active INTEGER NOT NULL,
                    revision INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX ix_proxy_profiles_active ON proxy_profiles(is_active) WHERE is_active = 1;
                CREATE TABLE proxy_configuration_audits (
                    audit_id TEXT NOT NULL PRIMARY KEY,
                    profile_id TEXT NULL,
                    action TEXT NOT NULL,
                    result TEXT NOT NULL,
                    problem_code TEXT NULL,
                    created_at TEXT NOT NULL
                );
                INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (8, CURRENT_TIMESTAMP);
                """, cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 9, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE proxy_subscriptions (
                    subscription_id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE,
                    profile_id TEXT NOT NULL UNIQUE,
                    protected_url TEXT NOT NULL,
                    last_updated_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX ix_proxy_subscriptions_profile_id ON proxy_subscriptions(profile_id);
                INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (9, CURRENT_TIMESTAMP);
                """, cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 10, cancellationToken))
        {
            if (!await HasColumnAsync(connection, transaction, "proxy_subscriptions", "download_route", cancellationToken))
                await ExecuteAsync(connection, transaction, "ALTER TABLE proxy_subscriptions ADD COLUMN download_route INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (10, CURRENT_TIMESTAMP);", cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 11, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE smb_windows_ownership_ledger (
                    share_id TEXT NOT NULL PRIMARY KEY,
                    share_name TEXT NOT NULL,
                    path_hash TEXT NOT NULL,
                    snapshot_hash TEXT NOT NULL,
                    reconciliation_required INTEGER NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (11, CURRENT_TIMESTAMP);
                """, cancellationToken);
        }
        if (!await IsAppliedAsync(connection, transaction, 12, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE smb_audit_entries (
                    audit_id TEXT NOT NULL PRIMARY KEY,
                    operation_id TEXT NOT NULL,
                    actor TEXT NULL,
                    token_reference TEXT NULL,
                    action TEXT NOT NULL,
                    resource_hash TEXT NOT NULL,
                    succeeded INTEGER NOT NULL,
                    problem_code TEXT NULL,
                    helper_protocol_version TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX ix_smb_audit_entries_created_at ON smb_audit_entries(created_at);
                INSERT INTO relaxkonos_host_schema_migrations(version, applied_at) VALUES (12, CURRENT_TIMESTAMP);
                """, cancellationToken);
        }
        transaction.Commit();
    }

    private static async Task<bool> IsAppliedAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM relaxkonos_host_schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string column, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column);";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }
}
