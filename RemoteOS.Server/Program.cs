using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using RemoteOS.Protocol.Common;
using RoyalTerminal.Terminal;
using Server.Endpoints;
using Server.Hubs;
using Server.Identity;
using Server.Storage;
using Server.Storage.Sqlite;

// `dotnet run` normally treats the project directory as ContentRoot, which would put every
// ContentRoot-relative runtime artifact under the checkout's data directory. Keep development
// data beside the compiled executable instead, while installed hosts retain their configured
// content root and storage locations.
var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environments.Production;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = environmentName,
    ContentRootPath = environmentName.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase)
        ? AppContext.BaseDirectory
        : null,
});
var kestrelCertificates = new Server.Certificate.KestrelCertificateRegistry();
builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
    https.ServerCertificateSelector = (_, hostName) => kestrelCertificates.Select(hostName)));

// Git HTTPS tokens are protected before they are persisted in application storage.
builder.Services.AddDataProtection();
// The signed host installer writes this ACL-protected file. It keeps machine-only
// Guardian IPC settings out of source-controlled appsettings.json and out of HTTP DTOs.
builder.Configuration.AddJsonFile("appsettings.host.json", optional: true, reloadOnChange: false);

// Proxy Goal 2: a Server-only, loopback-only controller adapter. There is deliberately no
// endpoint mapping or client registration until Goal 6, and no service/process management until Goal 3.
var mihomoController = builder.Configuration.GetSection("Proxy:Mihomo:Controller").Get<Server.Proxy.Mihomo.MihomoControllerOptions>()
    ?? new Server.Proxy.Mihomo.MihomoControllerOptions();
mihomoController.Validate();
builder.Services.AddSingleton(mihomoController);
builder.Services.AddSingleton<Server.Proxy.Mihomo.IProxyControllerSecretStore, Server.Proxy.Mihomo.DataProtectionProxyControllerSecretStore>();
// The controller is an optional local process. Its expected "not started" condition is
// reported by MihomoControllerClient as one actionable warning, instead of HttpClient's
// full connection exception stack on every status poll.
builder.Services.AddHttpClient<Server.Proxy.Mihomo.IMihomoControllerClient, Server.Proxy.Mihomo.MihomoControllerClient>()
    .RemoveAllLoggers();
builder.Services.AddSingleton<Server.Proxy.IProxyEngine, Server.Proxy.Mihomo.MihomoEngine>();
builder.Services.AddSingleton<Server.Proxy.IProxyEngineRegistry, Server.Proxy.ProxyEngineRegistry>();
builder.Services.AddSingleton<Server.Proxy.Mihomo.WindowsMihomoProcessHost>();
builder.Services.AddSingleton<Server.Proxy.Mihomo.IWindowsMihomoProcessHost>(sp => sp.GetRequiredService<Server.Proxy.Mihomo.WindowsMihomoProcessHost>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Server.Proxy.Mihomo.WindowsMihomoProcessHost>());
builder.Services.AddSingleton<Server.Proxy.Platform.IProxyPrivilegedOperations, Server.Proxy.Platform.NativeMihomoPrivilegedOperations>();
builder.Services.AddSingleton<Server.Proxy.IProxyPlatformPaths, Server.Proxy.Platform.ProxyPlatformPaths>();
builder.Services.AddSingleton<Server.Proxy.IProxyPlatformService, Server.Proxy.Platform.ProxyPlatformService>();
builder.Services.AddSingleton<Server.Proxy.IProxyDiagnosticLogStore, Server.Proxy.ProxyDiagnosticLogStore>();
builder.Services.AddSingleton<Server.Proxy.Mihomo.MihomoRuntimeManifest>();
builder.Services.AddSingleton<Server.Proxy.Mihomo.IMihomoRuntimeProbe, Server.Proxy.Mihomo.MihomoRuntimeProbe>();
builder.Services.AddSingleton<Server.Proxy.Mihomo.MihomoRuntimeManager>();
builder.Services.AddSingleton<Server.Proxy.IProxyRuntimeManager>(sp => sp.GetRequiredService<Server.Proxy.Mihomo.MihomoRuntimeManager>());
builder.Services.AddSingleton<Server.Proxy.Mihomo.IMihomoConfigurationValidator>(sp => sp.GetRequiredService<Server.Proxy.Mihomo.MihomoRuntimeManager>());
builder.Services.AddSingleton<Server.Proxy.IProxyGeoDataService, Server.Proxy.Mihomo.MihomoGeoDataService>();
// Provision bundled GEO files into Mihomo's -d HomeDir during Server startup, before users
// import subscriptions. The transaction service repeats this as an idempotent safety net.
builder.Services.AddHostedService<Server.Proxy.Mihomo.MihomoGeoDataHostedService>();
builder.Services.AddSingleton<Server.Proxy.IProxySettingsService, Server.Proxy.Mihomo.MihomoSettingsService>();
builder.Services.AddHostedService<Server.Proxy.Mihomo.SystemProxyGuardHostedService>();
builder.Services.AddHttpClient("MihomoRuntime", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("ProxySubscriptionDirect", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectAsync,
    });
