using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.ProxyServer;

var root = Path.Combine(Path.GetTempPath(), "relaxkonos-proxy-server-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new ProxyServerStore(new Paths(root), new EphemeralDataProtectionProvider());
    var helper = new Helper();
    var service = new ProxyServerService(store, helper, new TestMode(), new Engines());
    var realityId = Guid.NewGuid();
    var reality = new ProxyServerInbound(realityId, "Edge", ProxyServerProtocol.VlessReality, "127.0.0.1", 24443,
        "proxy.example.com", 24443, "proxy.example.com", HandshakeHost: "www.example.com", HandshakePort: 443);
    var reality2 = reality with { Id = Guid.NewGuid(), Name = "Edge 2", ListenPort = 24444, PublicPort = 24444 };
    var trojan = new ProxyServerInbound(Guid.NewGuid(), "Fallback", ProxyServerProtocol.Trojan, "127.0.0.1", 24445,
        "proxy.example.com", 24445, "proxy.example.com", CertificateId: Guid.NewGuid());

    Check(SingBoxConfiguration.Validate(new(0, [reality])).Valid, "Valid VLESS configuration");
    foreach (var host in new[] { "127.0.0.1", "169.254.169.254", "10.1.2.3", "::1", "fc00::1", "::ffff:127.0.0.1" })
        Check(!SingBoxConfiguration.PublicAddress(IPAddress.Parse(host)), "Reject internal handshake address " + host);
    Check(!SingBoxConfiguration.Validate(new(0, [reality, reality])).Valid, "Duplicate inbounds rejected");
    Check(SingBoxConfiguration.Validate(new(0, [reality], "http://subs.example.com")).Problems.Contains("proxy_server.subscription_base_invalid"),
        "A subscription host must be https");
    Check(SingBoxConfiguration.Validate(new(0, [trojan with { CertificateId = null }])).Problems.Contains("proxy_server.config_invalid"),
        "A certificate preset without a certificate is rejected");

    Check(await service.SaveAsync(new(0, [reality]), default) is null, "Save configuration");
    Check(await service.SaveAsync(new(0, [reality]), default) == "proxy_server.revision_conflict", "Optimistic revision conflict");

    // A principal is created with a null id; an id always addresses an existing record.
    Check(await service.SavePrincipalAsync(new(null, "Alice", "relaxkonos:alice"), default) is null, "Create principal");
    var alice = (await service.ReadAsync(default)).Principals.Single().Id;
    Check(await service.SavePrincipalAsync(new(alice, "Alice Chen", "relaxkonos:alice"), default) is null, "Update principal");
    Check((await service.ReadAsync(default)).Principals.Single().DisplayName == "Alice Chen", "Principal rename is stored");
    Check(await service.SavePrincipalAsync(new(null, "   ", null), default) == "proxy_server.principal_invalid", "Blank principal name rejected");
    Check(await service.SavePrincipalAsync(new(Guid.NewGuid(), "Ghost", null), default) == "proxy_server.principal_not_found",
        "Updating an unknown principal is refused");

    // A credential spans inbounds only while every one of them expects the same secret shape.
    Check(await service.SaveAsync(new(1, [reality, trojan]), default) is null, "Save a mixed-preset draft");
    Check(await service.SaveCredentialAsync(new(null, alice, "Mixed", [realityId, trojan.Id]), default) == "proxy_server.credential_invalid",
        "A credential never spans different secret shapes");
    Check(await service.SaveAsync(new(2, [reality, reality2]), default) is null, "Save a Reality-only draft");

    Check(await service.SaveCredentialAsync(new(null, Guid.NewGuid(), "Orphan", [realityId]), default) == "proxy_server.principal_not_found",
        "A credential requires an existing principal");
    Check(await service.SaveCredentialAsync(new(null, alice, "Empty", []), default) == "proxy_server.credential_invalid",
        "A credential needs at least one inbound");
    Check(await service.SaveCredentialAsync(new(null, alice, "Missing", [Guid.NewGuid()]), default) == "proxy_server.credential_invalid",
        "A credential cannot name an unknown inbound");
    Check(await service.SaveCredentialAsync(new(null, alice, "  ", [realityId]), default) == "proxy_server.credential_invalid",
        "Blank credential name rejected");
    Check(await service.SaveCredentialAsync(new(null, alice, "Phone", [realityId]), default) is null, "Create credential");
    var phone = (await service.ReadAsync(default)).Credentials.Single();
    Check(phone.PrincipalId == alice && phone.InboundIds.SequenceEqual([realityId]), "Credential metadata");
    Check((await service.ReadAsync(default)).Principals.Single().CredentialCount == 1, "Principal reports its credential count");

    Check(await service.SaveCredentialAsync(new(null, alice, "Tablet", [realityId, reality2.Id]), default) is null,
        "One credential spans two inbounds of the same shape");
    var tablet = (await service.ReadAsync(default)).Credentials.Single(c => c.Name == "Tablet");
    Check(tablet.InboundIds.Count == 2, "Shareable credential keeps both inbounds");
    Check(await service.SaveAsync(new(3, [reality]), default) == "proxy_server.inbound_has_credentials",
        "An inbound still named by a credential cannot be dropped");
    Check(await service.DeleteCredentialAsync(tablet.Id, default) is null, "Delete the second credential");
    Check(await service.DeletePrincipalAsync(alice, default) == "proxy_server.principal_has_credentials",
        "A principal with credentials cannot be deleted");
    Check(await service.DeletePrincipalAsync(Guid.NewGuid(), default) == "proxy_server.principal_not_found",
        "Deleting an unknown principal is refused");
    Check(await service.SaveAsync(new(3, [reality]), default) is null, "Drop the unused inbound");

    await ThrowsAsync(() => service.ExportAsync(new(phone.Id, "uri"), default), "proxy_server.configuration_not_applied");
    await ThrowsAsync(() => service.ExportFormatsAsync(phone.Id, default), "proxy_server.configuration_not_applied");
    Check(await service.ApplyAsync(default) is null, "Publish stopped service");
    Check(!(await service.ReadAsync(default)).Status.Applied, "Stopped service is not applied");
    await ThrowsAsync(() => service.ExportAsync(new(phone.Id, "uri"), default), "proxy_server.configuration_not_applied");
    Check(await service.ActionAsync(ProxyServerAction.Start, default) is null, "Start service");
    var export = await service.ExportAsync(new(phone.Id, "uri"), default);
    Check(export.Content.StartsWith("vless://") && export.Content.Contains("security=reality"), "VLESS export");
    var formats = await service.ExportFormatsAsync(phone.Id, default);
    Check(formats.Count == ProxyServerExportFormats.All.Count && formats.All(f => f.Supported), "A single-inbound credential supports every format");

    var validation = await service.ValidateAsync(default);
    Check(validation.Valid, "Draft validates once the runtime exists");
    var state = await store.ReadAsync(default);
    Check(validation.Preview is not null && !validation.Preview.Contains(state.Credentials.Single().Value), "Preview excludes secrets");
    var safe = JsonSerializer.Serialize(await service.ReadAsync(default));
    Check(!safe.Contains(state.Credentials.Single().Value) && !safe.Contains(state.Keys.Single().PrivateKey), "Public reads exclude secrets");
    var encrypted = await File.ReadAllTextAsync(Path.Combine(root, "proxy-server.protected"));
    Check(!encrypted.Contains(state.Credentials.Single().Value) && !encrypted.Contains("Phone"), "Persisted state protected");

    // Subscriptions: only the issuing response ever carries the token.
    await ThrowsAsync(() => service.IssueSubscriptionAsync(new(phone.Id, ["uri"]), default), "proxy_server.subscription_base_invalid");
    var draft = (await service.ReadAsync(default)).Configuration;
    Check(await service.SaveAsync(new(draft.Revision, [reality], "https://subs.example.com/"), default) is null, "Record the share host");
    await ThrowsAsync(() => service.IssueSubscriptionAsync(new(Guid.NewGuid(), ["uri"]), default), "proxy_server.credential_not_found");
    await ThrowsAsync(() => service.IssueSubscriptionAsync(new(phone.Id, ["openvpn"]), default), "proxy_server.export_unsupported");
    await ThrowsAsync(() => service.IssueSubscriptionAsync(new(phone.Id, ["uri"], DateTimeOffset.UtcNow.AddMinutes(-1)), default),
        "proxy_server.subscription_expiry_invalid");
    var issued = await service.IssueSubscriptionAsync(new(phone.Id, ["uri"]), default);
    Check(issued.Token.Length is >= 40 and <= 64, "Subscription token length");
    Check(issued.Urls.Single() == "https://subs.example.com" + ProxyServerRoutes.PublicSubscriptionRoot + "/" + issued.Token + "/uri",
        "Subscription url points at the anonymous route");
    var listed = await service.SubscriptionsAsync(default);
    Check(listed.Single().Id == issued.Subscription.Id && !listed.Single().Revoked, "Subscription is listed");
    var tokenHash = (await store.ReadAsync(default)).Subscriptions.Single().TokenHash;
    var afterIssue = JsonSerializer.Serialize(await service.ReadAsync(default));
    Check(!afterIssue.Contains(issued.Token) && !afterIssue.Contains(tokenHash), "Snapshots never carry the token or its hash");
    Check((await service.SubscriptionContentAsync(issued.Token, "uri", default)).Content == export.Content,
        "Anonymous download matches the management export");
    await ThrowsAsync(() => service.SubscriptionContentAsync(issued.Token, "sing-box", default), "proxy_server.subscription_invalid");
    await ThrowsAsync(() => service.SubscriptionContentAsync("short", "uri", default), "proxy_server.subscription_invalid");
    await ThrowsAsync(() => service.SubscriptionContentAsync(new string('a', 43), "uri", default), "proxy_server.subscription_invalid");
    Check(await service.RevokeSubscriptionAsync(issued.Subscription.Id, default) is null, "Revoke subscription");
    await ThrowsAsync(() => service.SubscriptionContentAsync(issued.Token, "uri", default), "proxy_server.subscription_invalid");
    Check(await service.RevokeSubscriptionAsync(Guid.NewGuid(), default) == "proxy_server.subscription_not_found",
        "Revoking an unknown subscription is refused");

    Check(await service.SaveCredentialAsync(new(null, alice, "Laptop", [realityId]), default) is null, "Create a second credential");
    var laptop = (await service.ReadAsync(default)).Credentials.Single(c => c.Name == "Laptop");
    var laptopSubscription = await service.IssueSubscriptionAsync(new(laptop.Id, ["uri", "sing-box"]), default);
    Check(laptopSubscription.Urls.Count == 2, "Every requested format gets a url");
    Check(await service.DeleteCredentialAsync(laptop.Id, default) is null, "Delete the second credential");
    Check((await service.SubscriptionsAsync(default)).Single(s => s.Id == laptopSubscription.Subscription.Id).Revoked,
        "Deleting a credential revokes the subscriptions it handed out");
    await ThrowsAsync(() => service.SubscriptionContentAsync(laptopSubscription.Token, "uri", default), "proxy_server.subscription_invalid");

    Check(await service.RotateCredentialAsync(phone.Id, null, default) is null, "Rotate credential");
    await ThrowsAsync(() => service.ExportAsync(new(phone.Id, "uri"), default), "proxy_server.configuration_not_applied");
    Check(await service.ApplyAsync(default) is null, "Publish rotated credential");
    Check((await service.ExportAsync(new(phone.Id, "uri"), default)).Content != export.Content, "Rotation changes exported credential");

    if (args.Length == 1)
    {
        var binary = Path.GetFullPath(args[0]);
        var keys = (await RunAsync(binary, ["generate", "reality-keypair"])).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = new ProxyServerRealityKey(realityId, keys.Single(l => l.StartsWith("PrivateKey: "))[12..], keys.Single(l => l.StartsWith("PublicKey: "))[11..], "1234567890abcdef");
        var native = new ProxyServerMaterial(new(0, [reality]), [new(Guid.NewGuid(), [realityId], Guid.NewGuid().ToString())], [key]);
        var nativePath = Path.Combine(root, "native.json");
        await File.WriteAllTextAsync(nativePath, SingBoxConfiguration.Render(native));
        await RunAsync(binary, ["check", "-c", nativePath]);
        var client = await service.ExportAsync(new(phone.Id, "sing-box"), default);
        await File.WriteAllTextAsync(nativePath, client.Content);
        await RunAsync(binary, ["check", "-c", nativePath]);
        Console.WriteLine("Native sing-box server and client configuration checks passed.");
    }

    var edited = (await service.ReadAsync(default)).Configuration;
    Check(await service.SaveAsync(new(edited.Revision, [reality with { PublicHost = "draft.example.com" }], edited.SubscriptionBaseUrl), default) is null,
        "Save an unrelated draft edit");
    Check(await service.SaveCredentialAsync(new(phone.Id, alice, "Phone", [realityId], ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)), default) is null,
        "Expire credential");
    await service.ReconcileExpiryAsync(default);
    var afterExpiry = await store.ReadAsync(default);
    Check(afterExpiry.Published!.Credentials.Count == 0, "Expired credential removed from the published grant set");
    Check(afterExpiry.Published.Configuration.Inbounds.Single().PublicHost == "proxy.example.com", "Expiry does not publish unrelated draft edits");
    await ThrowsAsync(() => service.ExportAsync(new(phone.Id, "uri"), default), "proxy_server.credential_unavailable");

    var rollbackBase = (await service.ReadAsync(default)).PublishedRevision;
    var pending = (await service.ReadAsync(default)).Configuration;
    Check(await service.SaveAsync(new(pending.Revision, [reality with { PublicHost = "edge.example.com" }], pending.SubscriptionBaseUrl), default) is null,
        "Save the next publication");
    Check(await service.ApplyAsync(default) is null, "Publish the next revision");
    Check((await service.ReadAsync(default)).PublishedRevision != rollbackBase, "Publication advanced");
    Check(await service.RollbackAsync(default) is null, "Rollback publication");
    Check((await service.ReadAsync(default)).PublishedRevision == rollbackBase, "Rollback restores the previous publication");
    Check(await service.RollbackAsync(default) == "proxy_server.rollback_unavailable", "Rollback only goes back one step");

    var operations = new ProxyOperationStore(new Paths(root), NullLogger<ProxyOperationStore>.Instance);
    var count = 0;
    var first = await service.EnqueueAsync("actor", "same-key", "check", new { Value = 1 }, operations, _ => { Interlocked.Increment(ref count); return Task.FromResult<string?>(null); }, default);
    var replay = await service.EnqueueAsync("actor", "same-key", "check", new { Value = 1 }, operations, _ => throw new Exception("Must not run twice"), default);
    Check(first.OperationId == replay.OperationId && count == 1, "Idempotent replay");
    await ThrowsAsync(() => service.EnqueueAsync("actor", "same-key", "check", new { Value = 2 }, operations, _ => Task.FromResult<string?>(null), default), "proxy_server.idempotency_conflict");
    await ThrowsAsync(() => service.EnqueueAsync("actor", " ", "check", new { Value = 1 }, operations, _ => Task.FromResult<string?>(null), default), "proxy_server.idempotency_key_required");

    Check(await service.UninstallAsync(keepData: true, default) is null, "Uninstall keeping data");
    var kept = await service.ReadAsync(default);
    Check(kept.PublishedRevision is null && kept.Credentials.Count == 1, "Uninstall keeps credentials but drops the publication");
    Check(await service.InstallAsync(default) is null, "Install runtime");
    Check(await service.UninstallAsync(keepData: false, default) is null, "Uninstall discarding data");
    var wiped = await service.ReadAsync(default);
    Check(wiped.Credentials.Count == 0 && wiped.Configuration.Revision == 0, "Discarding data wipes every record");
    Console.WriteLine("Proxy server configuration, principals, credentials, subscriptions, encryption, publication, rollback, expiry, export and idempotency checks passed.");
}
finally { Directory.Delete(root, true); }

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task ThrowsAsync(Func<Task> action, string code)
{
    try { await action(); }
    catch (ProxyServerException ex) when (ex.Message == code) { return; }
    throw new Exception("Expected " + code);
}
static async Task<string> RunAsync(string binary, string[] arguments)
{
    using var process = new Process { StartInfo = new(binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
    foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
    process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    var text = await output; var problem = await error;
    if (process.ExitCode != 0) throw new Exception("Native validation failed: " + problem);
    return text;
}
sealed class Paths(string root) : IProxyPlatformPaths
{
    public string GetStateDirectory() => root;
    public string GetEngineVersionsDirectory(string engineId) => root;
    public string GetEngineDataDirectory(string engineId) => root;
    public string GetProtectedConfigurationDirectory() => root;
    public string GetSanitizedLogDirectory() => root;
}
sealed class TestMode : IServerModeResolver
{
    public ServerMode Mode => ServerMode.System;
    public bool Supports(ServerHostFeature feature) => true;
    public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
}
sealed class Engines : IProxyEngineRegistry
{
    public IProxyEngine? Find(string engineId) => null;
    public IReadOnlyList<IProxyEngine> List() => [];
}
sealed class Helper : IPrivilegedOperationTransport
{
    private readonly List<string> applied = [];
    private bool installed = true;
    private bool running;

    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        object value;
        if (request.Operation == PrivilegedOperationKind.ProxyServerGenerateKeys)
            value = new ProxyServerRealityKey(Guid.Empty, new('A', 43), new('B', 43), "1234567890abcdef");
        else if (request.Operation == PrivilegedOperationKind.ProxyServerValidate)
            value = new ProxyServerValidation(true, [], null);
        else
        {
            switch (request.ProxyServer?.Action)
            {
                case ProxyServerAction.Install or ProxyServerAction.Upgrade: installed = true; running = false; break;
                case ProxyServerAction.Uninstall: installed = false; running = false; break;
                case ProxyServerAction.Start or ProxyServerAction.Restart: running = true; break;
                case ProxyServerAction.Stop or ProxyServerAction.Disable: running = false; break;
                // Rollback restores the previous publication, so the reported hash is the one below the top of the stack.
                case ProxyServerAction.Rollback: if (applied.Count > 1) applied.RemoveAt(applied.Count - 1); break;
            }
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply)
                applied.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(request.ProxyServer!.Material!)))));
            value = new ProxyServerStatus(true, installed, running, false, installed ? SingBoxManifest.Version : null,
                applied.Count == 0 ? null : applied[^1], running && applied.Count > 0, null);
        }
        return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType()))));
    }
}
