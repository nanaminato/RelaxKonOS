using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Startup;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using System.ServiceProcess;
using TimeoutException = System.TimeoutException;

internal static class WindowsCoreAcceptance
{
    internal static async Task RunAsync(NativeConfiguration native, string password, Action<bool, string> check)
    {
        var work = Path.GetDirectoryName(native.ClientReport)!;
        var root = Path.Combine(work, "core");
        var publish = Path.Combine(work, "core-fixture");
        Directory.CreateDirectory(publish);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "HostFixture.dll"), Path.Combine(publish, "HostFixture.dll"));
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(work, "temporary-signing.pem");
        ModulePackageBuildResult package;
        try
        {
            await File.WriteAllTextAsync(keyPath, signing.ExportPkcs8PrivateKeyPem());
            package = await ModulePackageBuilder.BuildAsync(new ModulePackageBuildOptions(publish, Path.Combine(work, "signed-package"),
                "fixture-broker", "1.0.0", "fixture-broker-1", "fixture", "third-party", RuntimeInformation.RuntimeIdentifier,
                "HostFixture.dll", "HostFixture.FixtureBrokerHostEntry", "native-test", new string('a', 40), "native-test", 1,
                "https://modules.example/", keyPath, "native-test", 3600, HostApiMinimum: "2.0.0", HostApiMaximumExclusive: "3.0.0",
                Runtime: "module-host", Privileges: [new ServiceManageCapability
                {
                    Platforms = ["windows"],
                    Services = [new(native.ActionServiceName, ["restart"])],
                }]), DateTimeOffset.UtcNow);
        }
        finally { File.Delete(keyPath); }
        var policy = new ModulePackageTrustPolicy(1, "native-test", 1, true, "fixture", "third-party", "https://modules.example/",
            ["fixture-broker"], new Dictionary<string, string> { ["native-test"] = package.PublicKeySpkiBase64 }, 3600);
        var configuration = new JsonObject
        {
            ["Server"] = new JsonObject { ["Mode"] = "system" },
            ["Storage"] = new JsonObject { ["Provider"] = "sqlite", ["DatabasePath"] = Path.Combine(root, "data", "identity.db") },
            ["Jwt"] = new JsonObject { ["Secret"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) },
            ["Guardian"] = new JsonObject { ["Enabled"] = false },
            ["Privileges"] = new JsonObject { ["Backend"] = "helper" },
            ["PrivilegedHelper"] = new JsonObject { ["PipeName"] = native.PipeName, ["SharedSecret"] = native.SharedSecret, ["UserExecutionBackend"] = "disabled" },
            ["Observability"] = new JsonObject { ["InstanceId"] = "native-v2-test", ["LogDirectory"] = Path.Combine(root, "data", "logs"),
                ["AuditDatabasePath"] = Path.Combine(root, "data", "audit.db"), ["AuditRetentionDays"] = 365,
                ["AuditHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
            ["ModuleManagement"] = new JsonObject { ["Authority"] = "managed", ["Administrators"] = new JsonArray() },
            ["ModulePackages"] = new JsonObject { ["Sources"] = new JsonObject { ["native-test"] = JsonSerializer.SerializeToNode(policy, ModulePackageJson.Options) } },
            ["ModulePrivileges"] = new JsonObject { ["Services"] = new JsonArray(native.ActionServiceName) },
            ["ModuleHosts"] = new JsonObject { ["ProbeIntervalSeconds"] = 1 },
        };
        var configurationPath = Path.Combine(root, "appsettings.host.json");
        async Task Save() => await File.WriteAllTextAsync(configurationPath, configuration.ToJsonString());
        await Save();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(45) };
        Process? server = null;
        async Task Stop()
        {
            if (server is null) return;
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            server.Dispose(); server = null;
            http.DefaultRequestHeaders.Authorization = null;
            var hostLock = Path.Combine(root, "data", "module-management", "host.lock");
            for (var attempt = 0; File.Exists(hostLock); attempt++)
            {
                try { using var released = new FileStream(hostLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 100) { await Task.Delay(50); }
            }
        }
        async Task Start()
        {
            var info = new ProcessStartInfo(Path.Combine(native.ServerDirectory, "RelaxKonOS.Server.exe"))
                { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            info.Environment["DOTNET_ENVIRONMENT"] = "Production";
            info.Environment["ASPNETCORE_URLS"] = http.BaseAddress!.ToString();
            server = Process.Start(info) ?? throw new IOException("Acceptance Server did not start.");
            _ = server.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = server.StandardError.BaseStream.CopyToAsync(Stream.Null);
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (server.HasExited) throw new IOException("Acceptance Server exited with code " + server.ExitCode);
                try { using var health = await http.GetAsync("/healthz"); if (health.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(500);
            }
            throw new TimeoutException("Acceptance Server did not become healthy.");
        }
        async Task<JsonNode> Api(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK, byte[]? binary = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (binary is not null) { request.Content = new ByteArrayContent(binary); request.Content.Headers.ContentType = new("application/octet-stream"); }
            using var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != expected)
            {
                string? problem = null;
                try { problem = JsonNode.Parse(text)?["problemCode"]?.GetValue<string>(); } catch (JsonException) { }
                if (problem is null || problem.Length > 128 || !problem.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) problem = "unspecified";
                throw new InvalidOperationException($"Core acceptance HTTP {(int)response.StatusCode} {problem} at {path}");
            }
            try { return JsonNode.Parse(text) ?? new JsonObject(); }
            catch (JsonException) { return JsonValue.Create(text)!; }
        }
        async Task<string> Login()
        {
            http.DefaultRequestHeaders.Authorization = null;
            var login = await Api(HttpMethod.Post, "/api/v1.0/auth/login", new { identifier = native.AdministratorName, password,
                clientPlatform = "windows", deviceName = "Native v2 Core acceptance", clientVersion = "0.2.2" });
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login["tokens"]!["accessToken"]!.GetValue<string>());
            return login["user"]!["id"]!.GetValue<string>();
        }
        try
        {
            await Start();
            var userId = await Login();
            check(true, "full Windows Core HTTP login verifies the temporary native administrator account");
            await Stop();
            configuration["ModuleManagement"]!["Administrators"] = new JsonArray(userId);
            await Save();
            var serviceId = ModuleManagementStore.ReadRecoveryReport(root).ServiceId;
            using (var catalogs = new ModulePackageCatalogStore(root, serviceId))
                catalogs.Accept(await File.ReadAllBytesAsync(package.CatalogPath), policy,
                    new(serviceId, RuntimeInformation.RuntimeIdentifier, "1.0.0", false), "uninitialized", "native-acceptance", "initial", DateTimeOffset.UtcNow);
            await Start(); await Login();
            var upload = await Api(HttpMethod.Post, "/api/v1.0/server/module-packages/upload", binary: await File.ReadAllBytesAsync(package.ArchivePath));
            var installation = await Api(HttpMethod.Post, "/api/v1.0/server/module-packages/plans", new { sourceId = "native-test", releaseId = "fixture-broker-1", uploadId = upload["uploadId"]!.GetValue<string>() });
            check(installation["runtime"]!.GetValue<string>() == "module-host" && installation["privileges"]![0]!["target"]!.GetValue<string>() == native.ActionServiceName,
                "Windows Core v2 install plan exposes the exact isolated runtime and declared service target");
            await Api(HttpMethod.Post, "/api/v1.0/server/module-packages/plans/" + installation["planId"]!.GetValue<string>() + "/apply");
            await Stop(); await Start(); await Login();
            var inventory = (await Api(HttpMethod.Get, "/api/v1.0/server/modules")).AsArray();
            var fact = inventory.Single(item => item!["id"]!.GetValue<string>() == "fixture-broker")!;
            var desired = inventory.Where(item => item!["desiredEnabled"]!.GetValue<bool>()).Select(item => item!["id"]!.GetValue<string>())
                .Append("fixture-broker").Distinct().Order().ToArray();
            var enable = await Api(HttpMethod.Post, "/api/v1.0/server/module-plans", new { serviceId = fact["serviceId"]!.GetValue<string>(), action = "Enable",
                moduleIds = new[] { "fixture-broker" }, enabledModuleIds = desired, expectedRevision = fact["desiredRevision"]!.GetValue<string>(), idempotencyKey = Guid.NewGuid().ToString("N") });
            check(enable["blockedBy"]!.AsArray().Count == 0, "Windows managed module enable plan admits the signed isolated package");
            await Api(HttpMethod.Post, "/api/v1.0/server/module-plans/" + enable["planId"]!.GetValue<string>() + "/apply");
            await Stop(); await Start(); await Login();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var hosts = (await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts"))["hosts"]!.AsArray();
                if (hosts.Any(host => host!["moduleId"]!.GetValue<string>() == "fixture-broker" && host["status"]!.GetValue<string>() == "running")) break;
                if (attempt == 39) throw new TimeoutException("Core managed ModuleHost did not become ready.");
                await Task.Delay(500);
            }
            check((await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts/fixture-broker/hello")).GetValue<string>() == "hello from module host",
                "the Windows production supervisor starts the installed package and the authenticated Core proxy reaches it");
            var prepared = await Api(HttpMethod.Post, "/api/v1.0/server/module-hosts/fixture-broker/privilege", new { kind = "service.manage", action = "restart",
                target = native.ActionServiceName, reason = "Full Windows Core acceptance", idempotencyKey = Guid.NewGuid().ToString("N") });
            var planPath = "/api/v1.0/server/module-privileges/plans/" + prepared["planId"]!.GetValue<string>();
            var plan = await Api(HttpMethod.Get, planPath);
            var pidPath = Path.Combine(Path.GetDirectoryName(native.WorkerReport)!, "action-pid.txt");
            var before = await File.ReadAllTextAsync(pidPath);
            await Api(HttpMethod.Post, planPath + "/approve", new { planHash = plan["planHash"]!.GetValue<string>(), confirmationToken = plan["confirmationToken"]!.GetValue<string>(),
                administratorUsername = native.AdministratorName, password = "invalid-core-test-password" }, HttpStatusCode.Forbidden);
            check(await File.ReadAllTextAsync(pidPath) == before, "Core HTTP rejects a bad administrator password without changing the real service PID");
            var approval = new { planHash = plan["planHash"]!.GetValue<string>(), confirmationToken = plan["confirmationToken"]!.GetValue<string>(), administratorUsername = native.AdministratorName, password };
            var result = await Api(HttpMethod.Post, planPath + "/approve", approval);
            check(result["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != before,
                "Core HTTP explicit confirmation verifies the real administrator and restarts the actual Windows service through Helper");
            var after = await File.ReadAllTextAsync(pidPath);
            await Api(HttpMethod.Post, planPath + "/approve", approval, HttpStatusCode.Conflict);
            check(await File.ReadAllTextAsync(pidPath) == after, "Core HTTP approval replay cannot repeat the real Windows service action");
            async Task<(string Path, JsonNode Plan)> FreshPlan()
            {
                var next = await Api(HttpMethod.Post, "/api/v1.0/server/module-hosts/fixture-broker/privilege", new { kind = "service.manage", action = "restart",
                    target = native.ActionServiceName, reason = "SessionValid policy acceptance", idempotencyKey = Guid.NewGuid().ToString("N") });
                var path = "/api/v1.0/server/module-privileges/plans/" + next["planId"]!.GetValue<string>();
                return (path, await Api(HttpMethod.Get, path));
            }
            object Consent(JsonNode value, string? credential = null) => new
            {
                planHash = value["planHash"]!.GetValue<string>(), confirmationToken = value["confirmationToken"]!.GetValue<string>(),
                administratorUsername = native.AdministratorName, password = credential,
            };
            async Task SetSessionValid(bool enabled)
            {
                var settings = await Api(HttpMethod.Get, "/api/v1.0/server/runtime-settings");
                var saved = await Api(HttpMethod.Put, "/api/v1.0/server/runtime-settings", new
                {
                    instanceId = settings["instanceId"]!.GetValue<string>(), name = "sessionValid", value = enabled,
                    expectedRevision = settings["revision"]!.GetValue<long>(),
                });
                if (saved["settings"]!["sessionValid"]!.GetValue<bool>() != enabled) throw new InvalidOperationException("Runtime policy did not change.");
            }
            var remembered = await FreshPlan();
            check(await File.ReadAllTextAsync(pidPath) == after, "remembered administrator verification cannot execute a newly prepared plan without confirmation");
            var rememberedResult = await Api(HttpMethod.Post, remembered.Path + "/approve", Consent(remembered.Plan));
            check(rememberedResult["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != after,
                "SessionValid enabled reuses verified identity only after a fresh explicit Core HTTP confirmation");
            await SetSessionValid(false);
            var disabled = await FreshPlan();
            var beforeDisabled = await File.ReadAllTextAsync(pidPath);
            await Api(HttpMethod.Post, disabled.Path + "/approve", Consent(disabled.Plan), HttpStatusCode.Forbidden);
            check(await File.ReadAllTextAsync(pidPath) == beforeDisabled, "turning SessionValid off immediately rejects previously remembered administrator authority");
            var disabledResult = await Api(HttpMethod.Post, disabled.Path + "/approve", Consent(disabled.Plan, password));
            check(disabledResult["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != beforeDisabled,
                "SessionValid disabled still permits an explicitly confirmed action with fresh real administrator credentials");
            var unremembered = await FreshPlan();
            var beforeUnremembered = await File.ReadAllTextAsync(pidPath);
            await Api(HttpMethod.Post, unremembered.Path + "/approve", Consent(unremembered.Plan), HttpStatusCode.Forbidden);
            check(await File.ReadAllTextAsync(pidPath) == beforeUnremembered, "a successful password verification is not remembered while SessionValid remains disabled");
            await Api(HttpMethod.Post, unremembered.Path + "/cancel");
            await SetSessionValid(true);
            var reenabled = await FreshPlan();
            await Api(HttpMethod.Post, reenabled.Path + "/approve", Consent(reenabled.Plan), HttpStatusCode.Forbidden);
            check(await File.ReadAllTextAsync(pidPath) == beforeUnremembered, "reenabling SessionValid never resurrects administrator authority cleared by a policy change");
            await Api(HttpMethod.Post, reenabled.Path + "/cancel");
            await SetSessionValid(false);
            const string consentsRoot = "/api/v1.0/server/module-privileges/consents";
            async Task<JsonNode> Schedule(Guid consentId, string key)
            {
                var scheduled = await Api(HttpMethod.Post, "/api/v1.0/server/module-hosts/fixture-broker/schedule-background", new
                {
                    consentId, operation = new { kind = "service.manage", action = "restart", target = native.ActionServiceName, reason = "Native standing consent acceptance", idempotencyKey = key },
                });
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var status = await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts/fixture-broker/background-results/" + scheduled["jobId"]!.GetValue<string>());
                    if (status["state"]!.GetValue<string>() != "Scheduled") return status;
                    await Task.Delay(500);
                }
                throw new TimeoutException("Background consent fixture did not finish.");
            }
            var beforeStanding = await File.ReadAllTextAsync(pidPath);
            var noConsent = await Schedule(Guid.NewGuid(), Guid.NewGuid().ToString("N"));
            check(noConsent["state"]!.GetValue<string>() == "Rejected" && await File.ReadAllTextAsync(pidPath) == beforeStanding,
                "real Windows background SDK work without a standing consent never dispatches a service action");
            var pendingConsent = await Api(HttpMethod.Post, consentsRoot, new
            {
                moduleId = "fixture-broker", kind = "service.manage", action = "restart", target = native.ActionServiceName,
                reason = "Approve scheduled fixture maintenance", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), idempotencyKey = Guid.NewGuid().ToString("N"),
            });
            var standingPath = consentsRoot + "/" + pendingConsent["consentId"]!.GetValue<string>();
            var standingPlan = await Api(HttpMethod.Get, standingPath);
            await Api(HttpMethod.Post, standingPath + "/approve", Consent(standingPlan, "incorrect-standing-consent-password"), HttpStatusCode.Forbidden);
            check(await File.ReadAllTextAsync(pidPath) == beforeStanding, "standing consent rejects a real incorrect administrator password without dispatch");
            await Api(HttpMethod.Post, standingPath + "/approve", Consent(standingPlan, password));
            var standingId = Guid.Parse(pendingConsent["consentId"]!.GetValue<string>());
            var standingKey = Guid.NewGuid().ToString("N");
            var backgroundResult = await Schedule(standingId, standingKey);
            check(backgroundResult["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != beforeStanding,
                "a real scheduled Windows module task uses explicitly approved fixed-target consent after the business request ends");
            var afterStanding = await File.ReadAllTextAsync(pidPath);
            await Stop(); await Start(); await Login();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var hosts = (await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts"))["hosts"]!.AsArray();
                if (hosts.Any(host => host!["moduleId"]!.GetValue<string>() == "fixture-broker" && host["status"]!.GetValue<string>() == "running")) break;
                if (attempt == 39) throw new TimeoutException("Core ModuleHost did not resume for standing consent recovery.");
                await Task.Delay(500);
            }
            var repeatedBackground = await Schedule(standingId, standingKey);
            check(repeatedBackground["planId"]!.GetValue<string>() == backgroundResult["planId"]!.GetValue<string>()
                && await File.ReadAllTextAsync(pidPath) == afterStanding, "Core restart and repeated real background work return the original operation without another SCM restart");
            await using (var standingAudit = new SqliteConnection("Data Source=" + Path.Combine(root, "data", "audit.db") + ";Mode=ReadOnly"))
            {
                await standingAudit.OpenAsync(); await using var query = standingAudit.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM security_audit_entries WHERE operation_id=$operation AND actor_reference IS NOT NULL AND actor_reference<>''";
                query.Parameters.AddWithValue("$operation", backgroundResult["planId"]!.GetValue<string>());
                check(Convert.ToInt64(await query.ExecuteScalarAsync()) >= 2,
                    "real background Helper audit retains the original granting actor despite having no active user request");
            }
            await Api(HttpMethod.Post, standingPath + "/revoke");
            var revokedBackground = await Schedule(standingId, Guid.NewGuid().ToString("N"));
            check(revokedBackground["state"]!.GetValue<string>() == "Rejected" && await File.ReadAllTextAsync(pidPath) == afterStanding,
                "revoking real standing consent immediately blocks later Windows background work");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            var expiryPending = await Api(HttpMethod.Post, consentsRoot, new
            {
                moduleId = "fixture-broker", kind = "service.manage", action = "restart", target = native.ActionServiceName,
                reason = "Actual standing consent deadline acceptance", expiresAt = deadline, idempotencyKey = Guid.NewGuid().ToString("N"),
            });
            var expiryPath = consentsRoot + "/" + expiryPending["consentId"]!.GetValue<string>();
            var expiryPlan = await Api(HttpMethod.Get, expiryPath);
            await Api(HttpMethod.Post, expiryPath + "/approve", Consent(expiryPlan, password));
            var remaining = deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            var expiredBackground = await Schedule(Guid.Parse(expiryPending["consentId"]!.GetValue<string>()), Guid.NewGuid().ToString("N"));
            check(expiredBackground["state"]!.GetValue<string>() == "Rejected" && await File.ReadAllTextAsync(pidPath) == afterStanding,
                "the real Windows standing consent deadline rejects later work without another SCM action");
            await Api(HttpMethod.Post, expiryPath + "/revoke");
            using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            const string ownerRoot = "/api/v1.0/auth/owner-devices";
            var enrollment = await Api(HttpMethod.Post, ownerRoot + "/bootstrap", new
            {
                deviceName = "Native v2 Core acceptance", platform = "windows", clientVersion = "0.2.2",
                publicKeySpki = Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo()),
            }, HttpStatusCode.Created);
            var ownerDevice = enrollment["id"]!.GetValue<string>();
            static byte[] Nonce(JsonNode challenge)
            {
                var value = challenge["nonce"]!.GetValue<string>().Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
            }
            var badChallenge = await Api(HttpMethod.Post, ownerRoot + "/challenge", new { deviceId = ownerDevice });
            using (var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                await Api(HttpMethod.Post, ownerRoot + "/sign-in", new
                {
                    challengeId = badChallenge["challengeId"]!.GetValue<string>(), deviceId = ownerDevice,
                    signature = Convert.ToBase64String(wrongKey.SignData(Nonce(badChallenge), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
                }, HttpStatusCode.Unauthorized);
            check(true, "owner-device HTTP sign-in rejects proof made by a different actual P-256 key");
            var challenge = await Api(HttpMethod.Post, ownerRoot + "/challenge", new { deviceId = ownerDevice });
            var proof = new
            {
                challengeId = challenge["challengeId"]!.GetValue<string>(), deviceId = ownerDevice,
                signature = Convert.ToBase64String(ownerKey.SignData(Nonce(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
            };
            var ownerLogin = await Api(HttpMethod.Post, ownerRoot + "/sign-in", proof);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerLogin["tokens"]!["accessToken"]!.GetValue<string>());
            check(true, "a real owner-device nonce signature obtains a controller session through the Windows Core HTTP endpoint");
            await Api(HttpMethod.Post, ownerRoot + "/sign-in", proof, HttpStatusCode.Unauthorized);
            check(true, "a consumed owner-device proof cannot be replayed for another login");
            var ownerBefore = await File.ReadAllTextAsync(pidPath);
            var ownerPlan = await FreshPlan();
            check(await File.ReadAllTextAsync(pidPath) == ownerBefore, "owner-device authority cannot execute a prepared Broker plan without explicit confirmation");
            var ownerResult = await Api(HttpMethod.Post, ownerPlan.Path + "/approve", Consent(ownerPlan.Plan));
            check(ownerResult["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != ownerBefore,
                "verified owner-device confirmation executes the real Helper action without a password even when SessionValid memory is disabled");
            var persistedText = await File.ReadAllTextAsync(Path.Combine(root, "data", "module-management", "privilege-plans.json"));
            var persisted = JsonNode.Parse(persistedText)!;
            var completed = persisted["plans"]!.AsArray().Where(item => item!["state"]!.GetValue<string>() == "Succeeded").ToArray();
            var administratorSid = ((SecurityIdentifier)new NTAccount(Environment.MachineName, native.AdministratorName).Translate(typeof(SecurityIdentifier))).Value;
            check(persisted["schemaVersion"]!.GetValue<int>() == 4 && completed.Length >= 4
                && completed.All(item => item!["audit"]!["initiatingUser"]!.GetValue<string>() == userId
                    && item["audit"]!["approvingUser"]!.GetValue<string>() == userId
                    && item["audit"]!["confirmedAt"] is not null && item["audit"]!["attemptCompletedAt"] is not null)
                && completed.Any(item => item!["audit"]!["administrator"]!["identity"]!.GetValue<string>() == "windows:" + administratorSid)
                && completed.Any(item => item!["audit"]!["administrator"]!["identity"]!.GetValue<string>() == "owner-device:" + ownerDevice)
                && !persistedText.Contains(password, StringComparison.Ordinal),
                "real Core durable audit records native administrator and owner-device identities, users and timestamps without credentials");
            await using (var auditDatabase = new SqliteConnection("Data Source=" + Path.Combine(root, "data", "audit.db") + ";Mode=ReadOnly"))
            {
                await auditDatabase.OpenAsync();
                await using var query = auditDatabase.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM security_audit_entries WHERE operation_id=$operation AND actor_reference IS NOT NULL AND actor_reference<>''";
                query.Parameters.AddWithValue("$operation", ownerPlan.Plan["planId"]!.GetValue<string>());
                check(Convert.ToInt64(await query.ExecuteScalarAsync()) >= 2,
                    "real Windows audit database durably attributes Helper accepted and completed records to the initiating actor");
            }
            var interrupted = await FreshPlan();
            var originalAuthorization = http.DefaultRequestHeaders.Authorization;
            var delayMarker = Path.Combine(work, "service-stop-delay.flag");
            var beforeCrash = await File.ReadAllTextAsync(pidPath);
            await File.WriteAllTextAsync(delayMarker, "fixed test-only SCM stop delay");
            try
            {
                var inFlight = Api(HttpMethod.Post, interrupted.Path + "/approve", Consent(interrupted.Plan));
                using var fixture = new ServiceController(native.ActionServiceName);
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    fixture.Refresh();
                    if (fixture.Status == ServiceControllerStatus.StopPending) break;
                    if (attempt == 99) throw new TimeoutException("Actual SCM action did not enter StopPending.");
                    await Task.Delay(50);
                }
                var inFlightDocument = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "data", "module-management", "privilege-plans.json")))!;
                var durable = inFlightDocument["plans"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == interrupted.Plan["planId"]!.GetValue<string>())!;
                check(durable["state"]!.GetValue<string>() == "Executing",
                    "Windows Core durably records Executing while the actual SCM service is StopPending");
                await Stop();
                var connectionLost = false;
                try { await inFlight.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException) { connectionLost = true; }
                check(connectionLost, "terminating the real Windows Core interrupts its in-flight HTTP approval connection");
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    fixture.Refresh();
                    if (fixture.Status == ServiceControllerStatus.Running && await File.ReadAllTextAsync(pidPath) != beforeCrash) break;
                    if (attempt == 99) throw new TimeoutException("Independent Helper did not finish the accepted SCM restart.");
                    await Task.Delay(200);
                }
            }
            finally { File.Delete(delayMarker); }
            var afterCrash = await File.ReadAllTextAsync(pidPath);
            await Start();
            http.DefaultRequestHeaders.Authorization = originalAuthorization;
            var recovery = await Api(HttpMethod.Get, interrupted.Path);
            check(recovery["state"]!.GetValue<string>() == "RecoveryRequired" && recovery["confirmationToken"]!.GetValue<string>() == "",
                "a fresh Windows Core recovers its interrupted plan as unknown without issuing a new confirmation token");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Api(HttpMethod.Get, interrupted.Path);
                check(await File.ReadAllTextAsync(pidPath) == afterCrash, "querying the recovered Windows plan does not repeat the real SCM restart");
            }
            using (var replay = await http.PostAsJsonAsync(interrupted.Path + "/approve", Consent(interrupted.Plan)))
                check(replay.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict,
                    "the old Windows approval is rejected after a real Core crash");
            check(await File.ReadAllTextAsync(pidPath) == afterCrash, "rejected Windows crash-recovery consent leaves the native PID unchanged");
            var recoveredDocument = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "data", "module-management", "privilege-plans.json")))!;
            var recoveredAudit = recoveredDocument["plans"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == interrupted.Plan["planId"]!.GetValue<string>())!["audit"]!;
            check(recoveredAudit["initiatingUser"]!.GetValue<string>() == userId
                && recoveredAudit["approvingUser"]!.GetValue<string>() == userId
                && recoveredAudit["administrator"]!["identity"]!.GetValue<string>() == "owner-device:" + ownerDevice
                && recoveredAudit["confirmedAt"] is not null && recoveredAudit["attemptCompletedAt"] is null,
                "Windows crash recovery preserves the actual approver evidence without inventing an observed completion timestamp");
            await using (var crashAudit = new SqliteConnection("Data Source=" + Path.Combine(root, "data", "audit.db") + ";Mode=ReadOnly"))
            {
                await crashAudit.OpenAsync();
                await using var query = crashAudit.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM security_audit_entries WHERE operation_id=$operation AND event_id=1300";
                query.Parameters.AddWithValue("$operation", interrupted.Plan["planId"]!.GetValue<string>());
                check(Convert.ToInt64(await query.ExecuteScalarAsync()) == 1,
                    "the recovered Windows operation has exactly one durable Helper acceptance after queries and rejected replay");
            }
            await Login();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var hosts = (await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts"))["hosts"]!.AsArray();
                if (hosts.Any(host => host!["moduleId"]!.GetValue<string>() == "fixture-broker" && host["status"]!.GetValue<string>() == "running")) break;
                if (attempt == 39) throw new TimeoutException("Managed ModuleHost did not return after Core crash.");
                await Task.Delay(500);
            }
            var afterRecovery = await FreshPlan();
            var recoveredResult = await Api(HttpMethod.Post, afterRecovery.Path + "/approve", Consent(afterRecovery.Plan, password));
            check(recoveredResult["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != afterCrash,
                "fresh native login and new explicit confirmation permit a new real Windows action after Core crash recovery");
            var helperPlan = await FreshPlan();
            var helperPid = JsonNode.Parse(await File.ReadAllTextAsync(native.WorkerReport))!["processId"]!.GetValue<int>();
            var beforeHelperCrash = await File.ReadAllTextAsync(pidPath);
            var helperMarker = Path.Combine(work, "helper-stop-pending-crash.flag");
            await File.WriteAllTextAsync(delayMarker, "fixed test-only SCM stop delay");
            await File.WriteAllTextAsync(helperMarker, "terminate only the test Helper itself during StopPending");
            try
            {
                var unknown = await Api(HttpMethod.Post, helperPlan.Path + "/approve", Consent(helperPlan.Plan, password));
                check(unknown["state"]!.GetValue<string>() == "RecoveryRequired",
                    "terminating the actual LocalSystem Helper during SCM stop leaves Core with an unknown result");
                using var target = new ServiceController(native.ActionServiceName);
                using var helperService = new ServiceController(native.ServiceName);
                for (var attempt = 0; attempt < 150; attempt++)
                {
                    target.Refresh(); helperService.Refresh();
                    var restarted = false;
                    try
                    {
                        var report = JsonNode.Parse(await File.ReadAllTextAsync(native.WorkerReport))!;
                        restarted = report["ready"]!.GetValue<bool>() && report["processId"]!.GetValue<int>() != helperPid;
                    }
                    catch (Exception error) when (error is IOException or JsonException) { }
                    if (target.Status == ServiceControllerStatus.Stopped && helperService.Status == ServiceControllerStatus.Running && restarted) break;
                    if (attempt == 149) throw new TimeoutException("SCM did not restore the test Helper or complete the partial service stop.");
                    await Task.Delay(200);
                }
                check(await File.ReadAllTextAsync(pidPath) == beforeHelperCrash,
                    "Helper failure after StopPending leaves the actual target stopped rather than silently completing its restart");
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var observed = await Api(HttpMethod.Get, helperPlan.Path);
                    target.Refresh();
                    check(observed["state"]!.GetValue<string>() == "RecoveryRequired" && target.Status == ServiceControllerStatus.Stopped,
                        "Helper SCM recovery and repeated Core queries never replay the partially executed Windows restart");
                }
                await Api(HttpMethod.Post, helperPlan.Path + "/approve", Consent(helperPlan.Plan, password), HttpStatusCode.Conflict);
            }
            finally { File.Delete(delayMarker); File.Delete(helperMarker); }
            await Stop(); await Start(); await Login();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var hosts = (await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts"))["hosts"]!.AsArray();
                if (hosts.Any(host => host!["moduleId"]!.GetValue<string>() == "fixture-broker" && host["status"]!.GetValue<string>() == "running")) break;
                if (attempt == 39) throw new TimeoutException("Managed ModuleHost did not return after test Helper recovery.");
                await Task.Delay(500);
            }
            var afterHelperRecovery = await FreshPlan();
            var helperRecoveredResult = await Api(HttpMethod.Post, afterHelperRecovery.Path + "/approve", Consent(afterHelperRecovery.Plan, password));
            check(helperRecoveredResult["state"]!.GetValue<string>() == "Succeeded" && await File.ReadAllTextAsync(pidPath) != beforeHelperCrash,
                "a new explicit confirmation restores the stopped fixture through the recovered real Windows Helper");
            await Api(HttpMethod.Post, "/api/v1.0/server/module-trust/fixture-broker", new { artifactSha256 = package.ArchiveSha256, manifestSha256 = package.ManifestSha256, trusted = false });
            await Api(HttpMethod.Get, "/api/v1.0/server/module-hosts/fixture-broker/hello", expected: HttpStatusCode.Forbidden);
            check(true, "Windows Core trust revocation immediately denies the installed module proxy");
        }
        finally { await Stop(); }
    }
}
