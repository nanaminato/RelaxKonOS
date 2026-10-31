using Microsoft.AspNetCore.HttpOverrides;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Observability;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

internal static class ServerPipeline
{
    /// <summary>
    /// The single place a failure becomes an HTTP response. A module that could not be woken answers
    /// 503 with a retry hint; any domain exception implementing <see cref="IProblemException"/> answers
    /// the shared problem shape. Endpoints therefore never translate those exceptions themselves.
    /// </summary>
    public static void UseServerProblemMapping(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (ModuleUnavailableException error) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = error.RetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = error.Code,
                    Type = "https://relaxkonos.app/problems/" + error.Code,
                    Detail = error.Message,
                    Extensions =
                    {
                        ["problemCode"] = error.Code,
                        ["retryAfterSeconds"] = error.RetryAfterSeconds,
                    },
                });
            }
            catch (Exception error) when (error is IProblemException && !context.Response.HasStarted)
            {
                // Endpoints that return validation problems use ProblemMapping.Create; this branch is
                // for failures raised inside a service, including one reached from a module runtime.
                await ProblemMapping.WriteAsync(context, (IProblemException)error);
            }
        });
    }

    public static void UseServerRequestObservation(this WebApplication app, ForwardedHeadersOptions forwardedHeaders)
    {
        app.UseServerProblemMapping();

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

    public static void MapServerApplication(this WebApplication app, RelaxKonOS.Server.EventAlerts.EventAlertsOptions eventAlertsOptions,
        ServerModuleContext moduleContext)
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
        app.MapHostNetworkEndpoints();
        app.MapProcessGuardianEndpoints();
        app.MapFileServiceEndpoints();
        app.MapInstallationEndpoints();
        app.MapApplicationDeploymentEndpoints();
        app.MapBackupRecoveryEndpoints();
        app.MapTunnelEndpoints();
        if (eventAlertsOptions.Enabled) app.MapEventAlertEndpoints();
        app.MapRuntimeCatalogEndpoints();
        app.MapComponentEndpoints();
        app.MapServerModuleManagementEndpoints();
        app.MapModulePrivilegeEndpoints();
        app.MapTaskPlanningEndpoints();
        app.MapTaskExecutionEndpoints();
        // Module-owned routes, HostedServices and Hubs come from the same frozen startup snapshot;
        // a disabled module leaves nothing behind here.
        app.Services.GetRequiredService<ServerModules>().MapEndpoints(app, moduleContext);
        app.MapHub<GuardianLogsHub>(RelaxKonOSEndpoints.GuardianLogsHubPath, options => options.CloseOnAuthenticationExpiration = true);
        app.MapHub<RelaxKonOS.Server.ApplicationDeployments.ApplicationDeploymentLogsHub>(RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath,
            options => options.CloseOnAuthenticationExpiration = true);
        app.MapHub<SettingsChangesHub>(RelaxKonOSEndpoints.SettingsChangesHubPath, options => options.CloseOnAuthenticationExpiration = true);
        if (eventAlertsOptions.Enabled)
            app.MapHub<RelaxKonOS.Server.Hubs.EventAlertsHub>("/hubs/event-alerts", options => options.CloseOnAuthenticationExpiration = true);
    }
}
