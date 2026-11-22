using System.Net;
using RelaxKonOS.ServerModuleHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ServerModule.Database;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Startup.Modules;
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
        var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory");
        var module = new DatabaseModule();
        module.ConfigureServices(builder, context);
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IHostElevationSessionStore>(_ => throw new NotSupportedException());
        var services = builder.Services;
        if (services.Count(x => x.ServiceType == typeof(IInstallationService)) != 11
            || services.Count(x => x.ServiceType == typeof(PostgreSqlCredentialStore)) != 1
            || services.Count(x => x.ServiceType == typeof(PostgreSqlBackupScheduler)) != 1
            || services.Count(x => x.ServiceType == typeof(IComponentLifecycleAdapter)) != 1)
            throw new InvalidOperationException("The database registration must own the installation adapters, credential store and scheduler.");

        await using var app = builder.Build();
        module.MapEndpoints(app, context);
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

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Modules:Database:Enabled"] = "false"
        }).Build();
        static ServerDescriptorDto Describe(ServerModules modules) => RelaxKonOS.Server.HostMode.ServerDescriptorFactory.Create(
            HostPlatformKind.Windows,
            new RelaxKonOS.Server.HostMode.ServerModeResolver(new ConfigurationBuilder().Build(), RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper),
            new RelaxKonOS.Server.EventAlerts.EventAlertsOptions(),
            new RelaxKonOS.Server.BackupRecovery.ConfigurationBackupRecoveryKeyProvider(new RelaxKonOS.Server.BackupRecovery.BackupRecoveryOptions()),
            modules.Capabilities);
        var disabled = ServerModules.Create(config);
        if (disabled.IsEnabled(ServerModules.DatabaseId) || disabled.Capabilities.Contains(ServerCapabilities.Database)
            || Describe(disabled).Capabilities.Contains(ServerCapabilities.Database)
            || !Describe(ServerModules.Create(new ConfigurationBuilder().Build())).Capabilities.Contains(ServerCapabilities.Database))
            throw new InvalidOperationException("The database module switch must control both availability and advertised capability.");

        using var native = new ServiceCollection().AddSingleton<IPrivilegedOperationTransport, IdleDatabaseHelper>().BuildServiceProvider();
        var helper = (IdleDatabaseHelper)native.GetRequiredService<IPrivilegedOperationTransport>();
        var queryHost = new DatabaseQueryHost(helper);
        var instanceId = Guid.NewGuid();
        if ((await queryHost.ReadPostgreSqlAsync(instanceId, default)).InstanceId != instanceId)
            throw new InvalidOperationException("PostgreSQL read requests must retain the selected instance.");
        await queryHost.ReadPostgreSqlMaintenancePlanAsync(default);
        await queryHost.ReadPostgreSqlMigrationPlanAsync(default);
        await queryHost.ReadMySqlAsync(default);
        if (!helper.ReadRequests.Select(request => request.Operation).SequenceEqual(new[]
            {
                PrivilegedOperationKind.PostgreSqlQuery, PrivilegedOperationKind.PostgreSqlMaintenancePlan,
                PrivilegedOperationKind.PostgreSqlMigrationPlan, PrivilegedOperationKind.MySqlQuery
            }))
            throw new InvalidOperationException("Database reads must use their distinct Helper query kinds.");

        var inspectionRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "database-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inspectionRoot);
        var inspectionBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = inspectionRoot });
        var linux = ServerModuleContext.Create(inspectionBuilder.Configuration, inspectionBuilder.Environment, "memory")
            with { Platform = HostPlatformKind.Linux };
        if ((await module.InspectOrphanedWorkAsync(native, linux, default)).Count != 0)
            throw new InvalidOperationException("A verified idle database host must permit deactivation.");
        helper.Pending = Guid.NewGuid();
        if (!(await module.InspectOrphanedWorkAsync(native, linux, default)).Any(work => work.Description.Contains(helper.Pending.ToString()!)))
            throw new InvalidOperationException("Native ownership must block database deactivation.");

        helper.Pending = null;
        helper.ScheduledInstance = Guid.NewGuid();
        if (!(await module.InspectOrphanedWorkAsync(native, linux, default)).Any(work =>
                work.Description.Contains(helper.ScheduledInstance.ToString()!) && work.Description.Contains("backup schedule")))
            throw new InvalidOperationException("A schedule on a secondary PostgreSQL instance must block deactivation.");
        helper.ScheduledInstance = null;

        using var unavailable = new ServiceCollection().BuildServiceProvider();
        if (!(await module.InspectOrphanedWorkAsync(unavailable, linux, default)).Any(work =>
                work.Description.Contains("Native database responsibility could not be established")))
            throw new InvalidOperationException("Missing native evidence must block database deactivation.");

        var ledger = new InstallationOperationStore(inspectionBuilder.Environment);
        var queued = ledger.Create(InstallationServiceId.PostgreSql, InstallationOperationKind.Configure,
            "database-boundary-test", "queued-fixture", InstallationOperationStore.Reference("fixture"), ["fixture"], out _);
        if (!(await module.InspectOrphanedWorkAsync(native, linux, default)).Any(work => work.Description.Contains(queued.Operation.OperationId.ToString())))
            throw new InvalidOperationException("A queued database installation must block deactivation.");

        var ledgerPath = Path.Combine(inspectionRoot, "data", "installation-operations.json");
        await File.WriteAllTextAsync(ledgerPath, "invalid ledger");
        if (!(await module.InspectOrphanedWorkAsync(native, linux, default)).Any(work =>
                work.Description.Contains("installation ledger cannot be read")))
            throw new InvalidOperationException("A damaged installation ledger must block database deactivation.");

        var brokerBuilder = WebApplication.CreateBuilder();
        brokerBuilder.Services.AddSingleton(new ModuleHostSupervisor(brokerBuilder.Environment, brokerBuilder.Configuration,
            NullLogger<ModuleHostSupervisor>.Instance, null!));
        await using var broker = brokerBuilder.Build();
        DatabaseObservationBroker.Map(broker, broker.Services, (_, _) =>
            throw new InvalidOperationException("An unauthenticated request cannot resolve an observation user."));
        var privateRoutes = ((IEndpointRouteBuilder)broker).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        if (!privateRoutes.Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet().SetEquals(new[]
            {
                DatabaseObservationProtocol.PostgreSqlState, DatabaseObservationProtocol.PostgreSqlMaintenancePlan,
                DatabaseObservationProtocol.PostgreSqlMigrationPlan, DatabaseObservationProtocol.MySqlState
            }) || privateRoutes.Any(route => route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() != "GET"))
            throw new InvalidOperationException("The private database broker must expose only the four fixed read routes.");
        foreach (var route in privateRoutes)
        {
            var request = new DefaultHttpContext { RequestServices = broker.Services };
            request.Request.Method = "GET";
            await route.RequestDelegate!(request);
            if (request.Response.StatusCode != StatusCodes.Status403Forbidden)
                throw new InvalidOperationException("A request without a trusted module generation reached the observation broker.");
        }

        await CheckObservationClientAsync();
        Console.WriteLine("Database registration, endpoint policies, deactivation and private observation admission checks passed.");

        void Check(string route, string method, string? policy)
        {
            if (!endpoints.TryGetValue(route, out var endpoint)
                || endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() != method
                || endpoint.Metadata.GetMetadata<IAuthorizeData>() is not { } authorization
                || authorization.Policy != policy)
                throw new InvalidOperationException($"The database route {method} {route} did not retain its authorization metadata.");
        }
    }

    private static async Task CheckObservationClientAsync()
    {
        var moduleToken = new string('a', 64);
        var requestToken = new string('b', 64);
        var accessor = new HttpContextAccessor();
        var handler = new DatabaseObservationTransportFixture();
        using var transport = new HttpClient(handler);
        var client = new DatabaseObservationClient(DatabaseObservationProtocol.ModuleId, "http://127.0.0.1:65000/",
            moduleToken, accessor, transport);

        await RejectAsync(() => client.ReadAsync(DatabaseObservationKind.MySqlState, null, default), HttpStatusCode.Forbidden);
        var incoming = new DefaultHttpContext();
        accessor.HttpContext = incoming;
        try
        {
            incoming.Request.Headers[ModulePrivilegeProtocol.RequestContextHeader] = requestToken;
            var outsider = new DatabaseObservationClient("other-module", "http://127.0.0.1:65000/", moduleToken, accessor, transport);
            await RejectAsync(() => outsider.ReadAsync(DatabaseObservationKind.MySqlState, null, default), HttpStatusCode.Forbidden);
            var external = new DatabaseObservationClient(DatabaseObservationProtocol.ModuleId, "http://example.invalid/",
                moduleToken, accessor, transport);
            await RejectAsync(() => external.ReadAsync(DatabaseObservationKind.MySqlState, null, default), HttpStatusCode.ServiceUnavailable);
            if (handler.Requests.Count != 0)
                throw new InvalidOperationException("Rejected observation clients must not call the broker.");

            var instance = Guid.NewGuid();
            foreach (var (kind, path, selected) in new (DatabaseObservationKind, string, Guid?)[]
            {
                (DatabaseObservationKind.PostgreSqlState, DatabaseObservationProtocol.PostgreSqlState + "?instance=" + instance.ToString("D"), instance),
                (DatabaseObservationKind.PostgreSqlMaintenancePlan, DatabaseObservationProtocol.PostgreSqlMaintenancePlan, null),
                (DatabaseObservationKind.PostgreSqlMigrationPlan, DatabaseObservationProtocol.PostgreSqlMigrationPlan, null),
                (DatabaseObservationKind.MySqlState, DatabaseObservationProtocol.MySqlState, null)
            })
            {
                using var response = await client.ReadAsync(kind, selected, default);
                if (response.StatusCode != HttpStatusCode.OK || handler.Requests.Last() != (HttpMethod.Get, path, moduleToken, requestToken))
                    throw new InvalidOperationException("Observation requests must preserve the fixed path and both authorization headers.");
            }
        }
        finally { accessor.HttpContext = null; }

        static async Task RejectAsync(Func<ValueTask<HttpResponseMessage>> read, HttpStatusCode status)
        {
            try { using var response = await read(); }
            catch (HttpRequestException error) when (error.StatusCode == status) { return; }
            throw new InvalidOperationException($"Expected observation client rejection {status}.");
        }
    }

    private sealed class DatabaseObservationTransportFixture : HttpMessageHandler
    {
        internal List<(HttpMethod, string, string, string)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.GetValues(ModuleHostProtocol.ServiceTokenHeader).Single(),
                request.Headers.GetValues(ModulePrivilegeProtocol.RequestContextHeader).Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

}

