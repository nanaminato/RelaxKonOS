using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Startup;

/// <summary>Explicit authenticated recovery surface. No module or business coordinator is registered.</summary>
internal static class ModuleRecoveryHost
{
    private sealed class IdentityLease(FileStream stream) : IDisposable
    { public void Dispose() => stream.Dispose(); }
    internal static async Task<WebApplication> BuildAsync(WebApplicationBuilder builder, ModuleManagementStore store)
    {
        if (!store.RecoveryMode) throw new InvalidOperationException("Recovery host requires recovery mode.");
        var modules = ServerModules.CreateManaged(new HashSet<string>(StringComparer.Ordinal));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(modules);
        // Configuration apply is synchronous and durable. Runtime actions are refused by the store;
        // the executor is registered for the shared endpoint signature, never as a hosted worker.
        builder.Services.AddSingleton<ModuleManagementExecutor>();
        builder.AddServerObservability(builder.Environment.IsDevelopment(), includeEventAlerts: false);
        var execution = UserExecutionBackendResolver.Resolve(builder.Configuration, builder.Environment);
        var mode = new ServerModeResolver(builder.Configuration, execution);
        builder.Services.AddSingleton<IServerModeResolver>(mode);
        builder.ConfigureHostListeners(mode);
        builder.Services.AddHostDataProtection();
        var forwarded = builder.AddServerAuthentication();
        builder.AddHostIdentity(mode, execution, authenticationOnly: true);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new ServerRuntimeSettingsStore(Path.Combine(builder.Environment.ContentRootPath, "data", "server-runtime-settings.json")));
        var storage = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        var provider = string.IsNullOrWhiteSpace(storage.Provider) ? "sqlite" : storage.Provider.ToLowerInvariant();
        if (provider is not ("sqlite" or "memory")) throw new InvalidOperationException("Unknown recovery storage provider.");
        builder.AddServerStorage(storage, provider, identityOnly: true);
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, provider);
        builder.Services.AddSingleton(context);
        // Match normal host ownership of the identity DB for the entire recovery process lifetime.
        var identityLock = provider == "sqlite" ? StorageStartup.AcquireIdentityHostLock(Path.Combine(builder.Environment.ContentRootPath, storage.DatabasePath)) : null;
        if (identityLock is not null) builder.Services.AddSingleton(_ => new IdentityLease(identityLock));
        WebApplication? app = null;
        try
        {
            app = builder.Build();
            if (identityLock is not null) _ = app.Services.GetRequiredService<IdentityLease>();
            app.UseServerRequestObservation(forwarded);
            if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<UserModeRequestGuardMiddleware>();
            app.UseRateLimiter();
            app.Use(async (http, next) => { http.Response.Headers.CacheControl = "no-store"; await next(); });
            app.MapServerHostEndpoints();
            app.MapAuthEndpoints(recoveryMode: true);
            app.MapServerModuleManagementEndpoints();
            // Recovery is reachable, but is never normal readiness or a successful candidate health check.
            foreach (var path in new[] { "/healthz", "/ready" })
                app.MapGet(path, (HttpContext http) => http.Connection.RemoteIpAddress is { } address && !System.Net.IPAddress.IsLoopback(address)
                    ? Results.NotFound() : Results.Json(new { status = "recovery-required" }, statusCode: 503)).AllowAnonymous();
            await app.InitializeServerStorageAsync(provider);
            return app;
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            identityLock?.Dispose();
            throw;
        }
    }
}