builder.Services.AddHttpClient("ProxySubscriptionDirectInsecureTls", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectAsync,
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true,
        },
    });
builder.Services.AddHttpClient("ProxySubscriptionSystemProxy", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        // Keep the handler and the connection policy on the exact same IWebProxy instance.
        // HttpClient's proxy source is deliberately used here; WebRequest.DefaultWebProxy is
        // a different (and obsolete) proxy pipeline which can choose a different endpoint.
        var systemProxy = HttpClient.DefaultProxy;
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = systemProxy,
            AllowAutoRedirect = false,
            ConnectCallback = (context, cancellationToken) =>
                Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectUsingSystemProxyAsync(systemProxy, context, cancellationToken),
        };
    });
builder.Services.AddHttpClient("ProxySubscriptionSystemProxyInsecureTls", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var systemProxy = HttpClient.DefaultProxy;
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = systemProxy,
            AllowAutoRedirect = false,
            ConnectCallback = (context, cancellationToken) =>
                Server.Proxy.ProxySubscriptionNetworkPolicy.ConnectUsingSystemProxyAsync(systemProxy, context, cancellationToken),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };
    });
builder.Services.AddSingleton<Server.Proxy.IProxySubscriptionDownloader, Server.Proxy.ProxySubscriptionDownloader>();
builder.Services.AddSingleton<Server.Proxy.Platform.IProxyNetworkSafetyPlatform, Server.Proxy.Platform.HostProxyNetworkSafetyPlatform>();
builder.Services.AddSingleton<Server.Proxy.IProxyTunSafetyService, Server.Proxy.ProxyTunSafetyService>();
builder.Services.AddSingleton<Server.Proxy.IProxyRecoveryService>(sp => sp.GetRequiredService<Server.Proxy.IProxyTunSafetyService>());
builder.Services.AddSingleton<Server.Proxy.ProxyOperationStore>();
builder.Services.AddSingleton<Server.Proxy.ProxyAuditStore>();
builder.Services.AddSingleton<Server.Proxy.IProxyLifecycleService, Server.Proxy.ProxyLifecycleService>();
builder.Services.AddHostedService<Server.Proxy.ProxyRecoveryHostedService>();

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

// 序列化：与 RemoteOsJsonOptions.Default 对齐（camelCase + 枚举字符串），保证线协议一致
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