internal sealed class IdleDatabaseHelper : IPrivilegedOperationTransport
{
    internal Guid? Pending { get; set; }
    internal List<PrivilegedOperationRequest> ReadRequests { get; } = [];
    internal Guid? ScheduledInstance { get; set; }

    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        ReadRequests.Add(request);
        object state = request.Operation switch
        {
            PrivilegedOperationKind.PostgreSqlQuery when request.PostgreSqlInstance is { } instance => new PostgreSqlState(
                true, true, true, "16", 1, instance, 5433, false, false, null, null, null,
                BackupSchedule: new(Guid.NewGuid(), true, 24, 3, false, "16", DateTimeOffset.UtcNow)),
            PrivilegedOperationKind.PostgreSqlQuery or PrivilegedOperationKind.PostgreSqlMaintenancePlan
                or PrivilegedOperationKind.PostgreSqlMigrationPlan => new PostgreSqlState(true, false, false, null, 0,
                null, 5432, false, false, null, null, null,
                Instances: ScheduledInstance is { } secondary ? [new(secondary, 5433)] : []),
            PrivilegedOperationKind.MySqlQuery => new MySqlState(true, 0, false, null,
                false, false, Pending, null, null),
            _ => throw new NotSupportedException(request.Operation.ToString())
        };
        return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state))));
    }
}
