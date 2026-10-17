using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

namespace RelaxKonOS.Server.Startup;

internal static class StorageStartup
{
    public static void AddServerStorage(this WebApplicationBuilder builder, StorageOptions storageOpts, string storageProvider)
    {
        if (storageProvider == "sqlite")
        {
            // 数据库文件路径相对 ContentRoot，自动建目录
            var dbPath = Path.Combine(builder.Environment.ContentRootPath, storageOpts.DatabasePath);
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir))
                Directory.CreateDirectory(dbDir);
            // AddDbContextFactory: 注册 IDbContextFactory<RelaxKonOSDbContext>（Singleton）供 Singleton 消费者
            // （如 LocalGitRepositoryService）按操作创建短生命周期 DbContext；同时保留 RelaxKonOSDbContext 为
            // Scoped，使既有 Scoped 仓储（SqliteUserRepository 等）直接注入不变。
            builder.Services.AddDbContextFactory<RelaxKonOSDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
            // 仓储为 Scoped（依赖 Scoped 的 DbContext）；Minimal API [FromServices] 每请求创建 scope，兼容
            builder.Services.AddScoped<IAliasCredentialRepository, SqliteAliasCredentialRepository>();
            builder.Services.AddScoped<IUserRepository, SqliteUserRepository>();
            builder.Services.AddScoped<IAuthenticationProtectionStore, SqliteAuthenticationProtectionStore>();
            builder.Services.AddScoped<IWorkspaceRepository, SqliteWorkspaceRepository>();
            builder.Services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
            builder.Services.AddScoped<IOwnerDeviceKeyRepository, SqliteOwnerDeviceKeyRepository>();
            builder.Services.AddScoped<IBrowserRepository, SqliteBrowserRepository>();
            builder.Services.AddScoped<IAppSettingsRepository, SqliteAppSettingsRepository>();
            // The registry is the runtime configuration source. It is hydrated once at startup and
            // batches durable SQLite writes in the background, so configuration reads never hit SQLite.
            builder.Services.AddSingleton<RelaxKonOS.Server.ConfigurationRegistry.CachedSqliteRegistryRepository>();
            builder.Services.AddSingleton<IRegistryRepository>(sp => sp.GetRequiredService<RelaxKonOS.Server.ConfigurationRegistry.CachedSqliteRegistryRepository>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RelaxKonOS.Server.ConfigurationRegistry.CachedSqliteRegistryRepository>());
            builder.Services.AddScoped<IImageMirrorRepository, SqliteImageMirrorRepository>();
            builder.Services.AddScoped<RelaxKonOS.Server.Secrets.ISecretStore, RelaxKonOS.Server.Secrets.DataProtectionSecretStore>();
            builder.Services.AddScoped<RelaxKonOS.Server.Tunnels.ITunnelService, RelaxKonOS.Server.Tunnels.TunnelService>();
            builder.Services.AddScoped<RelaxKonOS.Server.Tunnels.ITunnelAudit, RelaxKonOS.Server.Tunnels.TunnelAudit>();
            builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyProfileRepository, RelaxKonOS.Server.Proxy.SqliteProxyProfileRepository>();
            builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxyProfileService, RelaxKonOS.Server.Proxy.ProxyProfileService>();
            builder.Services.AddSingleton<RelaxKonOS.Server.Proxy.IProxySubscriptionRepository, RelaxKonOS.Server.Proxy.SqliteProxySubscriptionRepository>();
            builder.Services.AddScoped<RelaxKonOS.Server.Proxy.IProxySubscriptionService, RelaxKonOS.Server.Proxy.ProxySubscriptionService>();
            builder.Services.AddScoped<RelaxKonOS.Server.Proxy.IProxyConfigurationTransactionService, RelaxKonOS.Server.Proxy.ProxyConfigurationTransactionService>();
            builder.Services.AddScoped<RelaxKonOS.Server.Proxy.IProxyConfigurationService, RelaxKonOS.Server.Proxy.ProxyConfigurationService>();
            // The Docker proxy preference opens its own connection per operation, so it is a Singleton
            // like the proxy subscription repository rather than bound to a request scope.
            builder.Services.AddSingleton<IDockerProxySettingsRepository, RelaxKonOS.Server.Storage.Sqlite.SqliteDockerProxySettingsRepository>();
        }
        else
        {
            // memory：开发回退（重启丢失）
            builder.Services.AddSingleton<IAliasCredentialRepository, InMemoryAliasCredentialRepository>();
            builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
            builder.Services.AddSingleton<IAuthenticationProtectionStore, InMemoryAuthenticationProtectionStore>();
            builder.Services.AddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
            builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
            builder.Services.AddSingleton<IOwnerDeviceKeyRepository, InMemoryOwnerDeviceKeyRepository>();
            builder.Services.AddSingleton<IBrowserRepository, InMemoryBrowserRepository>();
            builder.Services.AddSingleton<IAppSettingsRepository, InMemoryAppSettingsRepository>();
            builder.Services.AddSingleton<IRegistryRepository, InMemoryRegistryRepository>();
            builder.Services.AddSingleton<IImageMirrorRepository, InMemoryImageMirrorRepository>();
            builder.Services.AddSingleton<RelaxKonOS.Server.Tunnels.ITunnelAudit, RelaxKonOS.Server.Tunnels.InMemoryTunnelAudit>();
            builder.Services.AddSingleton<IDockerProxySettingsRepository, InMemoryDockerProxySettingsRepository>();
        }
        // Session 始终内存（连接关系，不持久化）
        builder.Services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
        builder.Services.AddScoped<LoginProtectionService>();
    }

    public static FileStream AcquireIdentityHostLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return AuthMaintenanceCommand.AcquireLock(path);
    }

    public static async Task InitializeServerStorageAsync(this WebApplication app, string storageProvider)
    {
        // 启动时建库/建表（SQLite 模式）。EnsureCreated 零工具依赖，适合当前稳定 schema；
        // 未来 schema 需演进时切换为 EF Core Migrations（db.Database.MigrateAsync）。
        // 注意：EnsureCreated 只在库不存在时建表——已存在的 db 不会追加新表（如本次新增的 bookmarks/history_entries）。
        // 为兼容既有部署（保留测试数据），追加 CREATE TABLE IF NOT EXISTS 增量补齐浏览器相关表。
        if (storageProvider == "sqlite")
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
            var identityPreflight = IdentityMigrationRunner.Preflight(db.Database.GetDbConnection(), scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
            if (identityPreflight.Any(entry => entry.Problem is not null))
                throw new InvalidOperationException("Identity preflight failed. Run auth preflight --database <absolute path> and resolve ownership before upgrading.");
            db.Database.EnsureCreated();

            // 增量补齐：仅当表不存在时创建（与 EF Core 模型一致，索引/列类型对齐 OnModelCreating）。
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS "bookmarks" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Title" TEXT,
                    "Url" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_bookmarks_UserId_Url" ON "bookmarks" ("UserId", "Url");
                CREATE INDEX IF NOT EXISTS "IX_bookmarks_UserId" ON "bookmarks" ("UserId");

                CREATE TABLE IF NOT EXISTS "history_entries" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Title" TEXT,
                    "Url" TEXT NOT NULL,
                    "VisitCount" INTEGER NOT NULL,
                    "FirstVisitedAt" TEXT NOT NULL,
                    "LastVisitedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_history_entries_UserId_Url" ON "history_entries" ("UserId", "Url");
                CREATE INDEX IF NOT EXISTS "IX_history_entries_UserId_LastVisitedAt" ON "history_entries" ("UserId", "LastVisitedAt");

                CREATE TABLE IF NOT EXISTS "app_settings" (
                    "UserId" TEXT NOT NULL,
                    "Scope" TEXT NOT NULL,
                    "ScopeId" TEXT NOT NULL,
                    "AppId" TEXT NOT NULL,
                    "Key" TEXT NOT NULL,
                    "ValueJson" TEXT NOT NULL,
                    "SchemaVersion" INTEGER NOT NULL,
                    "Revision" INTEGER NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    PRIMARY KEY ("UserId", "Scope", "ScopeId", "AppId", "Key")
                );
                CREATE INDEX IF NOT EXISTS "IX_app_settings_UserId_UpdatedAt" ON "app_settings" ("UserId", "UpdatedAt");

                CREATE TABLE IF NOT EXISTS "image_mirrors" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Target" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Endpoint" TEXT NOT NULL,
                    "IsSelected" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_image_mirrors_UserId_Target" ON "image_mirrors" ("UserId", "Target");

                CREATE TABLE IF NOT EXISTS "git_repositories" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_git_repositories_UserId" ON "git_repositories" ("UserId");

                CREATE TABLE IF NOT EXISTS "tunnel_server_profiles" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Host" TEXT NOT NULL,
                    "Port" INTEGER NOT NULL,
                    "AuthKind" TEXT NOT NULL,
                    "TlsMode" TEXT NOT NULL,
                    "RuntimeMode" TEXT NOT NULL,
                    "ExternalExecutablePath" TEXT NULL,
                    "Revision" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_tunnel_server_profiles_UserId_Name" ON "tunnel_server_profiles" ("UserId", "Name");
                CREATE TABLE IF NOT EXISTS "tunnel_definitions" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "ServerProfileId" TEXT NOT NULL,
                    "UserId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "ProviderId" TEXT NOT NULL,
                    "Protocol" TEXT NOT NULL,
                    "LocalHost" TEXT NOT NULL,
                    "LocalPort" INTEGER NOT NULL,
                    "RemotePort" INTEGER NULL,
                    "Domain" TEXT NULL,
                    "Enabled" INTEGER NOT NULL,
                    "Encryption" INTEGER NOT NULL,
                    "Compression" INTEGER NOT NULL,
                    "Revision" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_tunnel_definitions_UserId_ServerProfileId_Name" ON "tunnel_definitions" ("UserId", "ServerProfileId", "Name");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_tunnel_definitions_ServerProfileId_RemotePort" ON "tunnel_definitions" ("ServerProfileId", "RemotePort") WHERE "RemotePort" IS NOT NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_tunnel_definitions_ServerProfileId_Domain" ON "tunnel_definitions" ("ServerProfileId", "Domain") WHERE "Domain" IS NOT NULL;
                CREATE TABLE IF NOT EXISTS "tunnel_secrets" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "ServerProfileId" TEXT NOT NULL,
                    "Purpose" TEXT NOT NULL,
                    "ProtectedValue" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_tunnel_secrets_ServerProfileId_Purpose" ON "tunnel_secrets" ("ServerProfileId", "Purpose");
                CREATE TABLE IF NOT EXISTS "tunnel_audit_entries" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "ActorUserId" TEXT NOT NULL,
                    "Action" TEXT NOT NULL,
                    "TargetId" TEXT NULL,
                    "Result" TEXT NOT NULL,
                    "ProblemCode" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_tunnel_audit_entries_CreatedAt" ON "tunnel_audit_entries" ("CreatedAt");

                CREATE TABLE IF NOT EXISTS "account_failure_states" (
                    "AccountKey" TEXT NOT NULL PRIMARY KEY,
                    "FailureCount" INTEGER NOT NULL,
                    "FirstFailureAt" TEXT NOT NULL,
                    "LastFailureAt" TEXT NOT NULL,
                    "BlockedUntil" TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS "authentication_security_events" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "EventType" TEXT NOT NULL,
                    "AccountKey" TEXT NULL,
                    "SourceIp" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_authentication_security_events_CreatedAt" ON "authentication_security_events" ("CreatedAt");
                CREATE INDEX IF NOT EXISTS "IX_authentication_security_events_AccountKey" ON "authentication_security_events" ("AccountKey");

                CREATE TABLE IF NOT EXISTS "registry_entries" (
                    "UserId" TEXT NOT NULL, "Scope" TEXT NOT NULL, "ScopeId" TEXT NOT NULL,
                    "Path" TEXT NOT NULL, "Name" TEXT NOT NULL, "ValueType" TEXT NOT NULL,
                    "ValueJson" TEXT NOT NULL, "Revision" INTEGER NOT NULL, "State" TEXT NOT NULL,
                    "DesiredUpdatedAt" TEXT NOT NULL, "DesiredUpdatedBy" TEXT NOT NULL,
                    "AppliedRevision" INTEGER NULL, "AppliedAt" TEXT NULL,
                    "LastErrorCode" TEXT NULL, "LastErrorMessage" TEXT NULL,
                    PRIMARY KEY ("UserId", "Scope", "ScopeId", "Path", "Name")
                );
                CREATE INDEX IF NOT EXISTS "IX_registry_entries_UserId_Scope_ScopeId_State"
                    ON "registry_entries" ("UserId", "Scope", "ScopeId", "State");
                CREATE TABLE IF NOT EXISTS "registry_keys" (
                    "UserId" TEXT NOT NULL, "Scope" TEXT NOT NULL, "ScopeId" TEXT NOT NULL,
                    "Path" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "CreatedBy" TEXT NOT NULL,
                    PRIMARY KEY ("UserId", "Scope", "ScopeId", "Path")
                );
                CREATE TABLE IF NOT EXISTS "owner_device_keys" (
                    "Id" TEXT NOT NULL PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "DeviceId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Platform" TEXT NOT NULL,
                    "ClientVersion" TEXT NOT NULL,
                    "PublicKeySpki" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "LastUsedAt" TEXT NULL,
                    "RevokedAt" TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_owner_device_keys_UserId_RevokedAt" ON "owner_device_keys" ("UserId", "RevokedAt");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_owner_device_keys_DeviceId" ON "owner_device_keys" ("DeviceId");
                """);

            // Host-global certificate/WebServer state uses independently versioned migrations. This
            // is deliberately not an ad-hoc ALTER/CREATE compatibility patch: operations must remain
            // durable and recoverable independently from user/workspace schema evolution.
            await HostGlobalMigrationRunner.MigrateAsync(db.Database.GetDbConnection().ConnectionString, app.Lifetime.ApplicationStopping);
            IdentityMigrationRunner.Migrate(db, scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
        }
    }
}
