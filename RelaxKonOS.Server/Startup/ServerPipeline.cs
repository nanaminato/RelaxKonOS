using Microsoft.AspNetCore.HttpOverrides;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Observability;

namespace RelaxKonOS.Server.Startup;

internal static class ServerPipeline
{
    public static void UseServerRequestObservation(this WebApplication app, ForwardedHeadersOptions forwardedHeaders)
    {
        // Only configured reverse proxies can affect the source IP used by login protection.
        // With no KnownProxies, ForwardedHeadersMiddleware ignores X-Forwarded-For entirely.
        if (forwardedHeaders.KnownProxies.Count > 0 || forwardedHeaders.KnownIPNetworks.Count > 0)
            app.UseForwardedHeaders(forwardedHeaders);

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.Contains("/auth/", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.Headers.CacheControl = "no-store";
                var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 8192;
            }
            var language = context.Request.GetTypedHeaders().AcceptLanguage?.FirstOrDefault()?.Value.Value;
            if (!string.IsNullOrWhiteSpace(language))
                context.Response.Headers.ContentLanguage = language;
            await next();
        });
        app.UseMiddleware<RequestObservationMiddleware>();
    }

    public static void MapServerApplication(this WebApplication app, RelaxKonOS.Server.EventAlerts.EventAlertsOptions eventAlertsOptions)
    {
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
        app.UseMiddleware<UserModeRequestGuardMiddleware>();
        app.UseRateLimiter();
        app.MapHealthEndpoints();
        app.MapServerHostEndpoints();
        app.MapAuthEndpoints();
        app.MapAliasEndpoints();
        app.MapFileEndpoints();
        app.MapPrivilegedEndpoints();
        app.MapAppCapabilityEndpoints();
        app.MapAppSettingsEndpoints();
        app.MapRegistryEndpoints();
        app.MapServerRuntimeEndpoints();
        app.MapImageMirrorEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapHostSettingsEndpoints();
        app.MapBrowserEndpoints();
        app.MapSystemMonitorEndpoints();
        app.MapDockerEndpoints();
        app.MapDockerProxyEndpoints();
        app.MapProcessGuardianEndpoints();
        app.MapWebServerEndpoints();
        app.MapWebsitePublicationEndpoints();
        app.MapFileServiceEndpoints();
        app.MapCertificateEndpoints();
        app.MapGitEndpoints();
        app.MapGitBuildEndpoints();
        app.MapInstallationEndpoints();
        app.MapApplicationDeploymentEndpoints();
        app.MapBackupRecoveryEndpoints();
        app.MapTunnelEndpoints();
        if (eventAlertsOptions.Enabled) app.MapEventAlertEndpoints();
        app.MapProxyEndpoints();
        app.MapGatewayProxyEndpoints();
        app.MapProxyServerEndpoints();
        app.MapRuntimeCatalogEndpoints();
        app.MapComponentEndpoints();
        app.MapTaskPlanningEndpoints();
        app.MapTaskExecutionEndpoints();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            app.MapFirewallEndpoints();
        app.Services.GetRequiredService<ServerModules>().MapEndpoints(app);
        app.MapHub<GuardianLogsHub>(RelaxKonOSEndpoints.GuardianLogsHubPath, options => options.CloseOnAuthenticationExpiration = true);
        app.MapHub<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentLogsHub>(RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath,
            options => options.CloseOnAuthenticationExpiration = true);
        app.MapHub<PerformanceHub>(RelaxKonOSEndpoints.PerformanceHubPath, options => options.CloseOnAuthenticationExpiration = true);
        app.MapHub<SettingsChangesHub>(RelaxKonOSEndpoints.SettingsChangesHubPath, options => options.CloseOnAuthenticationExpiration = true);
        if (eventAlertsOptions.Enabled)
            app.MapHub<RelaxKonOS.Server.Hubs.EventAlertsHub>("/hubs/event-alerts", options => options.CloseOnAuthenticationExpiration = true);
    }
}
