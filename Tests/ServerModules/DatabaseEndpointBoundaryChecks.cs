using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Databases;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;

internal static class DatabaseEndpointBoundaryChecks
{
    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDatabaseServices();
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IHostElevationSessionStore>(_ => throw new NotSupportedException());
        var services = builder.Services;
        if (services.Count(x => x.ServiceType == typeof(IInstallationService)) != 11
            || services.Count(x => x.ServiceType == typeof(PostgreSqlCredentialStore)) != 1
            || services.Count(x => x.ServiceType == typeof(PostgreSqlBackupScheduler)) != 1
            || services.Count(x => x.ServiceType == typeof(IComponentLifecycleAdapter)) != 1)
            throw new InvalidOperationException("The database registration must own the installation adapters, credential store and scheduler.");

        await using var app = builder.Build();
        app.MapDatabaseEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        Check(PostgreSqlRoutes.State, "GET", RuntimeCatalogEndpoints.ReadPolicy);
        Check(PostgreSqlRoutes.MaintenancePlan, "GET", RuntimeCatalogEndpoints.ReadPolicy);
        Check(PostgreSqlRoutes.MigrationPlan, "GET", RuntimeCatalogEndpoints.ReadPolicy);
        Check(MySqlRoutes.State, "GET", RuntimeCatalogEndpoints.ReadPolicy);
        Check(PostgreSqlRoutes.Credentials, "POST", null);
        Check(MySqlRoutes.Credentials, "POST", null);
        if (endpoints.Count != 6)
            throw new InvalidOperationException("Unexpected routes were added to the database endpoint surface.");

        Console.WriteLine("Database registration, endpoint paths, methods and authorization policies passed.");

        void Check(string route, string method, string? policy)
        {
            if (!endpoints.TryGetValue(route, out var endpoint)
                || endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() != method
                || endpoint.Metadata.GetMetadata<IAuthorizeData>() is not { } authorization
                || authorization.Policy != policy)
                throw new InvalidOperationException($"The database route {method} {route} did not retain its authorization metadata.");
        }
    }
}
