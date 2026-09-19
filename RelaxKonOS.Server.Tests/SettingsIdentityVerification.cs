using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

/// <summary>
/// Behaviour checks for the remote host-name domain. The provider is a controlled in-memory double, so
/// these cases prove coordination, not Windows or Linux platform effects.
/// </summary>
internal static class SettingsIdentityVerification
{
    public static async Task RunAsync(string root)
    {
        var environment = new TestHostEnvironment(root);
        var keys = new DirectoryInfo(Path.Combine(root, "settings-identity-keys"));
        var protection = DataProtectionProvider.Create(keys);
        var journal = new SettingsOperationJournal(environment, protection);
        var provider = new ControlledIdentityProvider();
        var grants = new HostElevationSessionStore();
        var coordinator = new HostIdentityOperationCoordinator(journal, provider, grants);
        var actor = Principal();

        // Shared syntax rules: a single RFC 952/1123 label, platform-bounded, never all digits.
        Check(HostIdentityValidation.Validate(new("relaxkon-host"), true) is null, "A valid short name must be accepted.");
        Check(HostIdentityValidation.Validate(new("host-01"), false) is null, "A valid name must be accepted on Linux.");
        Check(HostIdentityValidation.Validate(new(""), true) == "settings.identity.invalid_name", "An empty name must be rejected.");
        Check(HostIdentityValidation.Validate(new("has space"), true) == "settings.identity.invalid_name", "A space must be rejected.");
        Check(HostIdentityValidation.Validate(new("-leading"), true) == "settings.identity.invalid_name", "A leading hyphen must be rejected.");
        Check(HostIdentityValidation.Validate(new("trailing-"), true) == "settings.identity.invalid_name", "A trailing hyphen must be rejected.");
        Check(HostIdentityValidation.Validate(new("12345"), true) == "settings.identity.invalid_name", "An all-digit name must be rejected.");
        Check(HostIdentityValidation.Validate(new("host.example.com"), true) == "settings.identity.invalid_name", "A fully-qualified name must be rejected.");
        Check(HostIdentityValidation.Validate(new("host\u0000name"), true) == "settings.identity.invalid_name", "A NUL must be rejected.");
        Check(HostIdentityValidation.Validate(new("relaxkon-host-01"), true) == "settings.identity.invalid_name",
            "The Windows NetBIOS limit must be enforced independently of the platform provider.");
        Check(HostIdentityValidation.Validate(new("relaxkon-host-01"), false) is null, "Linux accepts the same 16-character name the Windows limit rejects.");

        var request = new HostnamePreviewRequest(provider.Revision, "identity-test", new("Test-Renamed"));
        var plan = await coordinator.PreviewAsync(actor, request, default);
        Check(plan.PlanId == (await coordinator.PreviewAsync(actor, request, default)).PlanId,
            "Idempotent preview must keep the original plan id.");
        Check(plan.Differences.Count == 1 && plan.Differences[0].Before == "Test-Old" && plan.Differences[0].After == "Test-Renamed",
            "The plan must describe the pending name it replaces.");
        await RejectAsync(409, () => coordinator.PreviewAsync(actor, request with { Change = new("Test-Other") }, default));
        await RejectAsync(400, () => coordinator.PreviewAsync(actor, new(provider.Revision, "unchanged", new("Test-Old")), default));
        await RejectAsync(400, () => coordinator.PreviewAsync(actor, new(provider.Revision, "invalid", new("bad_name")), default));
        await RejectAsync(409, () => coordinator.PreviewAsync(actor, new(SettingsRevisions.Hash("stale"), "stale", new("Test-Renamed")), default));
        await RejectAsync(404, () => coordinator.GetIfExistsAsync(Principal(), plan.PlanId, default));

        // Authorization is required, and a missing grant must not reach the provider write.
        await RejectAsync(428, () => coordinator.ApplyAsync(actor, plan.PlanId, default));
        Check(provider.Writes == 0, "Missing elevation must not reach the provider write.");

        grants.Grant(actor, HostElevationCapability.HostIdentityChange, HostIdentityOperationCoordinator.IdentityResource, false, "test");
        var applied = await coordinator.ApplyAsync(actor, plan.PlanId, default);
        Check(applied.State == SettingsOperationState.Applied && provider.PendingName == "Test-Renamed",
            "Apply must read back the provider's actual new state.");
        var stagedState = OperatingSystem.IsWindows() ? SettingsEffectiveState.HostRestart : SettingsEffectiveState.Immediate;
        Check(applied.EffectiveState == stagedState,
            "A staged rename must report HostRestart and an immediate platform must report Immediate; neither may claim the other.");
        await coordinator.ApplyAsync(actor, plan.PlanId, default);
        Check(provider.Writes == 1, "HTTP retries must not replay a helper operation.");

        // A restarted server must find the durable record rather than resend the write.
        var reopened = new HostIdentityOperationCoordinator(new SettingsOperationJournal(environment, DataProtectionProvider.Create(keys)), provider, grants);
        Check((await reopened.GetIfExistsAsync(actor, plan.PlanId, default))!.State == SettingsOperationState.Applied,
            "Encrypted identity operation records must survive reopening.");
        provider.PendingName = "Test-External";
        await RejectAsync(409, () => reopened.RollbackIfExistsAsync(actor, plan.PlanId, new(applied.ObservedRevision!), default));
        Check(provider.Writes == 1, "Rollback must not overwrite another administrator's later edit.");
        provider.PendingName = "Test-Renamed";
        Check((await reopened.RollbackIfExistsAsync(actor, plan.PlanId, new(applied.ObservedRevision!), default))!.State
            == SettingsOperationState.RolledBack, "Authorized rollback must restore and read back the original name.");
        Check(provider.PendingName == "Test-Old", "Rollback lost the original host name.");

        // A lost Helper result must stay Unknown and must never be replayed.
        var uncertain = await coordinator.PreviewAsync(actor, new(provider.Revision, "uncertain", new("Test-Renamed")), default);
        provider.ThrowAfterWrite = true;
        Check((await coordinator.ApplyAsync(actor, uncertain.PlanId, default)).State == SettingsOperationState.Unknown,
            "A lost helper result must be Unknown, never Failed or Applied.");
        var writes = provider.Writes;
        await reopened.ApplyAsync(actor, uncertain.PlanId, default);
        Check(provider.Writes == writes, "Unknown operations must not be replayed after reopening.");

        // A deterministic refusal is Failed, because the platform rejected the name and did not change.
        provider.ThrowAfterWrite = false;
        // The uncertain write above did persist, so the platform is brought back to the observed
        // baseline the way an administrator's own correction would.
        provider.PendingName = "Test-Old";
        var refused = await coordinator.PreviewAsync(actor, new(provider.Revision, "refused", new("Test-Renamed")), default);
        provider.Refuse = true;
        Check((await coordinator.ApplyAsync(actor, refused.PlanId, default)).State == SettingsOperationState.Failed,
            "A platform refusal must be Failed, not Unknown.");
        Check(provider.PendingName == "Test-Old", "A refusal must leave the platform name unchanged.");
        provider.Refuse = false;

        var protectedBytes = await File.ReadAllBytesAsync(Path.Combine(root, "data", "settings-operations", "operations.db"));
        Check(!System.Text.Encoding.UTF8.GetString(protectedBytes).Contains("Test-Renamed", StringComparison.Ordinal),
            "Journal must not store recovery content in plaintext.");
        Console.WriteLine("Settings identity passed: name rules, plan idempotency, revision conflict, elevation gate, staged readback, "
            + "restart effective state, external-edit rollback conflict, Unknown no-replay, deterministic refusal. Provider writes are controlled test data only.");
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

    /// <summary>Mirrors a platform that stages the rename: the pending name changes before the live one.</summary>
    private sealed class ControlledIdentityProvider : IHostIdentityService
    {
        public string ActiveName { get; private set; } = "Test-Old";
        public string PendingName { get; set; } = "Test-Old";
        public string Revision => SettingsRevisions.Hash(ActiveName + "\n" + PendingName);
        public int Writes { get; private set; }
        public bool ThrowAfterWrite { get; set; }
        public bool Refuse { get; set; }

        private HostIdentityState State() => new(ActiveName, PendingName, HostIdentityValidation.WindowsMaximumLength,
            Revision, DateTimeOffset.UtcNow, "controlled-test-provider");

        public Task<HostIdentityState> ReadAsync(CancellationToken ct) => Task.FromResult(State());

        public Task<PrivilegedOperationResult> ApplyAsync(HostnameChange change, string expectedRevision, Guid operationId, CancellationToken ct)
        {
            Check(expectedRevision == Revision, "Provider must receive the observed baseline.");
            if (Refuse) return Task.FromResult(new PrivilegedOperationResult(false, 1, ProblemCode: PrivilegedProblemCode.ResourceNotAllowed));
            Writes++;
            PendingName = change.HostName;
            if (ThrowAfterWrite) throw new IOException("Simulated lost response after persistence.");
            return Task.FromResult(new PrivilegedOperationResult(true, HostIdentity: State()));
        }
    }
}
