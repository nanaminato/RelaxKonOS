using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Endpoints;

public static class WebServerEndpoints
{
    public static IEndpointRouteBuilder MapWebServerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(WebServerApiRoutes.WebServers).RequireAuthorization().WithTags("WebServers");
        group.MapPost(WebServerApiRoutes.DiscoverPattern, (RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) => manager.DiscoverAsync(ct));
        group.MapGet(WebServerApiRoutes.CollectionPattern, (RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) => manager.ListAsync(ct));
        group.MapGet(WebServerApiRoutes.ByIdPattern, async (string id, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
            (await manager.ListAsync(ct)).FirstOrDefault(server => server.Id == id) is { } item ? Results.Ok(item) : Results.NotFound());
        group.MapGet(WebServerApiRoutes.StatusPattern, async (string id, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
            await manager.GetStatusAsync(id, ct) is { } status ? Results.Ok(status) : Results.NotFound());
        group.MapPost(WebServerApiRoutes.TestConfigurationPattern, async (string id, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
            await manager.TestConfigurationAsync(id, ct) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapPost(WebServerApiRoutes.ManagedInstallPattern, async (string providerId, InstallManagedWebServerRequest request, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxInstall, providerId))
                return ElevationRequired("此 Nginx 安装操作需要当前会话对该提供程序的管理员授权。");
            return await StartAsync(context.Request, key => manager.InstallManagedAsync(providerId, key, request, Actor(context), ct));
        });
        group.MapPost(WebServerApiRoutes.ManagedPackagePattern, async (string providerId, HttpRequest request, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { problemCode = "webserver.package_multipart_required" });
            var form = await request.ReadFormAsync(ct);
            var package = form.Files.GetFile("package");
            if (package is null || package.Length == 0) return Results.BadRequest(new { problemCode = "webserver.package_required" });
            await using var content = package.OpenReadStream();
            return await manager.UploadManagedPackageAsync(providerId, package.FileName, content, ct) is { } uploaded
                ? Results.Ok(uploaded)
                : Results.NotFound();
        });
        group.MapGet(WebServerApiRoutes.ManagedVersionsPattern, async (string providerId, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
            await manager.GetManagedInstallCatalogAsync(providerId, ct) is { } catalog ? Results.Ok(catalog) : Results.NotFound());
        group.MapGet(WebServerApiRoutes.ManagedDownloadPattern, async (string providerId, string? version, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
            await manager.GetManagedInstallDownloadAsync(providerId, version, ct) is { } download ? Results.Ok(download) : Results.NotFound());
        group.MapPost(WebServerApiRoutes.IntegratePattern, async (string id, IntegrateWebServerRequest request, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxConfigurationWrite, id))
                return ElevationRequired("此 Nginx 配置操作需要当前会话对该实例的管理员授权。");
            return await StartAsync(context.Request, key => manager.IntegrateAsync(id, key, request, Actor(context), ct));
        });
        group.MapPost(WebServerApiRoutes.LifecyclePattern, async (string id, string action, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!Enum.TryParse<WebServerLifecycleAction>(action, ignoreCase: true, out var lifecycle))
                return Results.BadRequest(new { problemCode = "webserver.lifecycle_action_invalid" });
            var capability = lifecycle == WebServerLifecycleAction.EnableAcmeHttp01
                ? HostElevationCapability.NginxConfigurationWrite
                : HostElevationCapability.NginxLifecycle;
            if (!elevations.IsGranted(context.User, capability, id))
                return ElevationRequired("此 Nginx 操作需要当前会话对该实例的管理员授权。");
            return await StartAsync(context.Request, key => manager.ApplyLifecycleAsync(id, lifecycle, key, Actor(context), ct));
        });
        group.MapPost(WebServerApiRoutes.ManagedUninstallPattern, async (string id, UninstallManagedWebServerRequest request, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxInstall, id))
                return ElevationRequired("此 Nginx 卸载操作需要当前会话对该实例的管理员授权。");
            return await StartAsync(context.Request, key => manager.UninstallManagedAsync(id, key, request, Actor(context), ct));
        });
        group.MapPost(WebServerApiRoutes.ReloadPattern, async (string id, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxLifecycle, id))
                return ElevationRequired("此 Nginx reload 操作需要当前会话对该实例的管理员授权。");
            return await StartAsync(context.Request, key => manager.ReloadAsync(id, key, Actor(context), ct));
        });
        group.MapGet(WebServerApiRoutes.SitesPattern, async (string id, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
            await manager.ListSitesAsync(id, ct) is { } sites ? Results.Ok(sites) : Results.NotFound());
        group.MapPost(WebServerApiRoutes.SitesPattern, async (string id, UpsertWebServerSiteRequest request, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxConfigurationWrite, id))
                return ElevationRequired("此 Nginx 站点配置操作需要当前会话对该实例的管理员授权。");
            try
            {
                return await manager.UpsertSiteAsync(id, request, ct) is { } site
                    ? Results.Ok(site)
                    : Results.BadRequest(new { problemCode = "webserver.site_save_failed" });
            }
            catch (RelaxKonOS.Server.WebServer.NginxWebServerManager.WebServerSiteValidationException exception)
            {
                return Results.BadRequest(new { problemCode = exception.ProblemCode });
            }
            catch (RelaxKonOS.Server.WebServer.NginxWebServerManager.WebServerSiteConflictException exception)
            {
                return Results.Conflict(new { problemCode = exception.ProblemCode });
            }
            catch (RelaxKonOS.Server.WebServer.NginxWebServerManager.WebServerSiteApplyException exception)
            {
                return Results.BadRequest(new { problemCode = exception.ProblemCode });
            }
        });
        group.MapDelete(WebServerApiRoutes.SiteByIdPattern, async (string id, string siteId, HttpContext context,
            IHostElevationSessionStore elevations, RelaxKonOS.Server.WebServer.IWebServerManager manager, CancellationToken ct) =>
        {
            if (!elevations.IsGranted(context.User, HostElevationCapability.NginxConfigurationWrite, id))
                return ElevationRequired("此 Nginx 站点配置操作需要当前会话对该实例的管理员授权。");
            return await manager.DeleteSiteAsync(id, siteId, ct) switch { true => Results.NoContent(), false => Results.NotFound(), _ => Results.BadRequest(new { problemCode = "webserver.site_delete_failed" }) };
        });
        group.MapGet(WebServerApiRoutes.OperationsPattern, async (Guid operationId, RelaxKonOS.Server.WebServer.WebServerOperationStore operations, CancellationToken ct) =>
            await operations.GetAsync(operationId, ct) is { } operation ? Results.Ok(operation) : Results.NotFound());
        group.MapPost(WebServerApiRoutes.CancelOperationPattern, async (Guid operationId, RelaxKonOS.Server.WebServer.WebServerOperationStore operations, CancellationToken ct) =>
            await operations.CancelAsync(operationId, ct) is { } operation ? Results.Ok(operation) : Results.NotFound());
        return app;
    }

    private static async Task<IResult> StartAsync(HttpRequest request, Func<string, Task<WebServerOperationDto?>> start)
    {
        var key = request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            return Results.BadRequest(new { problemCode = "webserver.idempotency_key_required" });
        var operation = await start(key);
        if (operation is null) return Results.NotFound();
        if (operation.OperationId == Guid.Empty)
        {
            var status = operation.ProblemCode.EndsWith("elevation_required", StringComparison.Ordinal)
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status409Conflict;
            return Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["problemCode"] = operation.ProblemCode });
        }
        return Results.Accepted($"{WebServerApiRoutes.WebServers}/operations/{operation.OperationId}", operation);
    }

    private static string? Actor(HttpContext context) => context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name)?.Value
        ?? context.User.Identity?.Name;

    private static IResult ElevationRequired(string detail) => Results.Problem(statusCode: StatusCodes.Status403Forbidden,
        title: "需要管理员权限", detail: detail, type: "https://remoteos.app/problems/elevation-required");
}
