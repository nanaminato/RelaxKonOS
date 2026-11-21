using System.Net;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Databases;
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
        using var lifetime = new CancellationTokenSource();
        var scoped = new ServerDatabaseCapability(id, permissions, new ExternalServerConnection(memory.Capture(session)),
            () => transport, lifetime.Token, () => transport);
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
        auth.Role = DeviceRole.Controller;
        await scoped.StageCredentialAsync(request, default);
        if (await mysql.StageCredentialAsync(request, default) != HttpStatusCode.Forbidden || transport.MySqlStages != 1 || transport.MySqlReads != 1)
            throw new InvalidOperationException("MySQL scope did not preserve the credential authorization response.");
        if (transport.Instance != request.InstanceId || transport.Staged != 1)
            throw new InvalidOperationException("PostgreSQL scope dispatched an unauthorized write or changed the selected instance.");

        var pending = scoped.MaintenancePlanAsync(default);
        auth.ServiceId = "https://replacement.invalid";
        auth.NotifyStateChanged();
        if (!transport.PendingToken.IsCancellationRequested)
            throw new InvalidOperationException("The previous PostgreSQL request token was not cancelled.");
        transport.Pending.SetResult(null!);
        await RejectAsync<OperationCanceledException>(() => pending);
        await RejectAsync<InvalidOperationException>(() => scoped.ReadAsync(default));
        await RejectAsync<InvalidOperationException>(() => mysql.ReadAsync(default));
        Console.WriteLine("Database scoped reads, controller-gated credential staging, and host switching passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Transport : IServerPostgreSql, IServerMySql
    {
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
