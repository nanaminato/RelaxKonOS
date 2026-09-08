using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

internal static class SettingsOperationVerification
{
    public static async Task RunAsync(string root)
    {
        var environment = new TestHostEnvironment(root);
        var keys = new DirectoryInfo(Path.Combine(root, "settings-test-keys"));
        var protection = DataProtectionProvider.Create(keys);
        var journal = new SettingsOperationJournal(environment, protection);
        var provider = new ControlledTimeProvider();
        var grants = new HostElevationSessionStore();
        var coordinator = new SettingsOperationCoordinator(journal, provider, grants);
        var actor = Principal();
        var request = new TimeZonePreviewRequest(provider.Revision, "time-test", new("Test/Two"));
        var plan = await coordinator.PreviewTimeAsync(actor, request, default);
        Check(plan.PlanId == (await coordinator.PreviewTimeAsync(actor, request, default)).PlanId,
            "Idempotent preview must keep the original plan id.");
        await RejectAsync(409, () => coordinator.PreviewTimeAsync(actor, request with { Change = new("Test/One") }, default));
        await RejectAsync(404, () => coordinator.GetAsync(Principal(), plan.PlanId, default));
        await RejectAsync(428, () => coordinator.ApplyTimeAsync(actor, plan.PlanId, default));
        Check(provider.Writes == 0, "Missing elevation must not reach the provider write.");
        grants.Grant(actor, HostElevationCapability.HostTimeChange, SettingsOperationCoordinator.TimeResource, false, "test");
        var applied = await coordinator.ApplyTimeAsync(actor, plan.PlanId, default);
        Check(applied.State == SettingsOperationState.Applied && provider.Zone == "Test/Two", "Apply must read back the provider's actual new state.");
        await coordinator.ApplyTimeAsync(actor, plan.PlanId, default);
        Check(provider.Writes == 1, "HTTP retries must not replay a helper operation.");
        var reopened = new SettingsOperationCoordinator(new(environment, DataProtectionProvider.Create(keys)), provider, grants);
        Check((await reopened.GetAsync(actor, plan.PlanId, default)).State == SettingsOperationState.Applied, "Encrypted operation record must survive reopening.");
        provider.Zone = "Test/External";
        await RejectAsync(409, () => reopened.RollbackAsync(actor, plan.PlanId, new(applied.ObservedRevision!), default));
        Check(provider.Writes == 1, "Rollback must not overwrite an external administrator's edit.");
        provider.Zone = "Test/Two";
        Check((await reopened.RollbackAsync(actor, plan.PlanId, new(applied.ObservedRevision!), default)).State == SettingsOperationState.RolledBack,
            "Authorized rollback must restore and read back the original zone.");
        Check(provider.Zone == "Test/One", "Rollback lost the original zone.");
        var uncertain = await coordinator.PreviewTimeAsync(actor, new(provider.Revision, "uncertain", new("Test/Two")), default);
        provider.ThrowAfterWrite = true;
        Check((await coordinator.ApplyTimeAsync(actor, uncertain.PlanId, default)).State == SettingsOperationState.Unknown,
            "A lost helper result must be Unknown, never Failed or Applied.");
        var writes = provider.Writes;
        await reopened.ApplyTimeAsync(actor, uncertain.PlanId, default);
        Check(provider.Writes == writes, "Unknown operations must not be replayed after reopening.");
        var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(root, "data", "settings-operations", "operations.db"));
        Check(!System.Text.Encoding.UTF8.GetString(protectedBytes).Contains("Test/Two", StringComparison.Ordinal), "Journal must not store recovery content in plaintext.");
        var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/usr/bin/true");
        start.Environment["LD_PRELOAD"] = "untrusted";
        start.Environment["DOTNET_STARTUP_HOOKS"] = "untrusted";
        TrustedProcessEnvironment.Apply(start);
        Check(!start.Environment.ContainsKey("LD_PRELOAD") && !start.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"), "Privileged child environment must discard injection variables.");
        Console.WriteLine("Settings operations passed: durable encrypted plans, ownership, elevation, idempotency, readback, external-edit rollback conflict, Unknown no-replay, clean child environment. Provider writes are controlled test data only.");
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity([
        new Claim("sub", Guid.NewGuid().ToString()), new Claim("jti", Guid.NewGuid().ToString())], "test"));
    private static async Task RejectAsync<T>(int status, Func<Task<T>> action)
    {
        try { await action(); }
        catch (SettingsException exception) when (exception.StatusCode == status) { return; }
        throw new InvalidOperationException("Expected settings rejection " + status);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class ControlledTimeProvider : IHostTimeService
    {
        public string Zone { get; set; } = "Test/One";
        public string Revision => SettingsRevisions.Hash(Zone);
        public int Writes { get; private set; }
        public bool ThrowAfterWrite { get; set; }
        public Task<HostTimeState> ReadAsync(CancellationToken ct) => Task.FromResult(new HostTimeState(Zone,
            ["Test/One", "Test/Two", "Test/External"], Revision, DateTimeOffset.UtcNow, "controlled-test-provider"));
        public Task<PrivilegedOperationResult> ApplyAsync(TimeZoneChange change, string expectedRevision, Guid operationId, CancellationToken ct)
        {
            Check(expectedRevision == Revision, "Provider must receive observed baseline.");
            Writes++;
            Zone = change.TimeZoneId;
            if (ThrowAfterWrite) throw new IOException("Simulated lost response after persistence.");
            return Task.FromResult(new PrivilegedOperationResult(true));
        }
    }
}
