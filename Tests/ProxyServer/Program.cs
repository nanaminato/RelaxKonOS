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
    var id = Guid.NewGuid();
    var inbound = new ProxyServerInbound(id, "Test", ProxyServerProtocol.VlessReality, "127.0.0.1", 24443, "proxy.example.com", 24443, "www.example.com", "www.example.com");
    Check(SingBoxConfiguration.Validate(new(0, [inbound])).Valid, "Valid VLESS configuration");
    foreach (var host in new[] { "127.0.0.1", "169.254.169.254", "10.1.2.3", "::1", "fc00::1", "::ffff:127.0.0.1" })
        Check(!SingBoxConfiguration.PublicAddress(IPAddress.Parse(host)), "Reject internal handshake address " + host);
    Check(!SingBoxConfiguration.Validate(new(0, [inbound, inbound])).Valid, "Duplicate inbounds rejected");
    Check(await service.SaveAsync(new(0, [inbound]), default) is null, "Save configuration");
    Check(await service.SaveAsync(new(0, [inbound]), default) == "proxy_server.revision_conflict", "Optimistic revision conflict");
    Check(await service.CredentialAsync(new(null, "Phone", id), default) is null, "Create credential");
    var snapshot = await service.ReadAsync(default);
    var credential = snapshot.Credentials.Single();
    await ThrowsAsync(() => service.ExportAsync(new(credential.Id, "uri"), default), "proxy_server.configuration_not_applied");
    Check(await service.ApplyAsync(default) is null, "Publish stopped service");
    Check(!(await service.ReadAsync(default)).Status.Applied, "Stopped service is not applied");
    Check(await service.ActionAsync(ProxyServerAction.Start, default) is null, "Start service");
    var export = await service.ExportAsync(new(credential.Id, "uri"), default);
    Check(export.Content.StartsWith("vless://") && export.Content.Contains("security=reality"), "VLESS export");
    var state = await store.ReadAsync(default);
    var safe = JsonSerializer.Serialize(await service.ReadAsync(default));
    Check(!safe.Contains(state.Credentials.Single().Value) && !safe.Contains(state.Keys.Single().PrivateKey), "Public reads exclude secrets");
    var encrypted = await File.ReadAllTextAsync(Path.Combine(root, "proxy-server.protected"));
    Check(!encrypted.Contains(state.Credentials.Single().Value) && !encrypted.Contains("Phone"), "Persisted state protected");
    await service.CredentialAsync(new(credential.Id, "Phone", id, Rotate: true), default);
    await ThrowsAsync(() => service.ExportAsync(new(credential.Id, "uri"), default), "proxy_server.configuration_not_applied");
    await service.ApplyAsync(default);
    Check((await service.ExportAsync(new(credential.Id, "uri"), default)).Content != export.Content, "Rotation changes exported credential");
    await service.SaveAsync(new(1, [inbound with { PublicHost = "draft.example.com" }]), default);
    await service.CredentialAsync(new(credential.Id, "Phone", id, ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)), default);
    await service.ReconcileExpiryAsync(default);
    var afterExpiry = await store.ReadAsync(default);
    Check(afterExpiry.Published!.Credentials.Count == 0, "Expired credential removed");
    Check(afterExpiry.Published.Configuration.Inbounds.Single().PublicHost == "proxy.example.com", "Expiry does not publish unrelated draft");
    await ThrowsAsync(() => service.ExportAsync(new(credential.Id, "uri"), default), "proxy_server.credential_unavailable");
    var operations = new ProxyOperationStore(new Paths(root), NullLogger<ProxyOperationStore>.Instance);
    var count = 0;
    var first = await service.EnqueueAsync("actor", "same-key", "check", new { Value = 1 }, operations, _ => { Interlocked.Increment(ref count); return Task.FromResult<string?>(null); }, default);
    var replay = await service.EnqueueAsync("actor", "same-key", "check", new { Value = 1 }, operations, _ => throw new Exception("Must not run twice"), default);
    Check(first.OperationId == replay.OperationId && count == 1, "Idempotent replay");
    await ThrowsAsync(() => service.EnqueueAsync("actor", "same-key", "check", new { Value = 2 }, operations, _ => Task.FromResult<string?>(null), default), "proxy_server.idempotency_conflict");
    if (args.Length == 1)
    {
        var binary = Path.GetFullPath(args[0]);
        var keys = (await RunAsync(binary, ["generate", "reality-keypair"])).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = new ProxyServerRealityKey(id, keys.Single(l => l.StartsWith("PrivateKey: "))[12..], keys.Single(l => l.StartsWith("PublicKey: "))[11..], "1234567890abcdef");
        var native = new ProxyServerMaterial(new(0, [inbound]), [new(Guid.NewGuid(), id, Guid.NewGuid().ToString())], [key]);
        var nativePath = Path.Combine(root, "native.json");
        await File.WriteAllTextAsync(nativePath, SingBoxConfiguration.Render(native));
        await RunAsync(binary, ["check", "-c", nativePath]);
        await service.CredentialAsync(new(credential.Id, "Phone", id), default);
        await service.ApplyAsync(default);
        var client = await service.ExportAsync(new(credential.Id, "sing-box"), default);
        await File.WriteAllTextAsync(nativePath, client.Content);
        await RunAsync(binary, ["check", "-c", nativePath]);
        Console.WriteLine("Native sing-box server and client configuration checks passed.");
    }
    Console.WriteLine("Proxy server configuration, encryption, publication, rotation, expiry, export and idempotency checks passed.");
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
    private bool _running;
    private string? _hash;
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        object value;
        if (request.Operation == PrivilegedOperationKind.ProxyServerGenerateKeys)
            value = new ProxyServerRealityKey(Guid.Empty, new('A', 43), new('B', 43), "1234567890abcdef");
        else
        {
            if (request.Operation == PrivilegedOperationKind.ProxyServerApply)
                _hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SingBoxConfiguration.Render(request.ProxyServer!.Material!))));
            if (request.Operation == PrivilegedOperationKind.ProxyServerAction)
                _running = request.ProxyServer!.Action is ProxyServerAction.Start or ProxyServerAction.Restart;
            value = new ProxyServerStatus(true, true, _running, false, SingBoxManifest.Version, _hash, _running, null);
        }
        return Task.FromResult(new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType()))));
    }
}