builder.Services.AddSingleton<AuthSessionStore>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHostedService<RefreshTokenCleanupService>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = RemoteOsAuthSchemes.User;
        options.DefaultChallengeScheme = RemoteOsAuthSchemes.User;
    })
    .AddJwtBearer(RemoteOsAuthSchemes.User, opts =>
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
        opts.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.HasClaim(RemoteOsAuthSchemes.TokenTypeClaim, RemoteOsAuthSchemes.FileCapabilityTokenType) == true)
                    context.Fail("File capability tokens cannot be used as user access tokens.");
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hubs/terminals") || path.StartsWithSegments(RemoteOsEndpoints.GuardianLogsHubPath)
                     || path.StartsWithSegments(RemoteOsEndpoints.PerformanceHubPath)))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    })
    .AddJwtBearer(RemoteOsAuthSchemes.FileCapability, opts =>
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
        opts.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.HasClaim(RemoteOsAuthSchemes.TokenTypeClaim, RemoteOsAuthSchemes.FileCapabilityTokenType) != true)
                    context.Fail("This endpoint requires a file capability token.");
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    foreach (var policyName in new[]
             {
                 Server.Files.FileAuthorizationPolicies.List,
                 Server.Files.FileAuthorizationPolicies.Read,
                 Server.Files.FileAuthorizationPolicies.Write,
                 Server.Files.FileAuthorizationPolicies.Manage,
             })
    {
        var requiredScope = Server.Files.FileAuthorizationPolicies.ScopeForPolicy(policyName);
        options.AddPolicy(policyName, policy => policy
            .AddAuthenticationSchemes(RemoteOsAuthSchemes.User, RemoteOsAuthSchemes.FileCapability)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                !context.User.HasClaim(RemoteOsAuthSchemes.TokenTypeClaim, RemoteOsAuthSchemes.FileCapabilityTokenType)
                || context.User.HasClaim(RemoteOsAuthSchemes.ScopeClaim, requiredScope)));
    }
    // JwtBearer may map the standard role claim to ClaimTypes.Role depending on the host's
    // inbound-claim mapping setting. Accept either representation, but never a client app id.
    options.AddPolicy("TunnelsRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
        || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
    options.AddPolicy("TunnelsManage", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
    options.AddPolicy("ProxyRead", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.HasClaim("role", "controller") || context.User.HasClaim("role", "observer")
        || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "observer")));
    options.AddPolicy("ProxyManage", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
    options.AddPolicy("ProxyDangerous", policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        context.User.HasClaim("role", "controller") || context.User.HasClaim(System.Security.Claims.ClaimTypes.Role, "controller")));
});

// 身份认证 Provider（按宿主 OS 平台选择，见 Authentication.md §1.1）
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<IIdentityProvider, WindowsLogonProvider>();
else if (OperatingSystem.IsLinux())
builder.Services.AddSingleton<IIdentityProvider, LinuxPamProvider>();
else
    throw new PlatformNotSupportedException("RemoteOS Server identity authentication supports Windows and Linux hosts only.");

// The Server normally remains an unprivileged service. The optional helper is a root-owned
// local executable invoked only after FileEndpoints has granted a short-lived file elevation.
var privilegedHelperOptions = builder.Configuration.GetSection("PrivilegedHelper").Get<Server.Privileged.PrivilegedHelperOptions>()
                             ?? new Server.Privileged.PrivilegedHelperOptions();
builder.Services.AddSingleton(privilegedHelperOptions);
builder.Services.AddSingleton<Server.Privileged.LocalPrivilegedOperationRunner>();
builder.Services.AddSingleton<Server.Privileged.IPrivilegedOperationTransport>(sp =>
    OperatingSystem.IsWindows()
        ? ActivatorUtilities.CreateInstance<Server.Privileged.WindowsNamedPipePrivilegedOperationTransport>(sp)
        : sp.GetRequiredService<Server.Privileged.LocalPrivilegedOperationRunner>());
builder.Services.AddSingleton<Server.Privileged.IPrivilegedFileService, Server.Privileged.PrivilegedFileService>();
builder.Services.AddSingleton<Server.Privileged.IHostElevationSessionStore, Server.Privileged.HostElevationSessionStore>();
builder.Services.AddSingleton<Server.Privileged.IFileElevationSessionStore, Server.Privileged.FileElevationSessionStore>();
builder.Services.AddSingleton<Server.Privileged.IHostAdministratorAuthenticator, Server.Privileged.HostAdministratorAuthenticator>();
builder.Services.AddSingleton<Server.ProcessGuardian.IPrivilegedNativeServiceOperations, Server.ProcessGuardian.PrivilegedNativeServiceOperations>();
builder.Services.AddSingleton<Server.WebServer.IPrivilegedNginxOperations, Server.WebServer.PrivilegedNginxOperations>();

// 任务管理器：系统指标采集 Provider（按宿主 OS 平台选择，与 IIdentityProvider 同模式）。
// CPU/内存平台特定（Linux 读 /proc；Windows 走 P/Invoke），磁盘/网络/GPU/进程跨平台共享。
// 以宿主 OS 进程身份读取，复用宿主用户/权限（不另建 ACL）。Singleton 持相邻采样差分状态。
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    builder.Services.AddSingleton<Server.SystemMonitor.ISystemMetricsProvider, Server.SystemMonitor.WindowsMetricsProvider>();
else
    builder.Services.AddSingleton<Server.SystemMonitor.ISystemMetricsProvider, Server.SystemMonitor.LinuxMetricsProvider>();

