using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Server.Domain;

namespace RelaxKonOS.Server.Storage.Sqlite;

/// <summary>EF Core DbContext。持久化 User / Workspace（身份与归属）/ Device / Bookmark / HistoryEntry 等实体。
/// Session / refresh token / PTY 进程不在本上下文（维持内存，见 docs/RelaxKonOS.Storage.md）。</summary>
public sealed class RelaxKonOSDbContext : DbContext
{
    public RelaxKonOSDbContext(DbContextOptions<RelaxKonOSDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ImageMirror> ImageMirrors => Set<ImageMirror>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<TunnelServerProfile> TunnelServerProfiles => Set<TunnelServerProfile>();
    public DbSet<TunnelDefinition> TunnelDefinitions => Set<TunnelDefinition>();
    public DbSet<TunnelSecret> TunnelSecrets => Set<TunnelSecret>();
    public DbSet<TunnelAuditEntry> TunnelAuditEntries => Set<TunnelAuditEntry>();
    public DbSet<AccountFailureState> AccountFailureStates => Set<AccountFailureState>();
    public DbSet<AuthenticationSecurityEvent> AuthenticationSecurityEvents => Set<AuthenticationSecurityEvent>();
    public DbSet<RegistryEntry> RegistryEntries => Set<RegistryEntry>();
    public DbSet<RegistryKey> RegistryKeys => Set<RegistryKey>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<RegistryEntry>(e =>
        {
            e.ToTable("registry_entries");
            e.HasKey(x => new { x.UserId, x.Scope, x.ScopeId, x.Path, x.Name });
            e.Property(x => x.UserId).HasColumnType("TEXT");
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.ScopeId).HasColumnType("TEXT");
            e.Property(x => x.Path).HasMaxLength(256).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.ValueType).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.ValueJson).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.DesiredUpdatedAt).HasColumnType("TEXT");
            e.Property(x => x.DesiredUpdatedBy).HasMaxLength(256).IsRequired();
            e.Property(x => x.AppliedAt).HasColumnType("TEXT");
            e.Property(x => x.LastErrorCode).HasMaxLength(128);
            e.Property(x => x.LastErrorMessage).HasMaxLength(512);
            e.HasIndex(x => new { x.UserId, x.Scope, x.ScopeId, x.State });
        });
        mb.Entity<RegistryKey>(e =>
        {
            e.ToTable("registry_keys");
            e.HasKey(x => new { x.UserId, x.Scope, x.ScopeId, x.Path });
            e.Property(x => x.UserId).HasColumnType("TEXT");
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.ScopeId).HasColumnType("TEXT");
            e.Property(x => x.Path).HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("TEXT");
            e.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
        });
        mb.Entity<AccountFailureState>(e =>
        {
            e.ToTable("account_failure_states");
            e.HasKey(x => x.AccountKey);
            e.Property(x => x.AccountKey).HasMaxLength(128);
            e.Property(x => x.FirstFailureAt).HasColumnType("TEXT");
            e.Property(x => x.LastFailureAt).HasColumnType("TEXT");
            e.Property(x => x.BlockedUntil).HasColumnType("TEXT");
        });
        mb.Entity<AuthenticationSecurityEvent>(e =>
        {
            e.ToTable("authentication_security_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT");
            e.Property(x => x.EventType).IsRequired().HasMaxLength(64);
            e.Property(x => x.AccountKey).HasMaxLength(128);
            e.Property(x => x.SourceIp).IsRequired().HasMaxLength(64);
            e.Property(x => x.CreatedAt).HasColumnType("TEXT");
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.AccountKey);
        });
        mb.Entity<TunnelServerProfile>(e =>
        {
            e.ToTable("tunnel_server_profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired().HasMaxLength(256);
            e.Property(x => x.Name).IsRequired().HasMaxLength(128);
            e.Property(x => x.Host).IsRequired().HasMaxLength(253);
            e.Property(x => x.AuthKind).HasConversion<string>();
            e.Property(x => x.TlsMode).HasConversion<string>();
            e.Property(x => x.RuntimeMode).HasConversion<string>();
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
        });
        mb.Entity<TunnelDefinition>(e =>
        {
            e.ToTable("tunnel_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired().HasMaxLength(256);
            e.Property(x => x.Name).IsRequired().HasMaxLength(128);
            e.Property(x => x.ProviderId).IsRequired().HasMaxLength(32);
            e.Property(x => x.LocalHost).IsRequired().HasMaxLength(253);
            e.Property(x => x.Protocol).HasConversion<string>();
            e.HasIndex(x => new { x.UserId, x.ServerProfileId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.ServerProfileId, x.RemotePort }).IsUnique().HasFilter("RemotePort IS NOT NULL");
            e.HasIndex(x => new { x.ServerProfileId, x.Domain }).IsUnique().HasFilter("Domain IS NOT NULL");
        });
        mb.Entity<TunnelSecret>(e =>
        {
            e.ToTable("tunnel_secrets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Purpose).IsRequired().HasMaxLength(64);
            e.Property(x => x.ProtectedValue).IsRequired();
            e.HasIndex(x => new { x.ServerProfileId, x.Purpose }).IsUnique();
        });
        mb.Entity<TunnelAuditEntry>(e =>
        {
            e.ToTable("tunnel_audit_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorUserId).IsRequired().HasMaxLength(256);
            e.Property(x => x.Action).IsRequired().HasMaxLength(64);
            e.Property(x => x.Result).IsRequired().HasMaxLength(32);
            e.Property(x => x.ProblemCode).HasMaxLength(128);
            e.HasIndex(x => x.CreatedAt);
        });
        // ── users ──
        mb.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnType("TEXT");
            e.Property(u => u.Username).IsRequired().HasMaxLength(128);
            // Platform 为枚举，存字符串与线协议（camelCase）一致
            e.Property(u => u.Platform).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(u => u.PlatformIdentity).HasMaxLength(256);
            e.Property(u => u.CreatedAt).HasColumnType("TEXT");
            e.Property(u => u.LastLoginAt).HasColumnType("TEXT");
            // 按 (username, platform) 索引——对应 InMemoryUserRepository._byName
            e.HasIndex(u => new { u.Username, u.Platform }).IsUnique();
        });

        // ── workspaces ──
        mb.Entity<Workspace>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(w => w.Id);
            e.Property(w => w.Id).HasColumnType("TEXT");
            e.Property(w => w.UserId).HasColumnType("TEXT");
            e.Property(w => w.Name).IsRequired().HasMaxLength(256);
            e.Property(w => w.State).HasConversion<string>().HasMaxLength(32);
            e.Property(w => w.CreatedAt).HasColumnType("TEXT");
            e.Property(w => w.ControllerDeviceId).HasColumnType("TEXT");
            e.Property(w => w.ControllerGrantedAt).HasColumnType("TEXT");
            e.Property(w => w.ControllerLeaseExpiresAt).HasColumnType("TEXT");
            // One User One Persistent Workspace——按 UserId 唯一索引，对应 InMemoryWorkspaceRepository._byUserId
            e.HasIndex(w => w.UserId).IsUnique();

        });

        // ── devices ──
        mb.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnType("TEXT");
            e.Property(d => d.Name).IsRequired().HasMaxLength(128);
            e.Property(d => d.Platform).IsRequired().HasMaxLength(32);
            e.Property(d => d.ClientVersion).HasMaxLength(64);
            e.Property(d => d.LastLoginAt).HasColumnType("TEXT");
            // 按 (name, platform) 索引——对应 InMemoryDeviceRepository._byKey
            e.HasIndex(d => new { d.Name, d.Platform }).IsUnique();
        });

        // ── bookmarks ── 浏览器书签：按用户隔离，同用户下 URL 唯一（UPSERT 语义靠唯一索引保证）
        mb.Entity<Bookmark>(e =>
        {
            e.ToTable("bookmarks");
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasColumnType("TEXT");
            e.Property(b => b.UserId).HasColumnType("TEXT");
            e.Property(b => b.Title).HasMaxLength(512);
            e.Property(b => b.Url).IsRequired().HasMaxLength(2048);
            e.Property(b => b.CreatedAt).HasColumnType("TEXT");
            // 按 (userId, url) 唯一索引——对应 InMemoryBrowserRepository._bookmarks
            e.HasIndex(b => new { b.UserId, b.Url }).IsUnique();
            // 按 UserId 普通索引——ListBookmarks 查询用
            e.HasIndex(b => b.UserId);
        });

        // ── history_entries ── 浏览器历史：按用户隔离，同用户同 URL 合并（应用层 Find+Update 实现 UPSERT）
        mb.Entity<HistoryEntry>(e =>
        {
            e.ToTable("history_entries");
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).HasColumnType("TEXT");
            e.Property(h => h.UserId).HasColumnType("TEXT");
            e.Property(h => h.Title).HasMaxLength(512);
            e.Property(h => h.Url).IsRequired().HasMaxLength(2048);
            e.Property(h => h.VisitCount).IsRequired();
            e.Property(h => h.FirstVisitedAt).HasColumnType("TEXT");
            e.Property(h => h.LastVisitedAt).HasColumnType("TEXT");
            // 按 (userId, url) 唯一索引——UPSERT 找现成条目的依据
            e.HasIndex(h => new { h.UserId, h.Url }).IsUnique();
            // 按 (userId, lastVisitedAt desc) 索引——ListHistory 排序查询用
            e.HasIndex(h => new { h.UserId, h.LastVisitedAt });
        });

        // ── app_settings ── application-private, versioned JSON configuration.
        // ScopeId is the user/workspace/device id selected by Scope; UserId remains the tenant boundary.
        mb.Entity<AppSetting>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(setting => new { setting.UserId, setting.Scope, setting.ScopeId, setting.AppId, setting.Key });
            e.Property(setting => setting.UserId).HasColumnType("TEXT");
            e.Property(setting => setting.Scope).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(setting => setting.ScopeId).HasColumnType("TEXT");
            e.Property(setting => setting.AppId).HasMaxLength(128).IsRequired();
            e.Property(setting => setting.Key).HasMaxLength(64).IsRequired();
            e.Property(setting => setting.ValueJson).IsRequired();
            e.Property(setting => setting.SchemaVersion).IsRequired();
            e.Property(setting => setting.Revision).IsConcurrencyToken().IsRequired();
            e.Property(setting => setting.UpdatedAt).HasColumnType("TEXT");
            e.HasIndex(setting => new { setting.UserId, setting.UpdatedAt });
        });

        // ── image_mirrors ── per-user mirror prefixes. Selection is maintained in the
        // repository so the default/no-mirror state needs no synthetic database row.
        mb.Entity<ImageMirror>(e =>
        {
            e.ToTable("image_mirrors");
            e.HasKey(mirror => mirror.Id);
            e.Property(mirror => mirror.Id).HasColumnType("TEXT");
            e.Property(mirror => mirror.UserId).HasColumnType("TEXT");
            e.Property(mirror => mirror.Target).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(mirror => mirror.Name).HasMaxLength(80).IsRequired();
            e.Property(mirror => mirror.Endpoint).HasMaxLength(255).IsRequired();
            e.Property(mirror => mirror.CreatedAt).HasColumnType("TEXT");
            e.Property(mirror => mirror.UpdatedAt).HasColumnType("TEXT");
            e.HasIndex(mirror => new { mirror.UserId, mirror.Target });
        });

        // ── git_repositories ── registered Git repository metadata (user-isolated).
        // Only Id/Name/Path/UserId/CreatedAt are persisted; branch/commit/status/diff are real-time git results.
        mb.Entity<GitRepository>(e =>
        {
            e.ToTable("git_repositories");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnType("TEXT");
            e.Property(r => r.UserId).HasColumnType("TEXT");
            e.Property(r => r.Name).HasMaxLength(256).IsRequired();
            e.Property(r => r.Path).HasMaxLength(4096).IsRequired();
            e.Property(r => r.CreatedAt).HasColumnType("TEXT");
            e.HasIndex(r => r.UserId);
        });
    }
}
