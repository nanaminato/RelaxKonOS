using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

internal static class MihomoTunHttpAcceptance
{
    private static readonly Guid ProfileId = Guid.Parse("8fec824f-778c-4cf5-b81d-94fb4618cae5");

    internal static async Task RunAsync(string root, MihomoPublicationStore store, IProxyPlatformPaths paths,
        IProxyControllerSecretStore secrets, IServerModeResolver mode, string socket, bool firstRun, bool pending, bool manual)
    {
        var quarantine = !firstRun && pending && File.Exists(Path.Combine(paths.GetStateDirectory(), "http-quarantine"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://192.0.2.2:18970");
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("ProxyDangerous", policy => policy.RequireAuthenticatedUser().RequireClaim("role", "controller"));
            options.AddPolicy("ProxyRead", policy => policy.RequireAuthenticatedUser().RequireClaim("role", "controller", "observer"));
            options.AddPolicy(RuntimeCatalogEndpoints.ReadPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(RuntimeCatalogEndpoints.ManagePolicy, policy => policy.RequireClaim("permission", "manage"));
        });
        var ids = new HashSet<string> { ServerModules.ProxyId };
        builder.Services.AddSingleton(services => new ModuleResidencyCoordinator([new ResidentModuleWorkRuntimeFactory(ServerModules.ProxyId)],
            ModuleResidencyOptions.Parse(builder.Configuration, ids, ids), services,
            ServerModuleContext.Create(builder.Configuration, builder.Environment, "private-systemd-http"),
            NullLogger<ModuleResidencyCoordinator>.Instance, residentWorkModuleIds: ids));
        builder.Services.AddSingleton<IProxyWorkLeaseSource, ProxyWorkLeaseSource>();
        builder.Services.AddSingleton<IResidentModuleWorkLeaseSource, ResidentModuleWorkLeaseSource>();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new MihomoRuntimeAuthorizationProvider(store, paths, secrets));
        builder.Services.AddSingleton(services => new MihomoRuntimeService(
            new PublicationTransport(socket, paths.GetStateDirectory(), store, services.GetRequiredService<ModuleResidencyCoordinator>(),
                firstRun, pending, quarantine || manual),
            store, services.GetRequiredService<MihomoRuntimeAuthorizationProvider>(), mode, new DirectDownloads()));
        builder.Services.AddSingleton<IProxyProfileRepository>(MihomoPublicationTransactionChecks.Stub<IProxyProfileRepository>(member =>
            member.Name == nameof(IProxyProfileRepository.GetActiveAsync)
                ? Task.FromResult<ProxyProfileDto?>(new(ProfileId, "private", "mihomo", true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
                : throw new InvalidOperationException("Unexpected fixture profile call.")));
        builder.Services.AddSingleton<IProxySettingsReader, Settings>();
        builder.Services.AddSingleton<IProxyNetworkSafetyPlatform, HostProxyNetworkSafetyPlatform>();
        builder.Services.AddSingleton<IProxyTunSafetyService, ProxyTunSafetyService>();
        builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddSingleton<ProxyOperationStore>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ProxyOperationStore>());
        builder.Services.AddScoped<ProxyLifecycleOperations>();
        builder.Services.AddScoped<MihomoManagedTunOperations>();
        builder.Services.AddTransient<MihomoConfigurationPublicationService>();
        builder.Services.AddTransient<IMihomoConfigurationPublicationRecovery>(services =>
            services.GetRequiredService<MihomoConfigurationPublicationService>());
        builder.Services.AddTransient<MihomoPublicationRecoveryService>();
        if (manual)
        {
            builder.Services.AddSingleton(mode);
            builder.Services.AddSingleton(new RuntimeCatalogService(
                new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
                    NullLogger<UnixSocketPrivilegedOperationTransport>.Instance), new DirectDownloads()));
            builder.Services.AddSingleton<IHostElevationSessionStore, MihomoRuntimeHttpChecks.Elevations>();
            builder.Services.AddTransient(services => new MihomoRuntimeRecoveryService(store,
                services.GetRequiredService<MihomoRuntimeService>(), services.GetRequiredService<IMihomoConfigurationPublicationRecovery>(),
                null!, null!, workLeases: services.GetRequiredService<IProxyWorkLeaseSource>()));
        }
        else builder.Services.AddHostedService<MihomoPublicationRecoveryScheduler>();
        await using var app = builder.Build();
        if (quarantine)
        {
            var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
            using var admission = await coordinator.AcquireAsync(ServerModules.ProxyId);
            // Inject the same retained generation lease as a failed management cleanup before Core starts.
            new ModuleWorkLeaseSource(coordinator, admission.Runtime).Acquire().RequireRecovery();
            app.MapGet("/api/v1.0/proxy/fixture-core-publication", async (CancellationToken ct) =>
            {
                var owner = coordinator.Describe(ServerModules.ProxyId)!;
                return Results.Json(new { pending = (await store.ReadAsync(ct)).Pending?.OperationId,
                    state = owner.State.ToString(), owner.Generation, owner.ActiveLeases });
            }).RequireAuthorization("ProxyDangerous");
        }
        app.UseServerProblemMapping();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (!firstRun || context.Request.Headers["X-Fixture-Drop"] != "accepted")
            {
                await next(context);
                return;
            }
            var original = context.Response.Body;
            await using var buffered = new MemoryStream();
            context.Response.Body = buffered;
            try
            {
                await next(context);
                if (context.Response.StatusCode != StatusCodes.Status202Accepted)
                    throw new InvalidOperationException("The authenticated TUN request was not admitted.");
                await File.WriteAllBytesAsync(Path.Combine(paths.GetStateDirectory(), "http-admitted"), buffered.ToArray());
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(Path.Combine(paths.GetStateDirectory(), "http-disconnected"), "aborted");
            }
            finally { context.Response.Body = original; }
        });
        app.MapTunOperationEndpoints();
        if (manual) app.MapRuntimeCatalogEndpoints();
        app.MapGet("/api/v1.0/proxy/fixture-work-leases", (ModuleResidencyCoordinator coordinator) =>
            coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases ?? 0).RequireAuthorization("ProxyDangerous");
        await app.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(paths.GetStateDirectory(), "http-ready"), Environment.ProcessId.ToString());
        await app.WaitForShutdownAsync();
    }

    private sealed class Settings : IProxySettingsReader
    {
        public Task<ProxySettingsDto> GetAsync(CancellationToken ct) =>
            Task.FromResult(new ProxySettingsDto(false, false, false, false, false, "info", 18881,
                Tun: new("gvisor", "rk-fd-proof", false, false, false, "", 1500)));
    }

    private sealed class PublicationTransport(string socket, string state, MihomoPublicationStore store,
        ModuleResidencyCoordinator coordinator, bool firstRun, bool pending, bool managementLeasePresent)
        : IPrivilegedOperationTransport
    {
        private readonly UnixSocketPrivilegedOperationTransport inner = new(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            var cut = Path.Combine(state, "http-action-cut");
            var action = request.Operation switch
            {
                PrivilegedOperationKind.MihomoRuntimeTunStart => "start",
                PrivilegedOperationKind.MihomoRuntimeTunStop => "stop",
                _ => null
            };
            var boundary = action is not null && File.Exists(cut) ? await File.ReadAllTextAsync(cut, ct) : null;
            if (boundary is not null && boundary == action + "-before")
                await HoldTunActionAsync(request, "before");
            var result = await inner.ExecuteAsync(request, ct);
            if (!result.Success) return result;
            if (boundary is not null && boundary == action)
                await HoldTunActionAsync(request, "after");
            if (firstRun && !pending && request.Operation == PrivilegedOperationKind.MihomoRuntimeConfigure)
            {
                if (coordinator.Describe(ServerModules.ProxyId) is not { ActiveLeases: >= 2 })
                    throw new InvalidOperationException("The queued HTTP operation lost its publication work leases.");
                await File.WriteAllTextAsync(Path.Combine(state, "server-cut-ready"), request.OperationId!.Value.ToString("N"));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            if (!firstRun && pending && request.Operation == PrivilegedOperationKind.MihomoRuntimeRecover)
            {
                var expectedLeases = managementLeasePresent ? 1 : 0;
                if (store.Gate.CurrentCount != 0 || coordinator.Describe(ServerModules.ProxyId)?.ActiveLeases != expectedLeases)
                    throw new InvalidOperationException("The original recovery did not hold its publication gate and expected management leases through the Helper response.");
                var ready = Path.Combine(state, "http-recovery-reply");
                await File.WriteAllTextAsync(ready + ".new",
                    $"{request.RuntimeLifecycle!.RecoveryOperationId!.Value:N} {request.OperationId!.Value:D}");
                File.Move(ready + ".new", ready);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(Path.Combine(state, "http-recovery-release")))
                    await Task.Delay(25, timeout.Token);
            }
            return result;
        }

        private async Task HoldTunActionAsync(PrivilegedOperationRequest request, string boundary)
        {
            if (coordinator.Describe(ServerModules.ProxyId) is not { ActiveLeases: >= 1 })
                throw new InvalidOperationException("The named TUN action lost its work lease.");
            var ready = Path.Combine(state, "http-action-ready");
            await File.WriteAllTextAsync(ready + ".new", $"{request.OperationId!.Value:N} {boundary}");
            File.Move(ready + ".new", ready);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = Request.Headers.Authorization.ToString();
            var role = identity switch
            {
                "Bearer controller" => "controller",
                "Bearer installer" => "controller",
                "Bearer elevated-installer" => "controller",
                "Bearer observer" => "observer",
                _ => null
            };
            if (role is null) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "private-http-" + role), new("role", role) };
            if (identity is "Bearer installer" or "Bearer elevated-installer")
            {
                claims.Add(new("permission", "manage"));
                claims.Add(new("installation:install", "mihomo"));
                if (identity == "Bearer elevated-installer") claims.Add(new("test-elevated", "true"));
            }
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