// 新任务管理器性能链路：原始 OS 读取、统一 1 秒采样、短期历史和 Hub 广播各自分层。
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    builder.Services.AddSingleton<Server.SystemPerformance.ISystemPerformanceSource, Server.SystemPerformance.WindowsPerformanceSource>();
else
    builder.Services.AddSingleton<Server.SystemPerformance.ISystemPerformanceSource, Server.SystemPerformance.LinuxPerformanceSource>();
builder.Services.AddSingleton<Server.SystemPerformance.PerformanceHistory>();
builder.Services.AddSingleton<Server.SystemPerformance.PerformanceSampler>();
builder.Services.AddSingleton<Server.SystemPerformance.IPerformanceSampler>(sp => sp.GetRequiredService<Server.SystemPerformance.PerformanceSampler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Server.SystemPerformance.PerformanceSampler>());
builder.Services.AddSingleton<Server.SystemPerformance.ProcessSampler>();
builder.Services.AddSingleton<Server.SystemPerformance.IProcessService>(sp => sp.GetRequiredService<Server.SystemPerformance.ProcessSampler>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Server.SystemPerformance.ProcessSampler>());

// Built-in Docker manager: the provider uses Docker's local CLI transport only; no socket/pipe
// is ever exposed to clients. Guardian intentionally remains a separate Agent boundary.
builder.Services.AddSingleton(builder.Configuration.GetSection("DockerEngine").Get<Server.Docker.DockerCliEngineOptions>() ?? new Server.Docker.DockerCliEngineOptions());
builder.Services.AddSingleton<Server.Docker.IDockerEngineService, Server.Docker.DockerCliEngineService>();
builder.Services.AddScoped<Server.ImageMirrors.IDockerImageMirrorResolver, Server.ImageMirrors.DockerImageMirrorResolver>();
builder.Services.AddSingleton<Server.Docker.IDockerRuntimeInstaller, Server.Docker.DockerRuntimeInstaller>();
builder.Services.Configure<Server.Docker.DockerComposeOptions>(builder.Configuration.GetSection("DockerCompose"));
builder.Services.AddSingleton<Server.Docker.IDockerComposeService, Server.Docker.DockerComposeService>();
var guardianOptions = builder.Configuration.GetSection("GuardianAgent").Get<Server.ProcessGuardian.GuardianAgentOptions>() ?? new Server.ProcessGuardian.GuardianAgentOptions();
builder.Services.AddSingleton(guardianOptions);
builder.Services.AddSingleton<Server.ProcessGuardian.IProcessGuardianService, Server.ProcessGuardian.NamedPipeProcessGuardianService>();
builder.Services.AddSingleton<Server.ProcessGuardian.IRunAsAuthorizationService, Server.ProcessGuardian.RunAsAuthorizationService>();
builder.Services.AddSingleton<Server.ProcessGuardian.IGuardianAgentInstaller, Server.ProcessGuardian.GuardianAgentInstaller>();
builder.Services.AddSingleton(builder.Configuration.GetSection("GuardianNativeServices").Get<Server.ProcessGuardian.NativeServiceAdapterOptions>() ?? new Server.ProcessGuardian.NativeServiceAdapterOptions());
builder.Services.AddSingleton<Server.ProcessGuardian.INativeServiceAdapter, Server.ProcessGuardian.NativeServiceAdapter>();

// Git client: server-side git CLI service (Singleton—holds per-repo write semaphore).
// Invokes host git CLI as the host user; credentials handled entirely by the host git credential helper.
builder.Services.AddSingleton<Server.Git.IHostGitCli, Server.Git.HostGitCli>();
builder.Services.AddSingleton<Server.Git.IGitRepositoryService, Server.Git.LocalGitRepositoryService>();

// Firewall keeps a deliberately narrow UFW-only surface. On Linux the RemoteOS Server service
// is the privileged host facade; on Windows the unavailable provider is retained only so all
// endpoint wiring has one stable abstraction (the app itself is hidden by its Linux manifest).
if (OperatingSystem.IsLinux())
    builder.Services.AddSingleton<Server.Firewall.IHostFirewallService, Server.Firewall.LinuxUfwFirewallService>();
else
    builder.Services.AddSingleton<Server.Firewall.IHostFirewallService, Server.Firewall.UnavailableHostFirewallService>();
