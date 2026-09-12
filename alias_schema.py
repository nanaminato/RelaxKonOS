exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
edit('RelaxKonOS.Server/Domain/AuthenticationProtection.cs','    public Guid Id { get; set; }','''    public Guid Id { get; set; }
    public Guid? CanonicalUserId { get; set; }
    public Guid? SessionId { get; set; }
    public string? AuthenticationMethod { get; set; }
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public long? Revision { get; set; }
    public string ActorKind { get; set; } = "User";''')
edit('RelaxKonOS.Server/Storage/Sqlite/RelaxKonOSDbContext.cs','    public DbSet<User> Users', '    public DbSet<AliasCredential> LoginCredentials => Set<AliasCredential>();\n    public DbSet<User> Users')
edit('RelaxKonOS.Server/Storage/Sqlite/RelaxKonOSDbContext.cs','        mb.Entity<RegistryEntry>(e =>','''        mb.Entity<AliasCredential>(e =>
        {
            e.ToTable("user_login_credentials", table => {
                table.HasCheckConstraint("CK_alias_pair", "(Alias IS NULL AND PasswordHash IS NULL) OR (Alias IS NOT NULL AND PasswordHash IS NOT NULL)");
                table.HasCheckConstraint("CK_login_method", "SystemLoginEnabled = 1 OR (Alias IS NOT NULL AND PasswordHash IS NOT NULL)");
                table.HasCheckConstraint("CK_alias_format", "Alias IS NULL OR (length(Alias) BETWEEN 3 AND 32 AND Alias NOT GLOB '*[^a-z0-9._-]*' AND substr(Alias,1,1) GLOB '[a-z]')");
                table.HasCheckConstraint("CK_alias_revision", "Revision > 0");
            });
            e.HasKey(x => x.UserId);
            e.HasOne<User>().WithOne().HasForeignKey<AliasCredential>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Alias).IsUnique();
            e.Property(x => x.Revision).IsConcurrencyToken();
        });
        mb.Entity<RegistryEntry>(e =>''')
edit('Shared/RelaxKonOS.Protocol/Identity/AuthApiRoutes.cs','    public const string Me =', '''    public const string LoginAlias = $"/{V1}/auth/me/login-alias";
    public const string AliasPassword = LoginAlias + "/password";
    public const string DeleteAlias = LoginAlias + "/delete";
    public const string SystemLogin = $"/{V1}/auth/me/system-login";
    public const string Me =''')
