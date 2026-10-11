using System.Diagnostics;
using RelaxKonOS.Server.SystemMonitor;
using RelaxKonOS.Protocol.SystemMonitor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Security.Claims;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;

public static class ProcessTerminationChecks
{
    public static async Task VerifyInstalledSnapshotsAsync(string hostConfigPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(hostConfigPath), optional: false).Build();
        var mode = new ServerModeResolver(configuration, RelaxKonOS.Server.UserExecution.UserExecutionBackend.Disabled);
        if (mode.Mode != RelaxKonOS.Protocol.Common.ServerMode.User) throw new InvalidOperationException("Expected a personal-mode installation.");
        var options = new PrivilegedHelperOptions
        {
            PipeName = configuration["PrivilegedHelper:PipeName"] ?? string.Empty,
            SharedSecret = configuration["PrivilegedHelper:SharedSecret"] ?? string.Empty,
        };
        var helper = new WindowsNamedPipePrivilegedOperationTransport(options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsNamedPipePrivilegedOperationTransport>.Instance);
        var sampler = new RelaxKonOS.Server.SystemPerformance.ProcessSampler(new OrdinaryControl(), mode, helper);
        var ordinary = await sampler.QueryAsync(1, 500, null, null, true);
        var elevated = await sampler.QueryAsync(1, 500, null, null, true, elevated: true);
        if (ordinary.TotalCount <= 0 || elevated.TotalCount <= 0) throw new InvalidOperationException("Installed context returned no processes.");
        Console.WriteLine($"Installed personal process verification passed: ordinary={ordinary.TotalCount}, helper={elevated.TotalCount}.");
    }

    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine($"PASS PROCESS {++count}: {label}"); }
        var tabbed = LinuxProcessMetadata.ParseStatus(["Name:\tfixture", "Uid:\t1001\t1001\t1001\t1001", "Threads:\t3"]);
        Check(tabbed.Uid == 1001 && tabbed.ThreadCount == 3, "Linux status fixture accepts tab-separated UID and threads");
        var spaced = LinuxProcessMetadata.ParseStatus(["Uid: 1002 1002 1002 1002", "Threads: 2"]);
        Check(spaced.Uid == 1002 && spaced.ThreadCount == 2, "Linux status fixture accepts space-separated fields");
        Check(LinuxProcessMetadata.ParseStatus(["Uid:\tinvalid\t1001", "Threads:\t-1"]).Uid is null &&
            LinuxProcessMetadata.ParseStatus(["Name:\tfixture"]).Uid is null, "Missing or malformed Linux owner remains unknown");
        Check(ProcessInstanceTermination.Terminate(0, DateTimeOffset.UtcNow, CancellationToken.None).ProblemCode == "process.invalid_instance", "Nonpositive PID rejected without an OS action");
        Check(ProcessInstanceTermination.Terminate(Environment.ProcessId, default, CancellationToken.None).ProblemCode == "process.invalid_instance", "Missing instance time rejected without an OS action");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var canceledBeforeAction = false;
        try { ProcessInstanceTermination.Terminate(Environment.ProcessId, DateTimeOffset.UtcNow, canceled.Token); }
        catch (OperationCanceledException) { canceledBeforeAction = true; }
        Check(canceledBeforeAction, "Cancellation rejected before opening or terminating a process");
        var request = new TerminateProcessRequest(DateTimeOffset.Parse("2026-10-01T00:00:00.1234567Z"));
        var json = JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default);
        Check(JsonSerializer.Deserialize<TerminateProcessRequest>(json, RelaxKonOSJsonOptions.Default) == request && json.Contains("1234567"), "Current request retains all timestamp ticks");
        var receipt = new KillProcessResultDto(false, true, "process.permission_denied", null);
        Check(JsonSerializer.Deserialize<KillProcessResultDto>(JsonSerializer.Serialize(receipt, RelaxKonOSJsonOptions.Default), RelaxKonOSJsonOptions.Default) == receipt,
            "Current unsuccessful receipt retains permission and problem facts");
        if (OperatingSystem.IsWindows())
        {
            using var current = Process.GetCurrentProcess();
            var identity = new DateTimeOffset(current.StartTime.ToUniversalTime());
            var snapshot = RelaxKonOS.Server.SystemPerformance.HostProcessSnapshotCollector.Capture();
            Check(snapshot.Items.Any(item => item.Process.Id == current.Id && item.Process.StartTime == identity && item.Process.ParentProcessId is > 0),
                "Windows helper snapshot contains the current instance and Toolhelp parent metadata");
            Check(snapshot.Items.Single(item => item.Process.Id == current.Id).Process.UserName is { Length: > 0 },
                "Windows token owner resolves to a nonempty account");
            var rejected = false;
            try { RelaxKonOS.Server.SystemPerformance.HostProcessSnapshotCollector.Capture(_ => throw new InvalidOperationException("test visibility failure")); }
            catch (InvalidOperationException error) { rejected = error.InnerException?.Message == "test visibility failure"; }
            Check(rejected, "An entirely failed enumeration reports its cause instead of an empty successful snapshot");
            var icons = new RelaxKonOS.Server.SystemPerformance.ProcessIconProvider();
            Check(await icons.GetIconAsync(current.Id, identity.AddMinutes(-1), default) is null,
                "Windows icon lookup rejects a different process instance");
            Check(await icons.GetIconAsync(current.Id, identity, default) is { Data.Length: > 0, ContentType: "image/png" },
                "Windows confirmed instance icon is encoded as PNG");
            foreach (var mode in new[] { RelaxKonOS.Protocol.Common.ServerMode.User, RelaxKonOS.Protocol.Common.ServerMode.System })
            {
                var transport = new ProcessTransport(snapshot);
                var ordinaryControl = new OrdinaryControl();
                var sampler = new RelaxKonOS.Server.SystemPerformance.ProcessSampler(ordinaryControl, new ProcessMode(mode), transport);
                Check((await sampler.QueryAsync(1, 500, null, null, true)).Items.Any(item => item.Id == current.Id)
                    && transport.Operations.Count == 0, $"Windows {mode} ordinary list works without the helper");
                await sampler.GetIconAsync(current.Id, identity);
                await sampler.KillAsync(current.Id, identity);
                Check(transport.Operations.Count == 0 && ordinaryControl.Kills == 1,
                    $"Windows {mode} ordinary icons and termination use the service account");
                Check((await sampler.QueryAsync(1, 500, null, null, true, elevated: true)).TotalCount == snapshot.Items.Count,
                    $"Windows {mode} administrator mode consumes the helper snapshot");
                await sampler.GetIconAsync(current.Id, identity, elevated: true);
                await sampler.KillAsync(current.Id, identity, elevated: true);
                Check(transport.Operations.SequenceEqual(new[] {
                    RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.ProcessSnapshot,
                    RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.ProcessIcon,
                    RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.ProcessTerminate }),
                    $"Windows {mode} administrator query, icon and termination use dedicated helper operations");
                transport.Fail = true;
                // A successful mocked termination invalidates the sample cache.
                var failed = false;
                try { await sampler.QueryAsync(1, 500, null, null, true, elevated: true); }
                catch (InvalidOperationException error) { failed = error.Message.Contains("HelperUnavailable"); }
                Check(failed, "Unavailable helper reports failure instead of a successful empty or partial list");
                transport.Fail = false;
                Check((await sampler.QueryAsync(1, 500, null, null, true)).Items.Any(item => item.Id == current.Id),
                    "Ordinary cache remains available when the administrator helper fails");
                transport.Snapshot = snapshot with { Items = snapshot.Items.Append(snapshot.Items[0]).ToArray() };
                failed = false;
                try { await sampler.QueryAsync(1, 500, null, null, true, elevated: true); }
                catch (InvalidOperationException error) { failed = error.Message.Contains("InvalidProtocol"); }
                Check(failed, "Inconsistent helper snapshot is rejected rather than cached");
                transport.Snapshot = snapshot with { Items = [] };
                failed = false;
                try { await sampler.QueryAsync(1, 500, null, null, true, elevated: true); }
                catch (InvalidOperationException error) { failed = error.Message.Contains("InvalidProtocol"); }
                Check(failed, "Empty full-host helper snapshot is rejected rather than displayed as zero processes");
            }
            await VerifyWindowsProcessHttpAsync(snapshot, current.Id, Check);
        }
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--process-termination-worker");
        using var child = Process.Start(start)!;
        try
        {
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "Isolated test child reached its wait state");
            var identity = new DateTimeOffset(child.StartTime.ToUniversalTime());
            var stale = ProcessInstanceTermination.Terminate(child.Id, identity.AddMinutes(-1), CancellationToken.None);
            Check(!stale.Success && stale.ProblemCode == "process.instance_changed" && !child.HasExited, "Wrong start time cannot terminate the test child");
            var killed = ProcessInstanceTermination.Terminate(child.Id, identity, CancellationToken.None);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(killed.Success && !killed.RequiresElevation && killed.ProblemCode == "" && child.HasExited, "Confirmed test child instance is terminated and exit observed");
            var missing = ProcessInstanceTermination.Terminate(child.Id, identity, CancellationToken.None);
            Check(!missing.Success, "Already exited instance never returns fabricated success");
        }
        finally
        {
            // This Process object is the only child created above; no process enumeration or name kill.
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
        }
        Console.WriteLine($"Process termination checks passed: {count}.");
    }

    private static async Task VerifyWindowsProcessHttpAsync(HostProcessSnapshotDto snapshot, int currentId, Action<bool, string> check)
    {
        var mode = new ProcessMode(RelaxKonOS.Protocol.Common.ServerMode.User);
        var transport = new ProcessTransport(snapshot);
        var sampler = new RelaxKonOS.Server.SystemPerformance.ProcessSampler(new OrdinaryControl(), mode, transport);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.IProcessService>(sampler);
        builder.Services.AddSingleton<RelaxKonOS.Server.SystemPerformance.IPerformanceSampler>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IServerModeResolver>(mode);
        builder.Services.AddSingleton<IHostAccountPrivilegeService>(new TestHostAccountPrivilegeService { Level = HostAccountPrivilege.HostAdministrator });
        builder.Services.AddSingleton<HostElevationSessionState>();
        builder.Services.AddScoped<IHostElevationSessionStore, HostElevationSessionStore>();
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "process-http-user"),
                new Claim("jti", "process-http-token"), new Claim("amr", "system")], "test"));
            return next(context);
        });
        app.UseAuthorization(); app.MapSystemMonitorEndpoints(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var ordinary = await http.GetFromJsonAsync<ProcessPageDto>(SystemMonitorApiRoutes.ProcessQuery + "?pageSize=500&elevated=false", RelaxKonOSJsonOptions.Default);
        check(ordinary is { TotalCount: > 0 } && ordinary.Items.Any(item => item.Id == currentId) && transport.Operations.Count == 0,
            "Windows User HTTP ordinary process query returns live rows without helper access");
        var elevated = await http.GetFromJsonAsync<ProcessPageDto>(SystemMonitorApiRoutes.ProcessQuery + "?pageSize=500&elevated=true", RelaxKonOSJsonOptions.Default);
        check(elevated?.TotalCount == snapshot.Items.Count && transport.Operations.Count == 1,
            "Windows User HTTP administrator query returns helper measurements through the actual sampler");
    }

    private sealed class ProcessMode(RelaxKonOS.Protocol.Common.ServerMode mode) : RelaxKonOS.Server.HostMode.IServerModeResolver
    {
        public RelaxKonOS.Protocol.Common.ServerMode Mode => mode;
        public RelaxKonOS.Protocol.Common.ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(RelaxKonOS.Server.HostMode.ServerHostFeature feature) => true;
    }

    private sealed class OrdinaryControl : ISystemMetricsProvider
    {
        public int Kills { get; private set; }
        public Task<SystemMetricsDto> GetMetricsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProcessInfoDto>> ListProcessesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KillProcessResultDto> KillProcessAsync(int id, DateTimeOffset startTime, CancellationToken ct = default)
        { Kills++; return Task.FromResult(new KillProcessResultDto(false, true, "process.permission_denied", null)); }
    }

    private sealed class ProcessTransport(HostProcessSnapshotDto snapshot) : RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport
    {
        public HostProcessSnapshotDto Snapshot { get; set; } = snapshot;
        public List<RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind> Operations { get; } = [];
        public bool Fail { get; set; }
        public Task<RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult> ExecuteAsync(
            RelaxKonOS.Protocol.Privileged.PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            Operations.Add(request.Operation);
            return Task.FromResult(Fail
                ? new RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult(false,
                    ProblemCode: RelaxKonOS.Protocol.Privileged.PrivilegedProblemCode.HelperUnavailable)
                : new RelaxKonOS.Protocol.Privileged.PrivilegedOperationResult(true, ProcessSnapshot: Snapshot,
                    ProcessTermination: new(true, false, "", null)));
        }
    }
}