builder.Services.AddSingleton<Server.Firewall.IFirewallChangeAuthorizationService, Server.Firewall.FirewallChangeAuthorizationService>();

// Web Server V1: host-global Nginx discovery/read state plus an explicitly confirmed,
// marker-owned conf.d integration. It never accepts shell text or elevation credentials from HTTP.
builder.Services.AddSingleton<Server.WebServer.IHostPrivilegeService, Server.WebServer.HostPrivilegeService>();
builder.Services.AddSingleton(builder.Configuration.GetSection("NginxManaged").Get<Server.WebServer.NginxManagedOptions>() ?? new Server.WebServer.NginxManagedOptions());
builder.Services.AddSingleton<Server.WebServer.NginxInstallPackageStore>();
builder.Services.AddSingleton<Server.Certificate.HostOperationJournal>();
builder.Services.AddSingleton<Server.WebServer.WebServerMetadataRepository>();
builder.Services.AddSingleton<Server.WebServer.WebServerOperationStore>();
builder.Services.AddSingleton<Server.WebServer.NginxWebServerManager>();
builder.Services.AddSingleton<Server.WebServer.IWebServerProvider>(services => services.GetRequiredService<Server.WebServer.NginxWebServerManager>());
builder.Services.AddSingleton<Server.WebServer.IWebServerManager, Server.WebServer.WebServerManager>();

// Tunnel desired state is stored separately from workspace preferences. FRP stays an external
// process; the provider only generates private configuration and supervises its own child PID.
builder.Services.AddSingleton<Server.Runtimes.IRuntimeManager, Server.Runtimes.FrpRuntimeManager>();
builder.Services.AddSingleton<Server.Tunnels.ITunnelProvider, Server.Tunnels.FrpTunnelProvider>();
builder.Services.AddSingleton<Server.Tunnels.IManagedFrpsService, Server.Tunnels.ManagedFrpsService>();
builder.Services.Configure<Server.Runtimes.FrpRuntimeOptions>(builder.Configuration.GetSection("FrpRuntime"));
builder.Services.AddHttpClient("FrpRuntime", client => client.Timeout = TimeSpan.FromMinutes(2));

// Certificate management is host-global. PEM/account keys remain behind the server-side
// store; the API exposes metadata and operation IDs only.
var certificateOptions = builder.Configuration.GetSection("Certificate").Get<Server.Certificate.CertificateOptions>() ?? new Server.Certificate.CertificateOptions();
builder.Services.AddSingleton(certificateOptions);
builder.Services.AddSingleton<Server.Certificate.FileHttp01ChallengeStore>();
builder.Services.AddSingleton<Server.Certificate.DirectHttp01ChallengeStore>();
builder.Services.AddSingleton(kestrelCertificates);
builder.Services.AddSingleton<Server.Certificate.CertificateMetadataRepository>();
builder.Services.AddSingleton<Server.Certificate.ICertificateStore, Server.Certificate.FileCertificateStore>();
builder.Services.AddSingleton<Server.Certificate.CertificateDeploymentRepository>();
builder.Services.AddSingleton<Server.Certificate.IAcmeService, Server.Certificate.AnvilAcmeService>();
builder.Services.AddSingleton<Server.Certificate.IAcmeRenewalInfoProvider>(services => (Server.Certificate.AnvilAcmeService)services.GetRequiredService<Server.Certificate.IAcmeService>());
builder.Services.AddSingleton<Server.Certificate.CertificateRenewalAttemptRepository>();
builder.Services.AddSingleton<Server.Certificate.CertificateOperationStore>();
builder.Services.AddSingleton<Server.Certificate.ICertificateManager, Server.Certificate.CertificateManager>();
builder.Services.AddHostedService<Server.Certificate.KestrelCertificateStartupService>();
builder.Services.AddHostedService<Server.Certificate.CertificateRenewalWorker>();

// 持久化仓储：按 Storage:Provider 选择 sqlite（EF Core + SQLite，默认）或 memory（内存，开发回退）。
// User / Workspace（身份与会话归属）/ Device 持久化；Workspace 配置由注册表持久化。
// 详见 docs/RemoteOS.Storage.md。
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
var storageOpts = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
var storageProvider = string.IsNullOrWhiteSpace(storageOpts.Provider) ? "sqlite" : storageOpts.Provider.ToLowerInvariant();

