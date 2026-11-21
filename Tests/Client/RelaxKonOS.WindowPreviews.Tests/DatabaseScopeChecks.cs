using System.Net;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Workspace;

internal static class DatabaseScopeChecks
{
    public static async Task RunAsync()
    {
        var id = new AppId("relaxkonos.database");
        var permissions = new CertificateCapabilityChecks.Permission(id.Value);
        var session = DispatchProxy.Create<IAuthSession, TailscaleScopeAuthFixture>();
        var auth = (TailscaleScopeAuthFixture)(object)session;
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "database-sdk-memory.json"));
        var transport = new Transport();
        var broker = new Broker();
        using var lifetime = new CancellationTokenSource();
        var scoped = new ServerDatabaseCapability(id, permissions, new ExternalServerConnection(memory.Capture(session)),
            () => transport, lifetime.Token, () => transport, () => transport, broker);
        IDatabaseInstallationOperations installation = scoped;
        object options = new { };
        IServerMySql mysql = scoped;
        var request = new PostgreSqlCredentialRequest(Guid.NewGuid(), Guid.NewGuid(), "test-only");
        await RejectAsync<UnauthorizedAccessException>(() => scoped.ReadAsync(default));
        await RejectAsync<UnauthorizedAccessException>(() => mysql.ReadAsync(default));

        permissions.Granted.Add(AppPermissions.ServerDatabasesRead);
        await scoped.ReadAsync(default, request.InstanceId);
        await mysql.ReadAsync(default);
        await RejectAsync<UnauthorizedAccessException>(() => scoped.StageCredentialAsync(request, default));
        await RejectAsync<UnauthorizedAccessException>(() => mysql.StageCredentialAsync(request, default));
        permissions.Granted.Add(AppPermissions.ServerDatabasesManage);
        auth.Role = DeviceRole.Observer;
        await RejectAsync<UnauthorizedAccessException>(() => scoped.StageCredentialAsync(request, default));
        await RejectAsync<UnauthorizedAccessException>(() => mysql.StageCredentialAsync(request, default));
        await RejectAsync<UnauthorizedAccessException>(() => installation.StartAsync(
            InstallationServiceId.MySql, InstallationOperationKind.Configure, options, "observer-key", default));
        await RejectAsync<UnauthorizedAccessException>(() => installation.RequestAuthorizationAsync(InstallationServiceId.MySqlAccounts));
        auth.Role = DeviceRole.Controller;
        await RejectAsync<UnauthorizedAccessException>(() => installation.RequestAuthorizationAsync(InstallationServiceId.Nginx));
        if (!await installation.RequestAuthorizationAsync(InstallationServiceId.MySqlAccounts)
            || broker.LastGrant != (HostElevationCapability.DatabaseAccountsManage, "mysqlaccounts") || broker.RequestCount != 1)
            throw new InvalidOperationException("The database authorization request escaped the selected installation service.");
        await RejectAsync<UnauthorizedAccessException>(() => installation.StartAsync(
            InstallationServiceId.Nginx, InstallationOperationKind.Configure, options, "foreign-key", default));
        var foreign = Guid.NewGuid();
        transport.ForeignOperation = foreign;
        if (await installation.GetAsync(foreign, default) is not null)
            throw new InvalidOperationException("Foreign installation details escaped the database capability.");
        await RejectAsync<UnauthorizedAccessException>(() => installation.CancelAsync(foreign, default));
        var operation = await installation.StartAsync(InstallationServiceId.MySql, InstallationOperationKind.Configure,
            options, "original-key", default);
        if (operation is null || transport.SubmissionKey != "original-key"
            || await installation.CancelAsync(operation.OperationId, default) is null || transport.Cancels != 1)
            throw new InvalidOperationException("Database installation did not preserve submission and cancellation scope.");
        await scoped.StageCredentialAsync(request, default);
        if (await mysql.StageCredentialAsync(request, default) != HttpStatusCode.Forbidden || transport.MySqlStages != 1 || transport.MySqlReads != 1)
            throw new InvalidOperationException("MySQL scope did not preserve the credential authorization response.");
        if (transport.Instance != request.InstanceId || transport.Staged != 1)
            throw new InvalidOperationException("PostgreSQL scope dispatched an unauthorized write or changed the selected instance.");

        var pending = scoped.MaintenancePlanAsync(default);
        broker.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingGrant = installation.RequestAuthorizationAsync(InstallationServiceId.PostgreSqlMaintenance);
        auth.ServiceId = "https://replacement.invalid";
        auth.NotifyStateChanged();
        if (!transport.PendingToken.IsCancellationRequested)
            throw new InvalidOperationException("The previous PostgreSQL request token was not cancelled.");
        transport.Pending.SetResult(null!);
        if (!broker.PendingToken.IsCancellationRequested)
            throw new InvalidOperationException("The previous server authorization was not cancelled.");
        broker.Pending.SetResult(true);
        await RejectAsync<OperationCanceledException>(() => pendingGrant);
        await RejectAsync<OperationCanceledException>(() => pending);
        await RejectAsync<InvalidOperationException>(() => scoped.ReadAsync(default));
        await RejectAsync<InvalidOperationException>(() => mysql.ReadAsync(default));
        Console.WriteLine("Database scoped reads, controller-gated credential staging, installation isolation, and host switching passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Broker : IHostElevationBroker
    {
        public (HostElevationCapability, string) LastGrant { get; private set; }
        public int RequestCount { get; private set; }
        public CancellationToken PendingToken { get; private set; }
        public TaskCompletionSource<bool>? Pending { get; set; }

        public Task<bool> RequestGrantAsync(HostElevationCapability capability, string target,
            CancellationToken cancellationToken = default)
        {
            LastGrant = (capability, target);
            RequestCount++;
            PendingToken = cancellationToken;
            return Pending?.Task ?? Task.FromResult(true);
        }

        public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Transport : IServerPostgreSql, IServerMySql, IInstallationOperations
    {
        public Guid ForeignOperation { get; set; }
        public string? SubmissionKey { get; private set; }
        public int Cancels { get; private set; }
        private InstallationOperationDto? _operation;

        public Task<InstallationOperationDto?> StartAsync(InstallationServiceId service, InstallationOperationKind kind,
            object options, string key, CancellationToken cancellationToken)
        {
            SubmissionKey = key;
            _operation = CreateOperation(Guid.NewGuid(), service);
            return Task.FromResult<InstallationOperationDto?>(_operation);
        }

        public Task<InstallationOperationDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(id == ForeignOperation ? CreateOperation(id, InstallationServiceId.Nginx) : _operation);

        public Task<InstallationOperationDto?> GetActiveAsync(InstallationServiceId service, CancellationToken cancellationToken) =>
            Task.FromResult(_operation);

        public Task<InstallationOperationDto?> CancelAsync(Guid id, CancellationToken cancellationToken)
        {
            Cancels++;
            return Task.FromResult(_operation);
        }

        private static InstallationOperationDto CreateOperation(Guid id, InstallationServiceId service) =>
            new(id, service, InstallationOperationKind.Configure, InstallationOperationState.Queued, InstallationStage.Queued,
                null, null, DateTimeOffset.UtcNow, null, null, true);

        public Guid? Instance { get; private set; }
        public int Staged { get; private set; }
        public int MySqlStages { get; private set; }
        public int MySqlReads { get; private set; }
        public CancellationToken PendingToken { get; private set; }
        public TaskCompletionSource<PostgreSqlState> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PostgreSqlState> ReadAsync(CancellationToken cancellationToken, Guid? instance = null)
        {
            Instance = instance;
            return Task.FromResult<PostgreSqlState>(null!);
        }

        public Task<PostgreSqlState> MaintenancePlanAsync(CancellationToken cancellationToken)
        {
            PendingToken = cancellationToken;
            return Pending.Task;
        }

        public Task<PostgreSqlState> MigrationPlanAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        Task<MySqlState?> IServerMySql.ReadAsync(CancellationToken cancellationToken)
        {
            MySqlReads++;
            return Task.FromResult<MySqlState?>(null);
        }

        Task<HttpStatusCode> IServerMySql.StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken)
        {
            MySqlStages++;
            return Task.FromResult(HttpStatusCode.Forbidden);
        }

        public Task StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken)
        {
            Staged++;
            return Task.CompletedTask;
        }
    }
}
