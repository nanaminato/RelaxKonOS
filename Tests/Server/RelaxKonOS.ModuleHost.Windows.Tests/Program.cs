extern alias modulehelper;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;
using modulehelper::RelaxKonOS.PrivilegedHelper;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows native acceptance requires Windows.");
if (args is ["--cleanup", var cleanupPath])
{
    var cleanup = NativeConfiguration.Read(cleanupPath);
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
    foreach (var id in new[] { "fixture-host", "fixture-http" })
    {
        var moduleRoot = Path.Combine(cleanup.Policy.IsolationRoot, id);
        if (File.Exists(Path.Combine(moduleRoot, "identity.json")))
            WindowsModuleHostAccount.RemoveRecorded(moduleRoot, id, cleanup.Policy.IsolationRoot);
    }
    return;
}
if (args is ["--service", var serviceConfiguration])
{
    var configuration = NativeConfiguration.Read(serviceConfiguration);
    using var identity = WindowsIdentity.GetCurrent();
    if (identity.User != WindowsModuleHostSecurity.System) throw new UnauthorizedAccessException("LocalSystem service is required.");
    ServiceBase.Run(new AcceptanceService(configuration) { ServiceName = configuration.ServiceName, AutoLog = false, CanStop = true });
    return;
}
if (args is not ["--client", var configurationPath]) throw new ArgumentException("--client <protected configuration> or --service <protected configuration> is required.");
var config = NativeConfiguration.Read(configurationPath);
var checks = new List<string>();
void Check(bool passed, string label)
{
    if (!passed) throw new InvalidOperationException(label);
    checks.Add(label);
    Console.WriteLine("PASS WINDOWS MODULEHOST " + label);
}
var options = new PrivilegedHelperOptions { PipeName = config.PipeName, SharedSecret = config.SharedSecret };
var source = config.Policy.SourceRoot;
await using var first = new ModuleHostProcessClient(Path.Combine(source, "fixture-host", "1.0.0"), "fixture-host", "ignored",
    isolated: true, helperOptions: options);
await using var second = new ModuleHostProcessClient(Path.Combine(source, "fixture-http", "1.0.0"), "fixture-http", "ignored",
    isolated: true, helperOptions: options);
