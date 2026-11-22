using Microsoft.AspNetCore.Mvc;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Endpoints;

internal static class DatabaseObservationRelay
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    });

    internal static void MapDatabaseObservationRelay(this IEndpointRouteBuilder endpoints)
    {
        foreach (var route in new[]
        {
            PostgreSqlRoutes.State, PostgreSqlRoutes.MaintenancePlan, PostgreSqlRoutes.MigrationPlan, MySqlRoutes.State
        })
        {
            endpoints.MapGet(route, (HttpContext http, [FromServices] ModuleHostSupervisor hosts,
                [FromServices] CoreModulePrivilegeBroker broker) =>
                ForwardAsync(http, hosts, broker, route)).RequireAuthorization(RuntimeCatalogEndpoints.ReadPolicy);
        }
    }

    private static async Task ForwardAsync(HttpContext http, ModuleHostSupervisor hosts,
        CoreModulePrivilegeBroker broker, string route)
    {
        const string moduleId = DatabaseObservationProtocol.ModuleId;
        using var connection = hosts.AcquireConnection(moduleId, trustedProvider: true);
        if (connection is null)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        if (!hosts.CanAccessDatabaseObservation(http.User))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, connection.Stopping);
        string? requestContext = null;
        try
        {
            requestContext = broker.IssueRequestContext(moduleId, connection.GenerationId, http.User);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(connection.Endpoint, route + http.Request.QueryString));
            request.Headers.TryAddWithoutValidation(ModuleHostProtocol.ServiceTokenHeader, connection.ServiceToken);
            request.Headers.TryAddWithoutValidation(ModulePrivilegeProtocol.RequestContextHeader, requestContext);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping.Token);
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(http.Response.Body, stopping.Token);
        }
        catch (ModulePrivilegeException error)
        {
            http.Response.StatusCode = error.Status;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested) http.Abort();
            else
            {
                http.Response.Clear();
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        }
        finally
        {
            if (requestContext is not null) broker.ReleaseRequestContext(requestContext);
        }
    }
}
