using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;
using System.Text.Json.Nodes;

internal static class ModulePrivilegeConsentChecks
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "module-consents-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock(); var trusted = true;
            var identity = new ModulePrivilegeIdentity("fixture", "1.0.0", "publisher", new string('a', 64), new string('b', 64),
                "revision-1", [ModuleCapabilityFixtures.Service("fixture.service", "restart")], ["fixture.service"]);
            ModulePrivilegeIdentity Resolve(string module) => trusted && module == identity.ModuleId ? identity
                : throw new ModulePrivilegeException("module.privilege_denied", 403);
            var consents = new ModulePrivilegeConsentStore(root, Resolve, clock);
            var operations = new ModulePrivilegePlanStore(root, Resolve, clock);
            ModulePrivilegeConsentRequest Request(string key) => new("fixture", ModuleCapabilityCatalog.ServiceManage, "restart", "fixture.service", "Scheduled test service maintenance", clock.Current.AddHours(1), key);
            static Task<ModulePrivilegeAdministrator?> Verify() => Task.FromResult<ModulePrivilegeAdministrator?>(new("linux:1000", "linux-pam-administrator"));
            async Task Reject(Func<Task> action, string code, string message)
            {
                try { await action(); }
                catch (ModulePrivilegeException error) when (error.Code == "module.privilege_" + code) { check(true, message); return; }
                throw new Exception("Expected standing consent rejection: " + message);
            }
            async Task<ModulePrivilegeConsent> Prepare(string key)
            {
                var pending = await consents.PrepareAsync("session", "granting-user", Request(key));
                return await consents.ReadAsync(pending.ConsentId, "session");
            }
            static ModulePrivilegeApproval Approval(ModulePrivilegeConsent consent) => new(consent.PlanHash, consent.ConfirmationToken);
            var executions = 0;
            Task<ModulePrivilegeStatus> Execute(Guid consentId, string key, string target = "fixture.service", string module = "fixture", bool responseLost = false)
            {
                var request = new ModulePrivilegeRequest(ModuleCapabilityCatalog.ServiceManage, "restart", target, "Scheduled maintenance", key);
                return consents.ExecuteAsync(consentId, module, request, async authority =>
                {
                    var session = "consent:" + authority.Id.ToString("N");
                    var prepared = await operations.PrepareAsync(module, session, request, authority.Actor);
                    if (prepared.State != "AwaitingConfirmation") return await operations.GetForModuleAsync(prepared.PlanId, module);
                    var plan = await operations.ReadForUserAsync(prepared.PlanId, session);
                    return await operations.ApproveAsync(plan.PlanId, session, new(plan.PlanHash, plan.ConfirmationToken),
                        () => Task.FromResult<ModulePrivilegeAdministrator?>(authority.Administrator), (_, _) =>
                        {
                            executions++;
                            if (responseLost) throw new IOException("test response lost after dispatch");
                            return Task.FromResult(true);
                        });
                });
            }
            var initialRequest = Request("first");
            var pending = await Prepare("first");
            check(pending.State == "AwaitingConfirmation" && pending.ConfirmationToken.Length == 64 && executions == 0,
                "standing consent preparation never executes or grants authority");
            await Reject(() => Execute(pending.ConsentId, "no-consent"), "confirmation_required", "background operation cannot use an unapproved standing consent");
            await Reject(() => consents.ReadAsync(pending.ConsentId, "another-session"), "denied", "standing consent confirmation belongs to the initiating administrator session");
            await Reject(() => consents.ApproveAsync(pending.ConsentId, "session", Approval(pending), () => Task.FromResult<ModulePrivilegeAdministrator?>(null)),
                "admin_required", "standing consent rejects failed native administrator verification");
            await Reject(() => consents.ApproveAsync(pending.ConsentId, "session", Approval(pending) with { PlanHash = new string('0', 64) }, Verify),
                "approval_invalid", "standing consent rejects a changed scope/hash");
            var active = await consents.ApproveAsync(pending.ConsentId, "session", Approval(pending), Verify);
            check(active.State == "Active" && active.ConfirmationToken == "", "explicit administrator confirmation activates exactly the reviewed scope");
            await Reject(() => consents.ApproveAsync(pending.ConsentId, "session", Approval(pending), Verify), "approval_invalid", "standing consent approval cannot replay");
            await Reject(() => Execute(active.ConsentId, "other-target", "outside.service"), "confirmation_required", "background target expansion never dispatches or queues");
            await Reject(() => Execute(active.ConsentId, "other-module", module: "other"), "confirmation_required", "a consent ID cannot authorize another authenticated module");
            var results = await Task.WhenAll(Execute(active.ConsentId, "same"), Execute(active.ConsentId, "same"));
            check(results.All(r => r.State == "Succeeded") && results[0].PlanId == results[1].PlanId && executions == 1,
                "concurrent background retries share one durable operation and one dispatch");
            consents = new ModulePrivilegeConsentStore(root, Resolve, clock);
            operations = new ModulePrivilegePlanStore(root, Resolve, clock);
            check((await Execute(active.ConsentId, "same")).PlanId == results[0].PlanId && executions == 1,
                "standing consent and operation survive restart without replay");
            var unknown = await Execute(active.ConsentId, "lost-response", responseLost: true);
            check(unknown.State == "RecoveryRequired" && executions == 2, "standing authority does not turn a lost Helper response into a successful background result");
            consents = new ModulePrivilegeConsentStore(root, Resolve, clock);
            operations = new ModulePrivilegePlanStore(root, Resolve, clock);
            var unknownRetry = await Execute(active.ConsentId, "lost-response", responseLost: true);
            check(unknownRetry.PlanId == unknown.PlanId && unknownRetry.State == "RecoveryRequired" && executions == 2,
                "standing consent cannot automatically replay an unknown background operation after restart");
            await consents.RevokeAsync(active.ConsentId, "another-host-administrator");
            await Reject(() => Execute(active.ConsentId, "after-revoke"), "confirmation_required", "administrator revocation immediately blocks later background operations");
            var cancelled = await Prepare("cancelled");
            await consents.RevokeAsync(cancelled.ConsentId, "granting-user");
            await Reject(() => consents.ApproveAsync(cancelled.ConsentId, "session", Approval(cancelled), Verify), "approval_invalid", "a refused standing consent cannot be approved later");
            var versioned = await Prepare("versioned");
            await consents.ApproveAsync(versioned.ConsentId, "session", Approval(versioned), Verify);
            identity = identity with { Version = "2.0.0", ArtifactSha256 = new string('c', 64) };
            await Reject(() => Execute(versioned.ConsentId, "after-update"), "plan_changed", "module version/digest change invalidates standing authority");
            identity = identity with { Version = "1.0.0", ArtifactSha256 = new string('a', 64) };
            trusted = false;
            await Reject(() => Execute(versioned.ConsentId, "after-trust-revoke"), "denied", "code trust revocation invalidates standing authority immediately");
            trusted = true;
            clock.Current = clock.Current.AddHours(2);
            await Reject(() => Execute(versioned.ConsentId, "expired"), "confirmation_required", "expired standing consent cannot dispatch");
            var shortPlan = await Prepare("old-confirmation");
            clock.Current = clock.Current.AddMinutes(6);
            var originalConsent = await consents.PrepareAsync("session", "granting-user", initialRequest);
            check(originalConsent.ConsentId == active.ConsentId && originalConsent.State == "Revoked",
                "an identical standing-consent creation retry returns the original revoked record even after its deadline");
            await Reject(() => consents.ApproveAsync(shortPlan.ConsentId, "session", Approval(shortPlan), Verify), "approval_invalid", "standing consent review expires after five minutes even with a later grant deadline");
            await Reject(() => consents.PrepareAsync("session", "user", Request("too-long") with { ExpiresAt = clock.Current.AddDays(31) }),
                "consent_invalid", "standing consent cannot request an unbounded or excessive duration");
            var file = Path.Combine(root, "data", "module-management", "privilege-consents.json");
            var original = File.ReadAllText(file);
            File.Delete(file);
            await Reject(() => consents.ListAsync(), "recovery_required", "a missing durable standing-consent file fails closed in the live process");
            File.WriteAllText(file, original);
            var changed = JsonNode.Parse(original)!; changed["consents"]![0]!["request"]!["target"] = "outside.service";
            File.WriteAllText(file, changed.ToJsonString());
            await Reject(() => consents.ListAsync(), "recovery_required", "live standing consent detects persisted state changed behind its owner");
            try { _ = new ModulePrivilegeConsentStore(root, Resolve, clock); throw new Exception("corrupt state accepted"); }
            catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required") { check(true, "standing consent recovery rechecks the frozen request and module hashes"); }
            File.WriteAllText(file, original);
            var audit = JsonNode.Parse(original)!["consents"]![0]!;
            check(audit["actor"]!.GetValue<string>() == "granting-user" && audit["administrator"]!["identity"]!.GetValue<string>() == "linux:1000"
                && audit["approvedAt"] is not null && audit["revokedBy"]!.GetValue<string>() == "another-host-administrator" && audit["revokedAt"] is not null,
                "standing consent records granting user, actual administrator, approval and revocation evidence without credentials");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
