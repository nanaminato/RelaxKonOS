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
using RelaxKonOS.Protocol.Privileged;
using Microsoft.Extensions.Logging.Abstractions;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows native acceptance requires Windows.");
if (args is ["--cleanup", var cleanupPath])
{
    var cleanup = NativeConfiguration.Read(cleanupPath);
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
    foreach (var id in new[] { "fixture-host", "fixture-http", "fixture-broker" })
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
if (args is ["--action-fixture", var fixtureConfig])
{
    var fixture = NativeConfiguration.Read(fixtureConfig);
    ServiceBase.Run(new ActionFixtureService(Path.Combine(Path.GetDirectoryName(fixture.WorkerReport)!, "action-pid.txt"),
        Path.Combine(Path.GetDirectoryName(fixture.ClientReport)!, "service-stop-delay.flag"))
        { ServiceName = fixture.ActionServiceName, AutoLog = false, CanStop = true });
    return;
}
if (args is not ["--client", var configurationPath]) throw new ArgumentException("--client <protected configuration> or --service <protected configuration> is required.");
var config = NativeConfiguration.Read(configurationPath);
var administratorPassword = Console.ReadLine() ?? throw new InvalidDataException("Missing test administrator credential on stdin.");
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
    var installation = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(config.ClientReport)!, "installation-report.json")));
    using (installation)
    {
        Check(installation.RootElement.GetProperty("productionInstaller").GetBoolean()
            && installation.RootElement.GetProperty("reusedSnapshot").GetBoolean(),
            "the production Windows installer provisions and reuses the immutable runtime snapshot");
        Check(installation.RootElement.GetProperty("rejectedUnexpectedFile").GetBoolean()
            && installation.RootElement.GetProperty("rejectedCorruption").GetBoolean(),
            "the production Windows installer rejects extra snapshot files and corrupted executables");
    }
    using (var authorized = new NamedPipeClientStream(".", config.PipeName, PipeDirection.InOut))
        authorized.Connect(3000);
    Check(true, "the real protected Helper probe pipe accepts the configured Server caller");
    var transport = new WindowsNamedPipePrivilegedOperationTransport(options,
        NullLogger<WindowsNamedPipePrivilegedOperationTransport>.Instance);
    var wrongAdministrator = HostAdministratorAuthenticator.AuthenticateWindows(config.AdministratorName, "invalid-acceptance-password");
    Check(!wrongAdministrator.Succeeded, "real Windows administrator authentication rejects an incorrect password");
    var administrator = HostAdministratorAuthenticator.AuthenticateWindows(config.AdministratorName, administratorPassword);
    Check(administrator.Succeeded && administrator.AdministratorIdentity?.StartsWith("windows:S-1-5-21-", StringComparison.Ordinal) == true,
        "real Windows LogonUser verifies the temporary administrator and returns a canonical SID");
    var brokerRoot = Path.Combine(Path.GetDirectoryName(config.ClientReport)!, "broker");
    var brokerIdentity = new ModulePrivilegeIdentity("fixture-host", "1.0.0", "fixture", new string('a', 64), new string('b', 64),
        "native-test", [new ServiceManageCapability { Platforms = ["windows"], Services = [new(config.ActionServiceName, ["restart"])] }],
        [config.ActionServiceName]);
    var plans = new ModulePrivilegePlanStore(brokerRoot, _ => brokerIdentity);
    static Task<ModulePrivilegeAdministrator?> Verification(HostAdministratorAuthenticationResult authentication) =>
        Task.FromResult<ModulePrivilegeAdministrator?>(authentication.Succeeded && authentication.AdministratorIdentity is { } id
            ? new(id, authentication.AuthenticationMethod) : null);
    var prepared = await plans.PrepareAsync("fixture-host", "native-windows-session",
        new(ModuleCapabilityCatalog.ServiceManage, "restart", config.ActionServiceName,
            "Restart only the unique Windows acceptance service", Guid.NewGuid().ToString("N")), "native-test-user");
    var plan = await plans.ReadForUserAsync(prepared.PlanId, "native-windows-session");
    var approval = new ModulePrivilegeApproval(plan.PlanHash, plan.ConfirmationToken);
    var executed = 0;
    async Task<bool> Execute(Guid id, ModulePrivilegeRequest request)
    {
        executed++;
        var result = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NativeServiceAction,
            ServiceId: request.Target, ServiceAction: PrivilegedServiceAction.Restart, OperationId: id));
        return result.Success;
    }
    try
    {
        await plans.ApproveAsync(plan.PlanId, "native-windows-session", approval, () => Verification(wrongAdministrator), Execute);
        throw new InvalidOperationException("Bad administrator credentials reached execution.");
    }
    catch (ModulePrivilegeException error) when (error.Code == "module.privilege_admin_required") { }
    Check(executed == 0, "Broker confirmation rejects real failed administrator verification without dispatch");
    var brokerPidPath = Path.Combine(Path.GetDirectoryName(config.WorkerReport)!, "action-pid.txt");
    var beforeBroker = await File.ReadAllTextAsync(brokerPidPath);
    var approved = await plans.ApproveAsync(plan.PlanId, "native-windows-session", approval,
        () => Verification(HostAdministratorAuthenticator.AuthenticateWindows(config.AdministratorName, administratorPassword)), Execute);
    Check(approved.State == "Succeeded" && executed == 1 && await File.ReadAllTextAsync(brokerPidPath) != beforeBroker,
        "explicit Broker confirmation with real administrator verification executes one real Helper service restart");
    try
    {
        await plans.ApproveAsync(plan.PlanId, "native-windows-session", approval, () => Verification(administrator), Execute);
        throw new InvalidOperationException("An old confirmation was replayed.");
    }
    catch (ModulePrivilegeException error) when (error.Code == "module.privilege_approval_invalid") { }
    Check(executed == 1, "Broker rejects replay after the real Windows privileged action");
    await WindowsCoreAcceptance.RunAsync(config, administratorPassword, Check);
    administratorPassword = "";
    var denied = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NativeServiceAction,
        ServiceId: config.ActionServiceName + "-outside-policy", ServiceAction: PrivilegedServiceAction.Restart));
    Check(!denied.Success && denied.ProblemCode == PrivilegedProblemCode.ResourceNotAllowed,
        "the real LocalSystem Helper rejects service targets outside its fixed allowlist");
    async Task ChangeService(PrivilegedServiceAction action)
    {
        var result = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.NativeServiceAction,
            ServiceId: config.ActionServiceName, ServiceAction: action));
        Check(result.Success, "the real LocalSystem Helper completes the allowed service " + action);
    }
    using (var fixture = new ServiceController(config.ActionServiceName))
    {
        await ChangeService(PrivilegedServiceAction.Stop);
        fixture.Refresh();
        Check(fixture.Status == ServiceControllerStatus.Stopped, "SCM confirms the actual fixture stopped");
        await ChangeService(PrivilegedServiceAction.Start);
        fixture.Refresh();
        Check(fixture.Status == ServiceControllerStatus.Running, "SCM confirms the actual fixture started");
        var pidPath = Path.Combine(Path.GetDirectoryName(config.WorkerReport)!, "action-pid.txt");
        var beforeRestart = await File.ReadAllTextAsync(pidPath);
        await ChangeService(PrivilegedServiceAction.Restart);
        fixture.Refresh();
        Check(fixture.Status == ServiceControllerStatus.Running, "SCM confirms the actual fixture restarted");
        Check(await File.ReadAllTextAsync(pidPath) != beforeRestart, "the real service restart creates a different native process PID");
    }
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
    WindowsModuleHostPolicy Policy, string ServerMarker, string WorkerReport, string ClientReport, string ActionServiceName, string AdministratorName, string ServerDirectory)
{
    internal static NativeConfiguration Read(string path) => ModuleHostPipeProtocol.Read<NativeConfiguration>(File.ReadAllBytes(path));
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AcceptanceService(NativeConfiguration configuration) : ServiceBase
{
    private WindowsModuleHostPipeServer? _host;
    private WindowsPrivilegedPipeServer? _helper;
    private readonly CancellationTokenSource _watchdogStopping = new();
    private Task? _watchdog;
    protected override void OnStart(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("RELAXKONOS_TEST_SERVER_SECRET", "test-marker-not-a-credential");
            _host = new WindowsModuleHostPipeServer(new WindowsHelperPipeConfiguration(configuration.PipeName,
                configuration.SharedSecret, [], [], configuration.CallerSid, ModuleHosts: configuration.Policy), error => Report(false, error));
            _host.Start();
            _helper = new WindowsPrivilegedPipeServer(new WindowsHelperPipeConfiguration(configuration.PipeName,
                configuration.SharedSecret, [], [configuration.ActionServiceName], configuration.CallerSid), error => Report(false, error));
            _helper.Start();
            _watchdog = Task.Run(async () =>
            {
                var marker = Path.Combine(Path.GetDirectoryName(configuration.ClientReport)!, "helper-stop-pending-crash.flag");
                try
                {
                    while (!_watchdogStopping.IsCancellationRequested)
                    {
                        if (File.Exists(marker))
                        {
                            using var target = new ServiceController(configuration.ActionServiceName);
                            if (target.Status == ServiceControllerStatus.StopPending)
                            {
                                // Test-only self-termination of this unique acceptance service.
                                // No PID, command or executable comes from the marker.
                                File.Delete(marker);
                                Process.GetCurrentProcess().Kill();
                                return;
                            }
                        }
                        await Task.Delay(50, _watchdogStopping.Token);
                    }
                }
                catch (OperationCanceledException) when (_watchdogStopping.IsCancellationRequested) { }
            });
            Report(true, null);
        }
        catch (Exception error) { Report(false, error); throw; }
    }
    private void Report(bool ready, Exception? error) => File.WriteAllText(configuration.WorkerReport,
        JsonSerializer.Serialize(new { ready, processId = Environment.ProcessId, identity = WindowsIdentity.GetCurrent().User?.Value,
            failureType = error?.GetType().Name, nativeCode = error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : (int?)null }, ModuleHostPipeProtocol.Json));
    protected override void OnStop()
    {
        _watchdogStopping.Cancel();
        _watchdog?.GetAwaiter().GetResult();
        _host?.StopAsync().GetAwaiter().GetResult();
        _helper?.StopAsync().GetAwaiter().GetResult();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); _helper?.DisposeAsync().AsTask().GetAwaiter().GetResult(); _watchdogStopping.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class ActionFixtureService(string pidPath, string delayMarker) : ServiceBase
{
    protected override void OnStart(string[] args) => File.WriteAllText(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    protected override void OnStop()
    {
        if (File.Exists(delayMarker)) Thread.Sleep(TimeSpan.FromSeconds(8));
    }
}
