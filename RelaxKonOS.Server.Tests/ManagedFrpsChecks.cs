internal static class ManagedFrpsChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("frps shell fixture requires Linux.");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "frps-fixture");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nif [ \"$1\" = \"verify\" ]; then exit 0; fi\necho 'token=private-fixture-token password=private-dashboard-password'\nsleep 30\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var services = new ServiceCollection();
        services.AddDbContext<RelaxKonOSDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "audit.db")}"));
        services.AddScoped<ITunnelAudit, TunnelAudit>();
        await using var container = services.BuildServiceProvider();
        await using (var scope = container.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>().Database.EnsureCreatedAsync();
        var protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
        var environment = new TestHostEnvironment(root);
        var runtime = new FixtureRuntime(executable);
        var windows = new WindowsManagedRuntimeOperations(new CapturingPrivilegedTransport());
        using var server = new ManagedFrpsService(environment, protector, runtime, container.GetRequiredService<IServiceScopeFactory>(), windows);
        TestAssert.Assert((await server.GetAsync(default)).State == ManagedFrpsState.NotConfigured, "An empty frps host was shown configured.");
        var request = new UpdateManagedFrpsConfigurationRequest(true, "127.0.0.1", FreePort(), [new(6000, 6010)], null, null, true,
            "private-fixture-token", false, "127.0.0.1", null, null, null, 0);
        var saved = await server.UpdateAsync(request, "actor", default);
        TestAssert.Assert(saved.Revision == 1 && saved.State == ManagedFrpsState.Stopped && saved.Token is null && saved.AppliedRevision is null, "frps save leaked Token or claimed a running configuration.");
        var serialized = JsonSerializer.Serialize(saved, RelaxKonOSJsonOptions.Default);
        TestAssert.Assert(!serialized.Contains("private-fixture-token"), "Safe frps DTO leaked Token.");
        try { await server.UpdateAsync(request, "actor", default); throw new Exception("Stale frps save was accepted."); } catch (ManagedFrpsRevisionConflictException) { }
        var json = JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default);
        var missing = System.Text.Json.Nodes.JsonNode.Parse(json)!; missing.AsObject().Remove("expectedRevision");
        try { JsonSerializer.Deserialize<UpdateManagedFrpsConfigurationRequest>(missing.ToJsonString(), RelaxKonOSJsonOptions.Default); throw new Exception("frps missing expectedRevision was accepted."); } catch (JsonException) { }
        try { await server.UpdateAsync(request with { ExpectedRevision = 1, Token = "token\ninjection" }, "actor", default); throw new Exception("Multiline frps secret was accepted."); } catch (ManagedFrpsValidationException ex) { TestAssert.Assert(ex.ProblemCode == "tunnel.frps_secret_invalid", "frps secret rejection was unstable."); }
        var editor = await server.GetForEditingAsync("actor", default);
        TestAssert.Assert(editor.Token == request.Token, "Controller frps editor did not return the current Token.");
        var persisted = await File.ReadAllTextAsync(Path.Combine(root, "data", "runtimes", "frp", "frps", "config.json"));
        TestAssert.Assert(!persisted.Contains("private-fixture-token"), "frps file did not protect Token.");
        try
        {
            var started = await server.StartAsync("actor", default);
            var running = await server.GetAsync(default);
            TestAssert.Assert(started.Succeeded && running.State == ManagedFrpsState.Running && running.AppliedRevision == 1, "frps startup did not establish the applied revision.");
            var changed = await server.UpdateAsync(request with { ExpectedRevision = 1, Token = null, ForceTls = false }, "actor", default);
            TestAssert.Assert(changed.State == ManagedFrpsState.Running && changed.Revision == 2 && changed.AppliedRevision == 1 && changed.Token is null, "Saving frps changes falsely applied them.");
            var unchanged = await server.StartAsync("actor", default);
            TestAssert.Assert(!unchanged.Succeeded && unchanged.State == TunnelConnectionState.SavedNotApplied && unchanged.ProblemCode == "tunnel.frps_restart_required", "Start silently applied a new frps configuration to an existing process.");
            using var reopened = new ManagedFrpsService(environment, protector, runtime, container.GetRequiredService<IServiceScopeFactory>(), windows);
            TestAssert.Assert((await reopened.GetAsync(default)).State == ManagedFrpsState.Unknown, "Reopening saved frps configuration guessed a stopped process.");
            var unknownStop = await reopened.StopAsync("actor", default);
            TestAssert.Assert(!unknownStop.Succeeded && unknownStop.State == TunnelConnectionState.Unknown, "A supervisor without process ownership claimed a successful stop.");
            TestAssert.Assert((await server.GetAsync(default)).State == ManagedFrpsState.Running, "Unowned stop affected the original process.");
            var stopped = await server.StopAsync("actor", default);
            TestAssert.Assert(stopped.Succeeded && stopped.State == TunnelConnectionState.Disconnected && (await server.GetAsync(default)).AppliedRevision is null, "frps stop was reported connected or retained applied proof.");
            TestAssert.Assert((await server.StartAsync("actor", default)).Succeeded && (await server.GetAsync(default)).AppliedRevision == 2, "frps restart did not apply the saved revision.");
            await server.StopAsync("actor", default);
            var dashboardPort = FreePort(); while (dashboardPort == request.BindPort) dashboardPort = FreePort();
            var secured = await server.UpdateAsync(request with { ExpectedRevision = 2, Token = null, DashboardEnabled = true, DashboardPort = dashboardPort,
                DashboardUser = "admin", DashboardPassword = "private-dashboard-password" }, "actor", default);
            TestAssert.Assert(secured.Revision == 3 && secured.DashboardPasswordConfigured && secured.Token is null, "Dashboard credentials did not persist as safe metadata.");
            var retained = await server.UpdateAsync(request with { ExpectedRevision = 3, Token = null, DashboardEnabled = true, DashboardPort = dashboardPort, DashboardUser = "admin", DashboardPassword = null }, "actor", default);
            TestAssert.Assert(retained.DashboardPasswordConfigured && retained.TokenConfigured, "Blank replacement lost stored frps secrets.");
            using var occupied = new TcpListener(IPAddress.Loopback, dashboardPort); occupied.Start();
            var refused = await server.StartAsync("actor", default);
            TestAssert.Assert(!refused.Succeeded && refused.ProblemCode == "tunnel.frps_port_in_use", "Occupied dashboard listener lost its stable failure result.");
            occupied.Stop();
            TestAssert.Assert((await server.StartAsync("actor", default)).Succeeded, "Configured dashboard frps did not start after releasing its port.");
            await Task.Delay(100);
            var logs = await server.GetLogsAsync(default);
            TestAssert.Assert(logs.Count <= 200 && logs.Any(x => x.Message.Contains("<redacted>")) && logs.All(x => !x.Message.Contains("private-fixture-token") && !x.Message.Contains("private-dashboard-password")), "frps logs leaked fixture secrets.");
            await using var scope = container.CreateAsyncScope();
            var audit = await scope.ServiceProvider.GetRequiredService<ITunnelAudit>().ListFrpsAsync(default);
            TestAssert.Assert(audit.Any(x => x.Action == "frps.token.read") && audit.Any(x => x.Action == "frps.configure" && x.ProblemCode == "tunnel.revision_conflict"), "frps secret reads or conflict writes lost audit evidence.");
            TestAssert.Assert(!JsonSerializer.Serialize(audit).Contains("private-fixture-token"), "frps audit leaked a secret.");
        }
        finally { await server.StopAsync("actor", default); }
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
