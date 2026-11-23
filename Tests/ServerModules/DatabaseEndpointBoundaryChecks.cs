using Microsoft.Extensions.Logging;
using RelaxKonOS.ModuleHost.Sdk;
using System.Net;
using RelaxKonOS.ServerModuleHost;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ServerModule.Database;
using RelaxKonOS.ServerModule.DatabaseObservation;
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
    public static async Task CheckDisabledModuleDoesNotLoadExecutionAsync()
    {
        var root = Directory.CreateTempSubdirectory("rk-disabled-database-");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName });
            var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory")
                with { Platform = HostPlatformKind.Linux };
            using var services = new ServiceCollection()
                .AddSingleton<IPrivilegedOperationTransport, IdleDatabaseHelper>().BuildServiceProvider();
            var issues = await new DatabaseModule().InspectOrphanedWorkAsync(services, context, default);
            if (issues.Count != 0 || AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetName().Name == "RelaxKonOS.ServerModule.Database"))
                throw new InvalidOperationException("A disabled, idle database module must not load its execution assembly.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

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

        var packageRecord = new InstalledModuleRecord(ServerModules.DatabaseId, "0.1.0", "database-fixture", "test", "test",
            "", "", "", "", "", [], [], "module-host", DateTimeOffset.UtcNow);
        var packageRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var packagedDatabase = new InstalledIsolatedModule(packageRecord, packageRoot);
        if (await packagedDatabase.CheckHealthAsync(true, default) is not
                { Status: "blocked", ProblemCode: "module-host-code-trust-required" }
            || await packagedDatabase.CheckHealthAsync(false, default) is not { Status: "disabled", ProblemCode: null })
            throw new InvalidOperationException("The database package must distinguish missing code trust from module disablement.");
        var packagedEnabled = ServerModules.Create(new ConfigurationBuilder().Build(), [packagedDatabase]);
        var packagedDisabled = ServerModules.Create(config, [packagedDatabase]);
        if (!packagedEnabled.IsEnabled(ServerModules.DatabaseId)
            || !packagedEnabled.Capabilities.Contains(ServerCapabilities.Database)
            || packagedDisabled.IsEnabled(ServerModules.DatabaseId)
            || packagedDisabled.Capabilities.Contains(ServerCapabilities.Database))
            throw new InvalidOperationException("An installed database ModuleHost must share the database module switch and capability.");

        var packagedBuilder = WebApplication.CreateBuilder();
        // Exercise isolated registration without supplying an executable package to this endpoint fixture.
        new InstalledIsolatedModule(packageRecord with { Quarantined = true }, packageRoot)
            .ConfigureServices(packagedBuilder, context);
        if (packagedBuilder.Services.Any(service =>
            service.ServiceType == typeof(RelaxKonOS.ServerModule.Database.Contracts.IDatabaseQueries)
            || service.ServiceType == typeof(PostgreSqlBackupScheduler)))
            throw new InvalidOperationException("An isolated database module must not register bundled queries or the Core backup scheduler.");
        packagedBuilder.Services.AddSingleton<IHostElevationSessionStore>(_ => throw new NotSupportedException());
        await using (var packagedApp = packagedBuilder.Build())
        {
            packagedDatabase.MapEndpoints(packagedApp, context);
            var packagedRoutes = ((IEndpointRouteBuilder)packagedApp).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.RoutePattern.RawText!);
            if (packagedRoutes.Count != endpoints.Count || endpoints.Keys.Any(path => !packagedRoutes.ContainsKey(path))
                || packagedRoutes.Values.Count(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy
                    == RuntimeCatalogEndpoints.ReadPolicy) != 4)
                throw new InvalidOperationException("The packaged database must keep its four authorized reads and two credential routes.");
        }

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
        using var relayManagement = new ModuleManagementStore(brokerBuilder.Configuration, inspectionRoot);
        brokerBuilder.Services.AddSingleton(new ModuleHostSupervisor(brokerBuilder.Environment, brokerBuilder.Configuration,
            NullLogger<ModuleHostSupervisor>.Instance, null!));
        brokerBuilder.Services.AddSingleton(services => new CoreModulePrivilegeBroker(brokerBuilder.Environment,
            brokerBuilder.Configuration, packagedEnabled, relayManagement, services));
        await using var broker = brokerBuilder.Build();
        DatabaseObservationBroker.Map(broker, broker.Services, (_, _) =>
            throw new InvalidOperationException("An unauthenticated request cannot resolve an observation user."));
        var privateRoutes = ((IEndpointRouteBuilder)broker).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        if (privateRoutes.Length != 6 || !privateRoutes.Select(endpoint =>
            (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single(), endpoint.RoutePattern.RawText))
            .ToHashSet().SetEquals(new[]
            {
                ("GET", DatabaseObservationProtocol.PostgreSqlState), ("GET", DatabaseObservationProtocol.PostgreSqlMaintenancePlan),
                ("GET", DatabaseObservationProtocol.PostgreSqlMigrationPlan), ("GET", DatabaseObservationProtocol.MySqlState),
                ("GET", DatabaseObservationProtocol.PostgreSqlBackupSlot), ("POST", DatabaseObservationProtocol.PostgreSqlBackupSlot)
            }))
            throw new InvalidOperationException("The private database broker must expose only fixed observation and backup slot routes.");
        foreach (var route in privateRoutes)
        {
            var request = new DefaultHttpContext { RequestServices = broker.Services };
            request.Request.Method = route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            if (request.Request.Method == "POST")
                request.Request.QueryString = new($"?instanceId={Guid.NewGuid():D}&scheduleId={Guid.NewGuid():D}&dueAt=2026-11-24T00:00:00Z");
            await route.RequestDelegate!(request);
            if (request.Response.StatusCode != StatusCodes.Status403Forbidden)
                throw new InvalidOperationException("A request without a trusted module generation reached the observation broker.");
        }

        broker.MapDatabaseObservationRelay();
        var publicReads = ((IEndpointRouteBuilder)broker).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(route => route.Metadata.GetMetadata<IAuthorizeData>()?.Policy
                == RuntimeCatalogEndpoints.ReadPolicy).ToArray();
        if (publicReads.Length != 4)
            throw new InvalidOperationException("The public database relay did not register all four reads.");
        foreach (var route in publicReads)
        {
            var request = new DefaultHttpContext { RequestServices = broker.Services };
            request.Request.Method = "GET";
            await route.RequestDelegate!(request);
            if (request.Response.StatusCode != StatusCodes.Status503ServiceUnavailable)
                throw new InvalidOperationException("A database relay without a running ModuleHost must return 503.");
        }

        await CheckObservationClientAsync();
        await CheckModuleEntryAsync();
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

    private static async Task CheckModuleEntryAsync()
    {
        var entryAssembly = typeof(DatabaseModuleHostEntry).Assembly;
        var executionAssembly = typeof(DatabaseQueries).Assembly;
        if (entryAssembly == executionAssembly || entryAssembly.GetReferencedAssemblies()
                .Any(reference => reference.Name == executionAssembly.GetName().Name))
            throw new InvalidOperationException("The isolated observation entry must not include or reference database execution.");

        var observation = new EntryObservationFixture();
        var entry = new DatabaseModuleHostEntry();
        var schedule = new EntryScheduleFixture();
        var context = new EntryContext(entry.Id, observation, schedule);
        await entry.StartAsync(context, default);
        if (await schedule.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)) != schedule.Slot)
            throw new InvalidOperationException("The isolated backup worker must submit the observed slot.");
        await using var app = WebApplication.CreateBuilder().Build();
        entry.MapEndpoints(app, context);
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.RoutePattern.RawText!);
        if (routes.Count != 4 || routes.Values.Any(endpoint =>
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() != "GET"))
            throw new InvalidOperationException("The database ModuleHost entry must expose exactly four read routes.");

        var instance = Guid.NewGuid();
        foreach (var (path, kind) in new[]
        {
            (PostgreSqlRoutes.State, DatabaseObservationKind.PostgreSqlState),
            (PostgreSqlRoutes.MaintenancePlan, DatabaseObservationKind.PostgreSqlMaintenancePlan),
            (PostgreSqlRoutes.MigrationPlan, DatabaseObservationKind.PostgreSqlMigrationPlan),
            (MySqlRoutes.State, DatabaseObservationKind.MySqlState)
        })
        {
            Guid? selected = kind == DatabaseObservationKind.PostgreSqlState ? instance : null;
            using var body = new MemoryStream();
            var http = new DefaultHttpContext { RequestServices = app.Services };
            http.Request.Method = "GET";
            if (selected is not null)
                http.Request.QueryString = new QueryString("?instance=" + selected.Value.ToString("D"));
            http.Response.Body = body;
            await routes[path].RequestDelegate!(http);
            var expectedProblem = kind == DatabaseObservationKind.PostgreSqlMigrationPlan
                ? """{"code":"database.migration_version_unsupported"}""" : "{}";
            if (observation.Last != (kind, selected) || http.Response.StatusCode != 503
                || http.Response.ContentType != "application/problem+json"
                || System.Text.Encoding.UTF8.GetString(body.ToArray()) != expectedProblem)
                throw new InvalidOperationException("The ModuleHost entry did not preserve the observation request and broker response.");
        }
        var operation = new InstallationOperationDto(Guid.NewGuid(), InstallationServiceId.PostgreSqlBackupAutomation,
            InstallationOperationKind.Configure, default, default, null, null, DateTimeOffset.UtcNow, null, null, false);
        var state = new PostgreSqlState(true, true, true, "16", 1, instance, 5432, true, true, null, null, null);
        observation.Snapshot = new(state, null, operation, operation with { OperationId = Guid.NewGuid() });
        using var successBody = new MemoryStream();
        var success = new DefaultHttpContext { RequestServices = app.Services };
        success.Request.Method = "GET";
        success.Response.Body = successBody;
        await routes[PostgreSqlRoutes.State].RequestDelegate!(success);
        var projected = JsonSerializer.Deserialize<PostgreSqlState>(successBody.ToArray(), RelaxKonOSJsonOptions.Default);
        if (success.Response.StatusCode != 200 || projected?.InstanceId != instance || projected.ScheduledOperation != operation)
            throw new InvalidOperationException("The isolated entry must project the current backup slot into public PostgreSQL state.");
        await entry.StopAsync(default);

        var stoppingSchedule = new EntryScheduleFixture { BlockSubmission = true };
        var stoppingEntry = new DatabaseModuleHostEntry();
        await stoppingEntry.StartAsync(new EntryContext(stoppingEntry.Id, observation, stoppingSchedule), default);
        await stoppingSchedule.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stoppingEntry.StopAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (!stoppingSchedule.SubmissionStopped.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException("Stopping the module must cancel and drain an in-flight backup submission.");
        await stoppingEntry.StopAsync(default);
    }

    private sealed record EntryContext(string ModuleId, IModuleDatabaseObservation DatabaseObservation,
        IModuleDatabaseBackupSchedule DatabaseBackupSchedule) : IModuleHostDatabaseContext
    {
        public string ModuleDataPath => "";
        public string HostApiVersion => ModuleHostProtocol.HostApiVersion;
        public IModuleHostStorage Storage => throw new NotSupportedException();
        public ILogger Logger => NullLogger.Instance;
        public IModulePrivilegeBroker Privileges => throw new NotSupportedException();
        public ICloudFileProviders CloudProviders => throw new NotSupportedException();
    }

    private sealed class EntryScheduleFixture : IModuleDatabaseBackupSchedule
    {
        public PostgreSqlBackupSlot Slot { get; } = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        public TaskCompletionSource<PostgreSqlBackupSlot> Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockSubmission { get; init; }
        public TaskCompletionSource SubmissionStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PostgreSqlBackupSlot?> ReadDueSlotAsync(CancellationToken cancellationToken) => new(Slot);

        public async ValueTask SubmitAsync(PostgreSqlBackupSlot slot, CancellationToken cancellationToken)
        {
            Submitted.SetResult(slot);
            if (!BlockSubmission) return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { SubmissionStopped.SetResult(); }
        }
    }

    private sealed class EntryObservationFixture : IModuleDatabaseObservation
    {
        public (DatabaseObservationKind, Guid?) Last { get; private set; }
        public PostgreSqlObservationSnapshot? Snapshot { get; set; }

        public ValueTask<HttpResponseMessage> ReadAsync(DatabaseObservationKind kind, Guid? instance,
            CancellationToken cancellationToken)
        {
            Last = (kind, instance);
            if (Snapshot is { } snapshot)
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(snapshot, options: RelaxKonOSJsonOptions.Default),
                });
            var content = new ByteArrayContent((kind == DatabaseObservationKind.PostgreSqlMigrationPlan
                ? """{"code":"database.migration_version_unsupported"}"""u8 : "{}"u8).ToArray());
            content.Headers.ContentType = new("application/problem+json");
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content });
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

        var slot = new PostgreSqlBackupSlot(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        handler.Slot = slot;
        if (await client.ReadDueSlotAsync(default) != slot ||
            handler.Requests.Last() != (HttpMethod.Get, DatabaseObservationProtocol.PostgreSqlBackupSlot, moduleToken, ""))
            throw new InvalidOperationException("The background client must read the due slot without a user request token.");
        await client.SubmitAsync(slot, default);
        if (handler.Requests.Last() != (HttpMethod.Post, DatabaseObservationProtocol.PostgreSqlBackupSlot
            + $"?instanceId={slot.InstanceId:D}&scheduleId={slot.ScheduleId:D}&dueAt=1970-01-01T00:00:00.0000000Z", moduleToken, ""))
            throw new InvalidOperationException("The background client must submit exactly the observed slot identity.");
        handler.Slot = null;
        if (await client.ReadDueSlotAsync(default) is not null)
            throw new InvalidOperationException("No Content means there is no due slot.");

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
        internal PostgreSqlBackupSlot? Slot { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.GetValues(ModuleHostProtocol.ServiceTokenHeader).Single(),
                request.Headers.TryGetValues(ModulePrivilegeProtocol.RequestContextHeader, out var context) ? context.Single() : ""));
            if (request.RequestUri.AbsolutePath == DatabaseObservationProtocol.PostgreSqlBackupSlot)
                return Task.FromResult(request.Method == HttpMethod.Get && Slot is { } slot
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(slot)) }
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
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
