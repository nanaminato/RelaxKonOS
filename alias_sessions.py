exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
edit('RelaxKonOS.Server/Identity/AuthSessionStore.cs','DateTimeOffset expiresAt, DateTimeOffset absoluteExpiresAt)','DateTimeOffset expiresAt, DateTimeOffset absoluteExpiresAt, string authenticationMethod, DateTimeOffset authenticatedAt, long securityVersion)')
edit('RelaxKonOS.Server/Identity/AuthSessionStore.cs','new RefreshRecord(sessionId, userId, workspaceId, deviceId, expiresAt, absoluteExpiresAt)','new RefreshRecord(sessionId, userId, workspaceId, deviceId, expiresAt, absoluteExpiresAt, authenticationMethod, authenticatedAt, securityVersion)')
edit('RelaxKonOS.Server/Identity/AuthSessionStore.cs','    /// <summary>吊销刷新令牌', '''    public event Action<Guid>? UserRevoked;
    public void RevokeUser(Guid userId)
    {
        foreach (var entry in _refresh)
            if (entry.Value.UserId == userId) _refresh.TryRemove(entry.Key, out _);
        UserRevoked?.Invoke(userId);
    }
    /// <summary>吊销刷新令牌''')
edit('RelaxKonOS.Server/Identity/AuthSessionStore.cs','    DateTimeOffset AbsoluteExpiresAt);','    DateTimeOffset AbsoluteExpiresAt,\n    string AuthenticationMethod, DateTimeOffset AuthenticatedAt, long SecurityVersion);')
edit('RelaxKonOS.Server/Identity/JwtTokenService.cs','Guid? sessionId = null, DateTimeOffset? absoluteExpiresAt = null)', 'Guid sessionId, string authenticationMethod, DateTimeOffset authenticatedAt, long securityVersion, DateTimeOffset? absoluteExpiresAt = null)')
edit('RelaxKonOS.Server/Identity/JwtTokenService.cs','var currentSessionId = sessionId ?? Guid.NewGuid();','var currentSessionId = sessionId;')
edit('RelaxKonOS.Server/Identity/JwtTokenService.cs','            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),','''            new Claim("sid", sessionId.ToString("D")),
            new Claim("amr", authenticationMethod),
            new Claim("auth_time", authenticatedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim("security_version", securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),''')
edit('RelaxKonOS.Server/Identity/JwtTokenService.cs','device.Id, refreshExp, absoluteExp);','device.Id, refreshExp, absoluteExp, authenticationMethod, authenticatedAt, securityVersion);')
edit('RelaxKonOS.Server/Identity/JwtTokenService.cs','IReadOnlyCollection<string> scopes)','IReadOnlyCollection<string> scopes, long securityVersion)')
edit('RelaxKonOS.Server/Identity/JwtTokenService.cs','            new(JwtRegisteredClaimNames.Sub, userId.ToString()),','            new("security_version", securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),\n            new(JwtRegisteredClaimNames.Sub, userId.ToString()),')
edit('RelaxKonOS.Server/Domain/Session.cs','    public Guid Id { get; set; }','''    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AuthenticationMethod { get; set; } = "system";
    public DateTimeOffset AuthenticatedAt { get; set; }''')
edit('RelaxKonOS.Server/Domain/Session.cs','LastActiveAt, Status);','LastActiveAt, Status, UserId, AuthenticationMethod, AuthenticatedAt);')
edit('Shared/RelaxKonOS.Protocol/Workspace/SessionDto.cs','[property: JsonPropertyName("status")] SessionStatus Status);','''[property: JsonPropertyName("status")] SessionStatus Status,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("authenticationMethod")] string AuthenticationMethod,
    [property: JsonPropertyName("authenticatedAt")] DateTimeOffset AuthenticatedAt);''')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','    private readonly TimeSpan _ttl;', '    private readonly SessionValidityService _validity;\n    private readonly TimeSpan _ttl;')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','MediaLeaseStore(IOptions<JwtOptions> options)','MediaLeaseStore(IOptions<JwtOptions> options, SessionValidityService validity)')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','        _ttl = options.Value.MediaLeaseTtl;', '        _validity = validity;\n        _ttl = options.Value.MediaLeaseTtl;')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','string appId, string path)','string appId, string path, long securityVersion)')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','now.Add(_maximumLifetime));','now.Add(_maximumLifetime), securityVersion);')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','lease.ExpiresAt > DateTimeOffset.UtcNow)','lease.ExpiresAt > DateTimeOffset.UtcNow && _validity.IsValid(lease.UserId, lease.SecurityVersion))')
edit('RelaxKonOS.Server/Files/MediaLeaseStore.cs','DateTimeOffset MaximumExpiresAt);','DateTimeOffset MaximumExpiresAt, long SecurityVersion);')
edit('RelaxKonOS.Server/Endpoints/AppCapabilityEndpoints.cs','request.AppId, request.Scopes));','request.AppId, request.Scopes, long.Parse(principal.FindFirstValue("security_version")!)));')
edit('RelaxKonOS.Server/Endpoints/AppCapabilityEndpoints.cs','request.AppId, request.Path);','request.AppId, request.Path, long.Parse(principal.FindFirstValue("security_version")!));')
