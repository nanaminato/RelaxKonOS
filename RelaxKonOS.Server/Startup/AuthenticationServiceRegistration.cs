using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Startup;

internal static class AuthenticationServiceRegistration
{
    public static ForwardedHeadersOptions AddServerAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<AuthSecurityOptions>(builder.Configuration.GetSection("AuthenticationSecurity"));
        var authSecurity = builder.Configuration.GetSection("AuthenticationSecurity").Get<AuthSecurityOptions>() ?? new AuthSecurityOptions();
        if (authSecurity.EndpointPermitLimit <= 0 || authSecurity.EndpointWindowSeconds <= 0
            || authSecurity.IpFailureLimit <= 0 || authSecurity.IpFailureWindowMinutes <= 0
            || authSecurity.IpBlockMinutes <= 0 || authSecurity.AccountFailureRetentionHours <= 0)
            throw new InvalidOperationException("AuthenticationSecurity values must be greater than zero.");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = authSecurity.EndpointWindowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy("login", http => RateLimitPartition.GetTokenBucketLimiter(
                http.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = authSecurity.EndpointPermitLimit,
                    TokensPerPeriod = authSecurity.EndpointPermitLimit,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(authSecurity.EndpointWindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });

        var forwardedHeaders = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        foreach (var proxy in authSecurity.TrustedProxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
                throw new InvalidOperationException($"AuthenticationSecurity:TrustedProxies contains an invalid IP address: {proxy}");
            forwardedHeaders.KnownProxies.Add(address);
        }
        foreach (var network in authSecurity.TrustedNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out var parsedNetwork))
                throw new InvalidOperationException($"AuthenticationSecurity:TrustedNetworks contains an invalid CIDR: {network}");
            forwardedHeaders.KnownIPNetworks.Add(parsedNetwork);
        }

        // 序列化：与 RelaxKonOSJsonOptions.Default 对齐（camelCase + 枚举字符串），保证线协议一致
        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.SerializerOptions.PropertyNameCaseInsensitive = true;
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        // JWT 配置（绑定 + 启动校验）
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        var jwtSection = builder.Configuration.GetSection("Jwt");

        if (!jwtSection.Exists())
        {
            throw new InvalidOperationException(
                $"Missing 'Jwt' configuration section. " +
                $"Environment={builder.Environment.EnvironmentName}, " +
                $"ContentRoot={builder.Environment.ContentRootPath}");
        }

        var jwtCfg = jwtSection.Get<JwtOptions>()
                     ?? throw new InvalidOperationException("Failed to bind Jwt configuration.");
        if (string.IsNullOrWhiteSpace(jwtCfg.Secret) || jwtCfg.Secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret 必须至少 32 字符（HMACSHA256 要求 ≥256 位）。");
        if (builder.Environment.IsProduction() && jwtCfg.Secret == JwtOptions.DefaultInsecureSecret)
            throw new InvalidOperationException("Production 环境必须替换默认 Jwt:Secret。");
        if (jwtCfg.AccessTokenTtl <= TimeSpan.Zero || jwtCfg.RefreshTokenTtl <= TimeSpan.Zero
            || jwtCfg.RefreshTokenMaximumLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("Jwt token lifetimes must be greater than zero.");
        if (jwtCfg.RefreshTokenMaximumLifetime < jwtCfg.AccessTokenTtl)
            throw new InvalidOperationException("Jwt:RefreshTokenMaximumLifetime must not be shorter than Jwt:AccessTokenTtl.");

        builder.Services.AddSingleton<AuthenticationGate>();
        builder.Services.AddSingleton<AliasPasswordService>();
        builder.Services.AddSingleton<SessionValidityService>();
        builder.Services.AddSingleton<SessionValidityHubFilter>();
        builder.Services.AddSingleton<OwnerDeviceKeyService>();
        builder.Services.AddScoped<CanonicalUserResolver>();
        builder.Services.AddScoped<RelaxKonOS.Server.UserExecution.IUserExecutionContextResolver,
            RelaxKonOS.Server.UserExecution.UserExecutionContextResolver>();
        builder.Services.AddScoped<LoginAuthenticationService>();
        builder.Services.AddScoped<AliasCredentialService>();
        builder.Services.AddSingleton<AuthSessionStore>();
        builder.Services.AddHostedService<AuthenticationRetentionService>();
        builder.Services.AddSingleton<JwtTokenService>();
        builder.Services.AddHostedService<RefreshTokenCleanupService>();

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = RelaxKonOSAuthSchemes.User;
                options.DefaultChallengeScheme = RelaxKonOSAuthSchemes.User;
            })
            .AddJwtBearer(RelaxKonOSAuthSchemes.User, opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtCfg.Issuer,
                    ValidAudience = jwtCfg.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg.Secret)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                // SignalR WebSocket 升级请求无法可靠携带 Authorization 头（.NET 客户端走头，但补齐 query 兜底）。
                // 对终端 Hub 路径，从查询串 access_token 读取令牌注入 JwtBearer，修复 WebSocket 升级 401。
                opts.MapInboundClaims = false;
                opts.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!context.HttpContext.RequestServices.GetRequiredService<SessionValidityService>().IsValid(context.Principal))
                            context.Fail("Session is no longer valid.");
                        if (context.Principal?.HasClaim(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType) == true)
                            context.Fail("File capability tokens cannot be used as user access tokens.");
                        return Task.CompletedTask;
                    },
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) &&
                            (path.StartsWithSegments("/hubs/terminals") || path.StartsWithSegments(RelaxKonOSEndpoints.GuardianLogsHubPath)
                             || path.StartsWithSegments(RelaxKonOSEndpoints.PerformanceHubPath)
                             || path.StartsWithSegments(RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath)
                             || path.StartsWithSegments(RelaxKonOSEndpoints.SettingsChangesHubPath)))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            })
            .AddNegotiate(RelaxKonOSAuthSchemes.WindowsOwnerDeviceBootstrap, _ => { })
            .AddJwtBearer(RelaxKonOSAuthSchemes.FileCapability, opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtCfg.Issuer,
                    ValidAudience = jwtCfg.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg.Secret)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                opts.MapInboundClaims = false;
                opts.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!context.HttpContext.RequestServices.GetRequiredService<SessionValidityService>().IsValid(context.Principal))
                            context.Fail("Session is no longer valid.");
                        if (context.Principal?.HasClaim(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType) != true)
                            context.Fail("This endpoint requires a file capability token.");
                        return Task.CompletedTask;
                    },
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("WindowsOwnerDeviceBootstrap", policy => policy
                .AddAuthenticationSchemes(RelaxKonOSAuthSchemes.WindowsOwnerDeviceBootstrap)
                .RequireAuthenticatedUser());
            foreach (var policyName in new[]
                     {
                         RelaxKonOS.Server.Files.FileAuthorizationPolicies.List,
                         RelaxKonOS.Server.Files.FileAuthorizationPolicies.Read,
                         RelaxKonOS.Server.Files.FileAuthorizationPolicies.Write,
                         RelaxKonOS.Server.Files.FileAuthorizationPolicies.Manage,
                     })
            {
                var requiredScope = RelaxKonOS.Server.Files.FileAuthorizationPolicies.ScopeForPolicy(policyName);
                options.AddPolicy(policyName, policy => policy
                    .AddAuthenticationSchemes(RelaxKonOSAuthSchemes.User, RelaxKonOSAuthSchemes.FileCapability)
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        !context.User.HasClaim(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType)
                        || context.User.HasClaim(RelaxKonOSAuthSchemes.ScopeClaim, requiredScope)));
            }
            // JwtBearer may map the standard role claim to ClaimTypes.Role depending on the host's
            // inbound-claim mapping setting. Accept either representation, but never a client app id.
            options.AddPolicy("TunnelsRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
                || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
            options.AddPolicy("TunnelsManage", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
            options.AddPolicy("EventsRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
                || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
            options.AddPolicy("EventsManage", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
            options.AddPolicy("EventsCriticalSuppress", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
            options.AddPolicy("ProxyRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
                || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
            options.AddPolicy("ProxyManage", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
            options.AddPolicy("ProxyDangerous", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
            options.AddPolicy("FileServicesRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
                || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
            options.AddPolicy("FileServicesManage", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
            // Containerised application deployment follows the same role boundary as the other host-mutating
            // features: an observer may read, and only a controller may change a running workload.
            options.AddPolicy(RelaxKonOS.Server.Endpoints.ApplicationDeploymentEndpoints.ReadPolicy, policy =>
                policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
                    || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")
                    || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
            options.AddPolicy(RelaxKonOS.Server.Endpoints.ApplicationDeploymentEndpoints.ManagePolicy, policy =>
                policy.RequireAuthenticatedUser().RequireAssertion(context =>
                    context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
        });
        return forwardedHeaders;
    }
}