try
{
    using (var authorized = new NamedPipeClientStream(".", config.PipeName, PipeDirection.InOut))
        authorized.Connect(3000);
    Check(true, "the real protected Helper probe pipe accepts the configured Server caller");
    var ready = await first.StartAsync();
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(ready.Url) };
    using var unauthenticated = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(ready.Url) };
    Check((await unauthenticated.GetAsync("/hello")).StatusCode == System.Net.HttpStatusCode.Unauthorized,
        "the dedicated ModuleHost HTTP listener requires its process token");
    http.DefaultRequestHeaders.Add(ModuleHostProtocol.ServiceTokenHeader, first.ServiceToken);
    var manifest = Path.Combine(source, "fixture-host", "1.0.0", "manifest.json");
    var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(manifest)));
    var immutable = Path.Combine(config.Policy.IsolationRoot, "fixture-host", "1.0.0-" + digest, "manifest.json");
    async Task<JsonElement> Inspect(string readPath)
    {
        var route = "/isolation?readPath=" + Uri.EscapeDataString(readPath) + "&writePath=" + Uri.EscapeDataString(immutable)
            + "&socketPath=" + Uri.EscapeDataString(config.PipeName);
        return await http.GetFromJsonAsync<JsonElement>(route);
    }
    var isolation = await Inspect(config.ServerMarker);
    var accountSid = isolation.GetProperty("sid").GetString();
    Check(isolation.GetProperty("user").GetString()!.StartsWith("rkmod-", StringComparison.OrdinalIgnoreCase)
        && accountSid != config.CallerSid && accountSid != "S-1-5-18" && !isolation.GetProperty("administrator").GetBoolean(),
        "the child runs as an actual distinct ordinary local module account");
    Check(!isolation.GetProperty("readable").GetBoolean() && !isolation.GetProperty("writable").GetBoolean(),
        "native ACLs deny Server-private data reads and immutable package writes");
    Check(!isolation.GetProperty("helper").GetBoolean(), "the module account cannot connect to the actual protected Helper pipe");
    Check(!isolation.GetProperty("serverEnvironmentLeaked").GetBoolean(), "the module does not inherit the Helper or Server environment");
    var secret = await Inspect(configurationPath);
    Check(!secret.GetProperty("readable").GetBoolean(), "the module cannot read the machine-secret configuration");
    Check(await http.GetStringAsync("/storage") == "hello from module storage", "the dedicated account can write and read its own private data");
    var peerReady = await second.StartAsync();
    using var peer = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(peerReady.Url) };
    peer.DefaultRequestHeaders.Add(ModuleHostProtocol.ServiceTokenHeader, second.ServiceToken);
    var peerIdentity = await peer.GetFromJsonAsync<JsonElement>("/isolation?readPath=" + Uri.EscapeDataString(config.ServerMarker)
        + "&writePath=" + Uri.EscapeDataString(immutable) + "&socketPath=" + Uri.EscapeDataString(config.PipeName));
    Check(peerIdentity.GetProperty("sid").GetString() != accountSid, "two concurrently running modules use different actual account SIDs");
    var cross = await Inspect(Path.Combine(config.Policy.IsolationRoot, "fixture-http", "data", "hello.txt"));
    Check(!cross.GetProperty("readable").GetBoolean(), "one module cannot read another running module's real private data");
    var healthy = await first.HealthAsync();
    Check(healthy.Healthy, "the authenticated line protocol health check succeeds under the dedicated token");
    using var spawned = await http.PostAsync("/spawn-child", null);
    spawned.EnsureSuccessStatusCode();
    var childPid = (await spawned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pid").GetInt32();
    Check(Process.GetProcesses().Any(process => { using (process) return process.Id == childPid; }), "a real descendant process is created inside the module job");
    // Abrupt client disposal closes the privileged launch channel. The job must reclaim descendants.
    await first.AbortAsync();
    for (var attempt = 0; attempt < 100 && Process.GetProcesses().Any(process => { using (process) return process.Id == childPid; }); attempt++)
        await Task.Delay(50);
    Check(!Process.GetProcesses().Any(process => { using (process) return process.Id == childPid; }),
        "closing the launch channel reclaims the complete native process tree");
    await second.ShutdownAsync();
    Check(true, "the second dedicated ModuleHost shuts down independently");
    await File.WriteAllTextAsync(config.ClientReport, JsonSerializer.Serialize(new { succeeded = true, checks }, ModuleHostPipeProtocol.Json));
}
catch (Exception error)
{
    await File.WriteAllTextAsync(config.ClientReport, JsonSerializer.Serialize(new { succeeded = false, checks,
        failureType = error.GetType().Name, problemCode = error is ModulePackageException package ? package.ProblemCode : null }, ModuleHostPipeProtocol.Json));
    throw;
}

internal sealed record NativeConfiguration(string ServiceName, string PipeName, string SharedSecret, string CallerSid,
    WindowsModuleHostPolicy Policy, string ServerMarker, string WorkerReport, string ClientReport)
{
    internal static NativeConfiguration Read(string path) => ModuleHostPipeProtocol.Read<NativeConfiguration>(File.ReadAllBytes(path));
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AcceptanceService(NativeConfiguration configuration) : ServiceBase
{
    private WindowsModuleHostPipeServer? _host;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _probe;
    protected override void OnStart(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("RELAXKONOS_TEST_SERVER_SECRET", "test-marker-not-a-credential");
            _host = new WindowsModuleHostPipeServer(new WindowsHelperPipeConfiguration(configuration.PipeName,
                configuration.SharedSecret, [], [], configuration.CallerSid, ModuleHosts: configuration.Policy), error => Report(false, error));
            _host.Start();
            var initialProbe = CreateProbe();
            _probe = Task.Run(async () =>
            {
                var first = initialProbe;
                while (!_stopping.IsCancellationRequested)
                {
                    await using var pipe = first ?? CreateProbe();
                    first = null;
                    try { await pipe.WaitForConnectionAsync(_stopping.Token); }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                }
            });
            Report(true, null);
        }
        catch (Exception error) { Report(false, error); throw; }
    }
    private NamedPipeServerStream CreateProbe() => NamedPipeServerStreamAcl.Create(configuration.PipeName,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0,
        WindowsPrivilegedPipeSecurity.Build(configuration.CallerSid, []));
    private void Report(bool ready, Exception? error) => File.WriteAllText(configuration.WorkerReport,
        JsonSerializer.Serialize(new { ready, identity = WindowsIdentity.GetCurrent().User?.Value,
            failureType = error?.GetType().Name, nativeCode = error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : (int?)null }, ModuleHostPipeProtocol.Json));
    protected override void OnStop()
    {
        _stopping.Cancel();
        _host?.StopAsync().GetAwaiter().GetResult();
        if (_probe is not null) _probe.GetAwaiter().GetResult();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); _stopping.Dispose(); }
        base.Dispose(disposing);
    }
}
