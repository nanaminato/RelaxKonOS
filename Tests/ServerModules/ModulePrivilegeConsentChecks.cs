using System.Security.Cryptography;
using System.Text;
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
                "revision-1",
                [
                    ModuleCapabilityFixtures.Service("fixture.service", "restart"),
                    ModuleCapabilityFixtures.Package("host-approved-fixture", "mysql-server", "install", "remove"),
                    new FileManageCapability
                    {
                        Platforms = [ModuleCapabilityFixtures.Platform], Root = "/etc/fixture",
                        Actions = ["write", "delete"], MaximumBytes = 4096,
                    },
                    new DataManageCapability
                    {
                        Platforms = [ModuleCapabilityFixtures.Platform], Root = "/var/lib/fixture",
                        Actions = ["preserve", "backup", "delete"],
                    },
                    new ApplicationManageCapability
                    {
                        Platforms = [ModuleCapabilityFixtures.Platform], Protocols = ["mysql-admin"],
                        Instances = ["fixture-db"], Actions = ["query", "rotate-credential"],
                    },
                    new SecretReferenceCapability
                    {
                        Platforms = [ModuleCapabilityFixtures.Platform],
                        Secrets = [new("database-admin", "fixture-db", ["read", "rotate"])],
                    },
                ], ["fixture.service"]);
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
            var packageConsent = await consents.PrepareAsync("session", "granting-user",
                new("fixture", ModuleCapabilityCatalog.PackageManage, "install", "mysql-server",
                    "Background package install", clock.Current.AddHours(1), "package-consent"));
            packageConsent = await consents.ReadAsync(packageConsent.ConsentId, "session");
            check(packageConsent.State == "AwaitingConfirmation" && packageConsent.ContentSha256 is null,
                "package install/update/repair can be reviewed as a standing permission");
            await Reject(() => consents.PrepareAsync("session", "granting-user",
                    new("fixture", ModuleCapabilityCatalog.PackageManage, "remove", "mysql-server",
                        "Background package removal", clock.Current.AddHours(1), "package-remove-consent")),
                "unsupported", "package removal still requires a fresh explicit confirmation");
            await Reject(() => consents.PrepareAsync("session", "granting-user",
                    new("fixture", ModuleCapabilityCatalog.FileManage, "delete", "/etc/fixture/app.conf",
                        "Background configuration deletion", clock.Current.AddHours(1), "file-delete-consent")),
                "unsupported", "file deletion still requires a fresh explicit confirmation");
            await Reject(() => consents.PrepareAsync("session", "granting-user",
                    new("fixture", ModuleCapabilityCatalog.DataManage, "delete", "/var/lib/fixture",
                        "Background data deletion", clock.Current.AddHours(1), "data-delete-consent")),
                "unsupported", "data deletion can never be authorized by a standing permission");
            await Reject(() => consents.PrepareAsync("session", "granting-user",
                    new("fixture", ModuleCapabilityCatalog.DataManage, "backup", "/var/lib/fixture",
                        "Background data backup", clock.Current.AddHours(1), "data-backup-consent")),
                "unsupported", "data backup still requires a fresh explicit confirmation");
            await Reject(() => consents.PrepareAsync("session", "granting-user",
                    new("fixture", ModuleCapabilityCatalog.ApplicationManage, "rotate-credential", "fixture-db",
                        "Background credential rotation", clock.Current.AddHours(1), "app-rotate-consent")),
                "unsupported", "credential rotation still requires a fresh explicit confirmation");
            await Reject(() => consents.PrepareAsync("session", "granting-user",
                    new("fixture", ModuleCapabilityCatalog.PackageManage, "install", "mysql-server",
                        "Digest on a package", clock.Current.AddHours(1), "package-digest-consent",
                        new string('a', 64))),
                "consent_invalid", "only file write may bind a content digest");
            var content = "fixture configuration\n";
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            var fileConsent = await consents.PrepareAsync("session", "granting-user",
                new("fixture", ModuleCapabilityCatalog.FileManage, "write", "/etc/fixture/app.conf",
                    "Background configuration publication", clock.Current.AddHours(1), "file-consent", digest));
            fileConsent = await consents.ReadAsync(fileConsent.ConsentId, "session");
            check(fileConsent.State == "AwaitingConfirmation" && fileConsent.ContentSha256 == digest,
                "file write standing permission binds the exact content digest");
            var preserveConsent = await consents.PrepareAsync("session", "granting-user",
                new("fixture", ModuleCapabilityCatalog.DataManage, "preserve", "/var/lib/fixture",
                    "Background data preservation", clock.Current.AddHours(1), "preserve-consent"));
            preserveConsent = await consents.ReadAsync(preserveConsent.ConsentId, "session");
            check(preserveConsent.State == "AwaitingConfirmation", "data preserve can be reviewed as a standing permission");
            var queryConsent = await consents.PrepareAsync("session", "granting-user",
                new("fixture", ModuleCapabilityCatalog.ApplicationManage, "query", "fixture-db",
                    "Background database query", clock.Current.AddHours(1), "query-consent"));
            queryConsent = await consents.ReadAsync(queryConsent.ConsentId, "session");
            check(queryConsent.State == "AwaitingConfirmation", "read-only application query can be reviewed as a standing permission");
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
            var activeFile = await consents.ApproveAsync(fileConsent.ConsentId, "session", Approval(fileConsent), Verify);
            check(activeFile.State == "Active" && activeFile.ContentSha256 == digest,
                "file permission retains the exact content digest after administrator approval");
            var fileExecutions = 0;
            var fileStatus = await consents.ExecuteAsync(activeFile.ConsentId, "fixture",
                new ModulePrivilegeRequest(ModuleCapabilityCatalog.FileManage, "write", "/etc/fixture/app.conf",
                    "Scheduled configuration publication", "file-match",
                    ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(content))),
                _ =>
                {
                    fileExecutions++;
                    return Task.FromResult(new ModulePrivilegeStatus(Guid.NewGuid(), "Succeeded", null, DateTimeOffset.UtcNow));
                });
            check(fileStatus.State == "Succeeded" && fileExecutions == 1,
                "background file write runs only when its bytes match the approved content digest");
            await Reject(() => consents.ExecuteAsync(activeFile.ConsentId, "fixture",
                    new ModulePrivilegeRequest(ModuleCapabilityCatalog.FileManage, "write", "/etc/fixture/app.conf",
                        "Changed configuration", "file-mismatch",
                        ContentBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("different configuration\n"))),
                    _ => Task.FromResult(new ModulePrivilegeStatus(Guid.NewGuid(), "Succeeded", null, DateTimeOffset.UtcNow))),
                "confirmation_required", "a changed file body cannot reuse the standing permission");
            await Reject(() => consents.ExecuteAsync(activeFile.ConsentId, "fixture",
                    new ModulePrivilegeRequest(ModuleCapabilityCatalog.FileManage, "write", "/etc/fixture/app.conf",
                        "Missing configuration", "file-missing"),
                    _ => Task.FromResult(new ModulePrivilegeStatus(Guid.NewGuid(), "Succeeded", null, DateTimeOffset.UtcNow))),
                "confirmation_required", "a file write without a body cannot reuse the standing permission");
            check(fileExecutions == 1, "mismatched or missing file content never reaches dispatch");
            var activePackage = await consents.ApproveAsync(packageConsent.ConsentId, "session", Approval(packageConsent), Verify);
            var activePreserve = await consents.ApproveAsync(preserveConsent.ConsentId, "session", Approval(preserveConsent), Verify);
            var activeQuery = await consents.ApproveAsync(queryConsent.ConsentId, "session", Approval(queryConsent), Verify);
            var categoryExecutions = 0;
            async Task CategoryBackground(Guid consentId, ModulePrivilegeRequest request)
            {
                await consents.ExecuteAsync(consentId, "fixture", request, authority =>
                {
                    categoryExecutions++;
                    return Task.FromResult(new ModulePrivilegeStatus(Guid.NewGuid(), "Succeeded", null, DateTimeOffset.UtcNow));
                });
            }
            await CategoryBackground(activePackage.ConsentId, new(ModuleCapabilityCatalog.PackageManage, "install",
                "mysql-server", "Background package install", "package-background"));
            await CategoryBackground(activePreserve.ConsentId, new(ModuleCapabilityCatalog.DataManage, "preserve",
                "/var/lib/fixture", "Background data preservation", "preserve-background"));
            await CategoryBackground(activeQuery.ConsentId, new(ModuleCapabilityCatalog.ApplicationManage, "query",
                "fixture-db", "Background database query", "query-background",
                [new("database-admin", "fixture-db", "read")]));
            check(categoryExecutions == 3,
                "active standing permissions dispatch package, data-preserve and read-only application operations");
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
            var race = await Prepare("revoke-race");
            await consents.ApproveAsync(race.ConsentId, "session", Approval(race), Verify);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = consents.ExecuteAsync(race.ConsentId, "fixture",
                new ModulePrivilegeRequest(ModuleCapabilityCatalog.ServiceManage, "restart", "fixture.service",
                    "Scheduled maintenance", "revoke-race-op"), async _ =>
            {
                entered.SetResult();
                await release.Task;
                return new ModulePrivilegeStatus(Guid.NewGuid(), "Succeeded", null, DateTimeOffset.UtcNow);
            });
            await entered.Task;
            var revocation = consents.RevokeAsync(race.ConsentId, "another-host-administrator");
            check(!revocation.IsCompleted, "revocation waits for the already dispatched background action");
            release.SetResult();
            check((await inFlight).State == "Succeeded" && (await revocation).State == "Revoked",
                "in-flight dispatch is not misreported as cancelled and revocation commits afterwards");
            await Reject(() => Execute(race.ConsentId, "after-race"), "confirmation_required",
                "a completed revocation rejects every later background dispatch");
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
            JsonNode StoredConsent(string json, Guid id) => JsonNode.Parse(json)!["consents"]!.AsArray()
                .Single(node => node!["id"]!.GetValue<Guid>() == id)!;
            var changed = JsonNode.Parse(original)!;
            changed["consents"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == active.ConsentId)!
                ["request"]!["target"] = "outside.service";
            File.WriteAllText(file, changed.ToJsonString());
            await Reject(() => consents.ListAsync(), "recovery_required", "live standing consent detects persisted state changed behind its owner");
            try { _ = new ModulePrivilegeConsentStore(root, Resolve, clock); throw new Exception("corrupt state accepted"); }
            catch (ModulePrivilegeException error) when (error.Code == "module.privilege_recovery_required") { check(true, "standing consent recovery rechecks the frozen request and module hashes"); }
            File.WriteAllText(file, original);
            var audit = StoredConsent(original, active.ConsentId);
            check(audit["actor"]!.GetValue<string>() == "granting-user" && audit["administrator"]!["identity"]!.GetValue<string>() == "linux:1000"
                && audit["approvedAt"] is not null && audit["revokedBy"]!.GetValue<string>() == "another-host-administrator" && audit["revokedAt"] is not null,
                "standing consent records granting user, actual administrator, approval and revocation evidence without credentials");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
