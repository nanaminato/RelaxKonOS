exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
p='RelaxKonOS.Server/Endpoints/AuthEndpoints.cs'
edit(p,'        app.MapPost(AuthApiRoutes.Login', '''        var group = app.MapGroup("").AddEndpointFilter<AuthenticationEndpointFilter>();
        group.MapPost(AuthApiRoutes.Login''')
edit(p,'                IIdentityProvider idp,','                LoginAuthenticationService authentication,')
s=Path(p).read_text(); start=s.index('                if (string.IsNullOrWhiteSpace(req.Username)'); end=s.index('                // 查/建 Workspace',start)
s=s[:start]+'''                var login = await authentication.AuthenticateAsync(req.Identifier, req.Password, http.Connection.RemoteIpAddress, ct);
                var user = login.User;
                var now = DateTimeOffset.UtcNow;

'''+s[end:]; Path(p).write_text(s,encoding='utf-8')
edit(p,'                    Id = Guid.NewGuid(),\n                    WorkspaceId = ws.Id,','''                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    AuthenticationMethod = login.Method,
                    AuthenticatedAt = now,
                    WorkspaceId = ws.Id,''')
edit(p,'var tokens = jwt.Issue(user, ws, device, role);','''authentication.RequireCurrent(login);
                var tokens = jwt.Issue(user, ws, device, role, session.Id, login.Method, now, login.SecurityVersion);
                await protection.RecordSuccessAsync(login.ProtectionKey, http.Connection.RemoteIpAddress, ct);''')
edit(p,'        app.MapPost(AuthApiRoutes.Refresh','        group.MapPost(AuthApiRoutes.Refresh')
edit(p,'                JwtTokenService jwt) =>','''                JwtTokenService jwt,
                CanonicalUserResolver resolver,
                SessionValidityService validity) =>''')
edit(p,'                if (user is null || ws is null || device is null)','                if (user is null || ws is null || device is null || !validity.IsValid(rec.UserId, rec.SecurityVersion))')
edit(p,'                var tokens = jwt.Issue(user, ws, device, role, rec.SessionId, rec.AbsoluteExpiresAt);','''                resolver.RequireBinding(user, rec.AuthenticationMethod == "alias");
                if (!validity.IsValid(rec.UserId, rec.SecurityVersion)) return Results.Unauthorized();
                var tokens = jwt.Issue(user, ws, device, role, rec.SessionId, rec.AuthenticationMethod, rec.AuthenticatedAt, rec.SecurityVersion, rec.AbsoluteExpiresAt);''')
edit(p,'        app.MapPost(AuthApiRoutes.Logout','        group.MapPost(AuthApiRoutes.Logout')
edit(p,'        app.MapGet(AuthApiRoutes.Me','        group.MapGet(AuthApiRoutes.Me')
s=Path(p).read_text(); a=s.index('    /// <summary>CredentialError'); b=s.index('    private static IResult Problem',a); s=s[:a]+s[b:]; Path(p).write_text(s,encoding='utf-8')
edit('Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs','public sealed record LoginRequest(','[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]\npublic sealed record LoginRequest(')
edit('Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs','JsonPropertyName("username")] string Username','JsonPropertyName("identifier")] string Identifier')
p='RelaxKonOS.Server/Program.cs'
edit(p,'builder.Services.AddSingleton<AuthSessionStore>();','''builder.Services.AddSingleton<AuthenticationGate>();
builder.Services.AddSingleton<AliasPasswordService>();
builder.Services.AddSingleton<SessionValidityService>();
builder.Services.AddSingleton<SessionValidityHubFilter>();
builder.Services.AddScoped<CanonicalUserResolver>();
builder.Services.AddScoped<LoginAuthenticationService>();
builder.Services.AddScoped<AliasCredentialService>();
builder.Services.AddSingleton<AuthSessionStore>();''')
edit(p,'        opts.Events = new JwtBearerEvents','        opts.MapInboundClaims = false;\n        opts.Events = new JwtBearerEvents')
edit(p,'            OnTokenValidated = context =>\n            {','''            OnTokenValidated = context =>
            {
                if (!context.HttpContext.RequestServices.GetRequiredService<SessionValidityService>().IsValid(context.Principal))
                    context.Fail("Session is no longer valid.");''')
edit(p,'    builder.Services.AddScoped<IUserRepository, SqliteUserRepository>();','    builder.Services.AddScoped<IAliasCredentialRepository, SqliteAliasCredentialRepository>();\n    builder.Services.AddScoped<IUserRepository, SqliteUserRepository>();')
edit(p,'    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();','    builder.Services.AddSingleton<IAliasCredentialRepository, InMemoryAliasCredentialRepository>();\n    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();')
edit(p,'builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null);','builder.Services.AddSignalR(options => { options.MaximumReceiveMessageSize = null; options.AddFilter<SessionValidityHubFilter>(); });')
edit(p,'app.MapAuthEndpoints();','app.MapAuthEndpoints();\napp.MapAliasEndpoints();')
edit(p,'app.MapHub<TerminalHub>("/hubs/terminals");','app.MapHub<TerminalHub>("/hubs/terminals", options => options.CloseOnAuthenticationExpiration = true);')
edit(p,'app.MapHub<GuardianLogsHub>(RelaxKonOSEndpoints.GuardianLogsHubPath);','app.MapHub<GuardianLogsHub>(RelaxKonOSEndpoints.GuardianLogsHubPath, options => options.CloseOnAuthenticationExpiration = true);')
edit(p,'app.MapHub<PerformanceHub>(RelaxKonOSEndpoints.PerformanceHubPath);','app.MapHub<PerformanceHub>(RelaxKonOSEndpoints.PerformanceHubPath, options => options.CloseOnAuthenticationExpiration = true);')
edit(p,'    await HostGlobalMigrationRunner.MigrateAsync(db.Database.GetDbConnection().ConnectionString, app.Lifetime.ApplicationStopping);','''    await HostGlobalMigrationRunner.MigrateAsync(db.Database.GetDbConnection().ConnectionString, app.Lifetime.ApplicationStopping);
    IdentityMigrationRunner.Migrate(db, scope.ServiceProvider.GetRequiredService<IIdentityProvider>());''')