if (storageProvider == "sqlite")
{
    // 数据库文件路径相对 ContentRoot，自动建目录
    var dbPath = Path.Combine(builder.Environment.ContentRootPath, storageOpts.DatabasePath);
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir))
        Directory.CreateDirectory(dbDir);
    // AddDbContextFactory: 注册 IDbContextFactory<RemoteOsDbContext>（Singleton）供 Singleton 消费者
    // （如 LocalGitRepositoryService）按操作创建短生命周期 DbContext；同时保留 RemoteOsDbContext 为
    // Scoped，使既有 Scoped 仓储（SqliteUserRepository 等）直接注入不变。
    builder.Services.AddDbContextFactory<RemoteOsDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
    // 仓储为 Scoped（依赖 Scoped 的 DbContext）；Minimal API [FromServices] 每请求创建 scope，兼容
    builder.Services.AddScoped<IUserRepository, SqliteUserRepository>();
    builder.Services.AddScoped<IAuthenticationProtectionStore, SqliteAuthenticationProtectionStore>();
    builder.Services.AddScoped<IWorkspaceRepository, SqliteWorkspaceRepository>();
    builder.Services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
    builder.Services.AddScoped<IBrowserRepository, SqliteBrowserRepository>();
    builder.Services.AddScoped<IAppSettingsRepository, SqliteAppSettingsRepository>();
    // The registry is the runtime configuration source. It is hydrated once at startup and
    // batches durable SQLite writes in the background, so configuration reads never hit SQLite.
    builder.Services.AddSingleton<Server.ConfigurationRegistry.CachedSqliteRegistryRepository>();
    builder.Services.AddSingleton<IRegistryRepository>(sp => sp.GetRequiredService<Server.ConfigurationRegistry.CachedSqliteRegistryRepository>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Server.ConfigurationRegistry.CachedSqliteRegistryRepository>());
    builder.Services.AddScoped<IImageMirrorRepository, SqliteImageMirrorRepository>();
    builder.Services.AddScoped<Server.Secrets.ISecretStore, Server.Secrets.DataProtectionSecretStore>();
    builder.Services.AddScoped<Server.Tunnels.ITunnelService, Server.Tunnels.TunnelService>();
    builder.Services.AddScoped<Server.Tunnels.ITunnelAudit, Server.Tunnels.TunnelAudit>();
    builder.Services.AddSingleton<Server.Proxy.IProxyProfileRepository, Server.Proxy.SqliteProxyProfileRepository>();
    builder.Services.AddSingleton<Server.Proxy.IProxyProfileService, Server.Proxy.ProxyProfileService>();
    builder.Services.AddSingleton<Server.Proxy.IProxySubscriptionRepository, Server.Proxy.SqliteProxySubscriptionRepository>();
    builder.Services.AddScoped<Server.Proxy.IProxySubscriptionService, Server.Proxy.ProxySubscriptionService>();
    builder.Services.AddScoped<Server.Proxy.IProxyConfigurationTransactionService, Server.Proxy.ProxyConfigurationTransactionService>();
    builder.Services.AddScoped<Server.Proxy.IProxyConfigurationService, Server.Proxy.ProxyConfigurationService>();
}
else
{
    // memory：开发回退（重启丢失）
    builder.Services.AddSingleton<IUserRepository, InMemoryUserRepository>();
    builder.Services.AddSingleton<IAuthenticationProtectionStore, InMemoryAuthenticationProtectionStore>();
    builder.Services.AddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
    builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
    builder.Services.AddSingleton<IBrowserRepository, InMemoryBrowserRepository>();
    builder.Services.AddSingleton<IAppSettingsRepository, InMemoryAppSettingsRepository>();
    builder.Services.AddSingleton<IRegistryRepository, InMemoryRegistryRepository>();
    builder.Services.AddSingleton<IImageMirrorRepository, InMemoryImageMirrorRepository>();
    builder.Services.AddSingleton<Server.Tunnels.ITunnelAudit, Server.Tunnels.InMemoryTunnelAudit>();
}
// Session 始终内存（连接关系，不持久化）
builder.Services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
builder.Services.AddScoped<LoginProtectionService>();

