internal static class ManagedFrpsChecks
{
    internal static async Task VerifyConfigurationValuesAsync(string root)
    {
        var services = new ServiceCollection();
        services.AddDbContext<RelaxKonOSDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "values-audit.db")}"));
        services.AddScoped<ITunnelAudit, TunnelAudit>();
        await using var container = services.BuildServiceProvider();
        await using (var scope = container.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>().Database.EnsureCreatedAsync();
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "values-keys")));
        var environment = new TestHostEnvironment(root);
        var runtime = new FixtureRuntime(Path.Combine(root, "unused-frps"));
        var windows = new ManagedRuntimeOperations(new CapturingPrivilegedTransport());
        using var server = new ManagedFrpsService(environment, protection, runtime, container.GetRequiredService<IServiceScopeFactory>(), windows);
        var request = new UpdateManagedFrpsConfigurationRequest(true, "127.0.0.1", 7000, [new(6000, 6010)], null, null, true,
            "saved-token", true, "127.0.0.1", 7500, "admin", "saved-dashboard-password", 0);
        var saved = await server.UpdateAsync(request, "actor", default);
        TestAssert.Assert(saved.Token == request.Token && saved.DashboardPassword == request.DashboardPassword, "Save omitted configured credentials.");
        var refreshed = await server.GetAsync(default);
        var editing = await server.GetForEditingAsync("actor", default);
        TestAssert.Assert(refreshed.Token == saved.Token && refreshed.DashboardPassword == saved.DashboardPassword
            && editing.Token == saved.Token && editing.DashboardPassword == saved.DashboardPassword, "Refresh or reopening hid configured credentials.");
        var retained = await server.UpdateAsync(request with { ExpectedRevision = 1, Token = null, DashboardPassword = null }, "actor", default);
        TestAssert.Assert(retained.Token == saved.Token && retained.DashboardPassword == saved.DashboardPassword, "Blank updates did not retain and return credentials.");
        var persisted = await File.ReadAllTextAsync(Path.Combine(root, "data", "runtimes", "frp", "frps", "config.json"));
        TestAssert.Assert(!persisted.Contains("saved-token") && !persisted.Contains("saved-dashboard-password"), "Configuration persisted plaintext credentials.");
    }

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddDbContext<RelaxKonOSDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "audit.db")}"));
        services.AddScoped<ITunnelAudit, TunnelAudit>();
        await using var container = services.BuildServiceProvider();
        await using (var scope = container.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>().Database.EnsureCreatedAsync();
        var protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var transport = new IndependentManagedRuntimeTransport();
        var runtime = new FixtureRuntime("independent-fixture");
        var operations = new ManagedRuntimeOperations(transport);
        var environment = new TestHostEnvironment(root);
        var request = new UpdateManagedFrpsConfigurationRequest(true, "127.0.0.1", FreePort(), [new(6000, 6010)], null, null, true,
            "private-fixture-token", false, "127.0.0.1", null, null, null, 0);
        var server = new ManagedFrpsService(environment, protector, runtime, container.GetRequiredService<IServiceScopeFactory>(), operations);
        await server.UpdateAsync(request, "actor", default);
        TestAssert.Assert((await server.StartAsync("actor", default)).Succeeded, "Independent frps did not start.");
        TestAssert.Assert((await server.GetAsync(default)).AppliedRevision == 1, "Independent service did not persist applied revision.");
        await server.UpdateAsync(request with { ExpectedRevision = 1, Token = null, ForceTls = false }, "actor", default);
        TestAssert.Assert((await server.GetAsync(default)).AppliedRevision == 1, "Saving a configuration falsely applied it.");
        var unchanged = await server.StartAsync("actor", default);
        TestAssert.Assert(!unchanged.Succeeded && unchanged.ProblemCode == "tunnel.frps_restart_required", "Running service silently accepted another revision.");
        server.Dispose();
        TestAssert.Assert(transport.FrpsState.Running, "Disposing Server stopped the independent component.");
        using var reopened = new ManagedFrpsService(environment, protector, runtime, container.GetRequiredService<IServiceScopeFactory>(), operations);
        var restored = await reopened.GetAsync(default);
        TestAssert.Assert(restored.State == ManagedFrpsState.Running && restored.Revision == 2 && restored.AppliedRevision == 1,
            "Reopened Server lost the running service or its applied revision.");
        TestAssert.Assert((await reopened.GetForEditingAsync("actor", default)).Token == request.Token, "Reopened Server lost saved credentials.");
        var stopped = await reopened.StopAsync("actor", default);
        TestAssert.Assert(stopped.Succeeded && !transport.FrpsState.Running && (await reopened.GetAsync(default)).AppliedRevision is null,
            "Explicit stop failed to stop the independent service.");
        TestAssert.Assert((await reopened.StartAsync("actor", default)).Succeeded && (await reopened.GetAsync(default)).AppliedRevision == 2,
            "Reapplication did not commit the saved revision.");
        await reopened.StopAsync("actor", default);
        using (var occupied = new TcpListener(IPAddress.Loopback, request.BindPort))
        {
            occupied.Start();
            TestAssert.Assert(!(await reopened.StartAsync("actor", default)).Succeeded, "Occupied bind port was accepted.");
        }
        var persisted = await File.ReadAllTextAsync(Path.Combine(root, "data", "runtimes", "frp", "frps", "config.json"));
        TestAssert.Assert(!persisted.Contains(request.Token!), "FRPS credentials were persisted in plaintext.");
        await using var auditScope = container.CreateAsyncScope();
        var audit = await auditScope.ServiceProvider.GetRequiredService<ITunnelAudit>().ListFrpsAsync(default);
        TestAssert.Assert(audit.Any(x => x.Action == "frps.token.read") && !JsonSerializer.Serialize(audit).Contains(request.Token!),
            "FRPS secret audit was missing or exposed credentials.");
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private sealed class FixtureRuntime(string path) : IRuntimeManager
    {
        public Task<TunnelRuntimeDto> GetManagedFrpsStatusAsync(CancellationToken ct) => Task.FromResult(new TunnelRuntimeDto("frp", TunnelRuntimeMode.Managed, TunnelRuntimeState.Available, "fixture", path));
        public Task<TunnelRuntimeDto> GetManagedFrpcStatusAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelRuntimeDto> DetectExternalFrpcAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelRuntimeDownloadDto?> GetManagedFrpcDownloadAsync(string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelOperationResultDto> InstallManagedFrpcAsync(string version, IInstallationProgress progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelOperationResultDto> InstallManagedFrpcFromArchiveAsync(string version, Stream archive, long length, IInstallationProgress progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelOperationResultDto> UninstallManagedFrpcAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelOperationResultDto> RollbackManagedFrpcAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