// 终端：服务端 PTY 工厂（Windows ConPTY / Unix forkpty）+ 持久会话管理器 + SignalR Hub。
// AddSignalR 由 Microsoft.NET.Sdk.Web 隐式 FrameworkReference 提供，无需额外 NuGet。
builder.Services.AddSingleton<IPtyFactory, Server.Terminal.PlatformPtyFactory>();
builder.Services.AddSingleton<Server.Terminal.TerminalSessionManager>();
// 以 JWT sub claim 作为 Hub UserIdentifier，供 TerminalHub 按用户索引/过滤持久会话。
builder.Services.AddSingleton<IUserIdProvider, Server.Terminal.TerminalUserIdProvider>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null);
builder.Services.AddSingleton<GuardianLogSubscriptionRegistry>();
builder.Services.AddHostedService<GuardianLogBroadcastService>();
builder.Services.AddHostedService<PerformanceBroadcastService>();

// 文件管理：以宿主 OS 进程身份执行 IO，复用宿主用户/权限（不另建 ACL——见 project_memory 硬约束）。
// LocalFileService 移植自 Jaya FileSystemService 的目录枚举逻辑并扩展为完整文件操作；平台感知（Windows 盘符 / Linux "/" 根）。
builder.Services.AddSingleton<Server.Files.IFileService, Server.Files.LocalFileService>();
builder.Services.AddSingleton<Server.Files.MediaLeaseStore>();
builder.Services.AddSingleton<WorkspaceWallpaperStore>();
builder.Services.AddScoped<Server.Settings.IWorkspaceSettingsService, Server.Settings.WorkspaceSettingsService>();

// CORS（开发期允许客户端跨域）
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddOpenApi();

var app = builder.Build();

// Only configured reverse proxies can affect the source IP used by login protection.
// With no KnownProxies, ForwardedHeadersMiddleware ignores X-Forwarded-For entirely.
if (forwardedHeaders.KnownProxies.Count > 0 || forwardedHeaders.KnownIPNetworks.Count > 0)
    app.UseForwardedHeaders(forwardedHeaders);

app.Use(async (context, next) =>
{
    var language = context.Request.GetTypedHeaders().AcceptLanguage?.FirstOrDefault()?.Value.Value;
    if (!string.IsNullOrWhiteSpace(language))
        context.Response.Headers.ContentLanguage = language;
    await next();
});

// 启动时建库/建表（SQLite 模式）。EnsureCreated 零工具依赖，适合当前稳定 schema；
// 未来 schema 需演进时切换为 EF Core Migrations（db.Database.MigrateAsync）。
// 注意：EnsureCreated 只在库不存在时建表——已存在的 db 不会追加新表（如本次新增的 bookmarks/history_entries）。
// 为兼容既有部署（保留测试数据），追加 CREATE TABLE IF NOT EXISTS 增量补齐浏览器相关表。
if (storageProvider == "sqlite")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RemoteOsDbContext>();
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
        """);

    // Host-global certificate/WebServer state uses independently versioned migrations. This
    // is deliberately not an ad-hoc ALTER/CREATE compatibility patch: operations must remain
    // durable and recoverable independently from user/workspace schema evolution.
    await HostGlobalMigrationRunner.MigrateAsync(db.Database.GetDbConnection().ConnectionString, app.Lifetime.ApplicationStopping);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseCors();
    // 开发期不强制 HTTPS 重定向，方便客户端用 http 测试
}
else
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapFileEndpoints();
app.MapPrivilegedEndpoints();
app.MapAppCapabilityEndpoints();
app.MapAppSettingsEndpoints();
app.MapRegistryEndpoints();
app.MapImageMirrorEndpoints();
app.MapWorkspaceEndpoints();
app.MapBrowserEndpoints();
app.MapSystemMonitorEndpoints();
app.MapDockerEndpoints();
app.MapProcessGuardianEndpoints();
app.MapWebServerEndpoints();
app.MapCertificateEndpoints();
app.MapGitEndpoints();
app.MapTunnelEndpoints();
app.MapProxyEndpoints();
if (OperatingSystem.IsLinux())
    app.MapFirewallEndpoints();
app.MapHub<TerminalHub>("/hubs/terminals");
app.MapHub<GuardianLogsHub>(RemoteOsEndpoints.GuardianLogsHubPath);
app.MapHub<PerformanceHub>(RemoteOsEndpoints.PerformanceHubPath);

app.Run();
