using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;
using System.Diagnostics;
using System.Reflection;

internal static class ModuleHostLifetimeChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using (var fixture = new Fixture())
        {
            var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var generation = await fixture.Generation(async () => { calls++; entered.TrySetResult(); await shutdown.Task; });
            check(fixture.Active == 1 && fixture.Coordinator.Describe("fixture-host") is { Releasable: false, State: ModuleResidencyState.Resident },
                "a ModuleHost generation owns real continuous work protection and remains resident");
            check(await fixture.Coordinator.TrySleepAsync("fixture-host", default) == "module-resident-work-owner",
                "process work metadata cannot advertise ModuleHost idle release");
            using var proxy = generation.TryAcquire();
            using var callback = generation.TryAcquire();
            check(proxy is not null && callback is not null && proxy.GenerationId == callback.GenerationId
                && proxy.ModuleId == "fixture-host" && proxy.ServiceToken == new string('a', 64),
                "proxy and callback scopes bind to one exact process generation and service identity");
            generation.RequestClose();
            check(proxy!.Stopping.IsCancellationRequested && generation.TryAcquire() is null && calls == 0 && fixture.Active == 1,
                "closing revokes new work and signals old transport without ending its live scopes");
            proxy.Dispose();
            check(calls == 0 && !generation.Completion.IsCompleted, "one completed scope cannot drain another unfinished callback");
            callback!.Dispose(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var budget = new CancellationTokenSource(); budget.Cancel();
            try { await generation.Completion.WaitAsync(budget.Token); throw new Exception("Stop budget ignored"); }
            catch (OperationCanceledException) { }
            check(fixture.Active == 1 && !generation.Completion.IsCompleted && calls == 1,
                "stop wait cancellation preserves protection through the actual process cleanup task");
            shutdown.TrySetResult(); await generation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            check(fixture.Active == 0 && calls == 1, "confirmed process cleanup precedes the only release of its work lease");
            var next = await fixture.Generation(() => Task.CompletedTask);
            check(next.Id != generation.Id && generation.TryAcquire() is null,
                "a fresh process identity cannot reopen admission on a closed generation");
            next.RequestClose(); await next.Completion;
        }
        await using (var fixture = new Fixture())
        {
            var calls = 0;
            var generation = await fixture.Generation(() => { calls++; return Task.CompletedTask; }, startupFinished: false);
            generation.RequestClose();
            check(fixture.Active == 1 && calls == 0, "startup ownership remains protected while close waits for actual initialization completion");
            generation.StartupFinished(); await generation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            check(fixture.Active == 0 && calls == 1, "completed initialization permits one cleanup instead of a detached startup process");
        }
        await using (var fixture = new Fixture())
        {
            var generation = await fixture.Generation(() => Task.CompletedTask);
            using var callback = generation.TryAcquire();
            var stopping = fixture.Coordinator.DisposeAsync().AsTask();
            check(callback is not null && callback.Stopping.IsCancellationRequested && generation.TryAcquire() is null
                && !stopping.IsCompleted && fixture.Active == 1,
                "coordinator shutdown closes ModuleHost generation admission and waits for the real callback scope");
            callback!.Dispose(); await generation.Completion; await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            check(fixture.Active == 0, "coordinator drain follows generation and process cleanup rather than IPC cancellation notification");
        }
        // Cleanup failure intentionally retains responsibility; a counting lease avoids faking real coordinator recovery.
        var lease = new CountingLease(); var cleanupCalls = 0; Exception? observed = null;
        var failed = new ModuleHostGeneration("failed-host", new string('b', 64), lease,
            () => { cleanupCalls++; throw new IOException("process exit unknown"); }, () => { }, error => observed = error);
        failed.Start(); failed.StartupFinished(); failed.PublishReady(new Uri("http://127.0.0.1:1")); failed.RequestClose();
        try { await failed.Completion; throw new Exception("Unknown exit was hidden"); } catch (IOException) { }
        failed.RequestClose();
        check(!lease.Disposed && lease.RecoveryRequired && cleanupCalls == 1 && observed is IOException && failed.TryAcquire() is null,
            "unknown process cleanup retains its lease, reports failure and never replays shutdown");

        var secret = new byte[32]; Random.Shared.NextBytes(secret);
        using var frames = new MemoryStream();
        await ModuleHostPipeProtocol.WriteAsync(frames, new(1, "tree-exit", "0"), secret, "nonce", ModuleHostPipeProtocol.HelperDirection, default);
        frames.Position = 0;
        check((await ModuleHostPipeProtocol.ReadAsync(frames, secret, "nonce", ModuleHostPipeProtocol.HelperDirection, 1, default)).Type == "tree-exit",
            "the current helper contract accepts an explicit owned-tree exit acknowledgement");
        frames.SetLength(0);
        await ModuleHostPipeProtocol.WriteAsync(frames, new(1, "exit", "0"), secret, "nonce", ModuleHostPipeProtocol.HelperDirection, default);
        frames.Position = 0;
        try { await ModuleHostPipeProtocol.ReadAsync(frames, secret, "nonce", ModuleHostPipeProtocol.HelperDirection, 1, default); throw new Exception("Weak exit accepted"); }
        catch (InvalidDataException) { check(true, "a parent-only legacy exit frame cannot be treated as owned-tree cleanup proof"); }

        await VerifyExitedLauncherAsync(check, 7, isolated: false, failDuringWrite: false);
        await VerifyExitedLauncherAsync(check, 7, isolated: false, failDuringWrite: true);
        await VerifyExitedLauncherAsync(check, 0, isolated: false, failDuringWrite: true);
        if (OperatingSystem.IsLinux())
        {
            await VerifyExitedLauncherAsync(check, 0, isolated: true, failDuringWrite: false);
            await VerifyExitedLauncherAsync(check, 0, isolated: true, failDuringWrite: true);
            await VerifyExitedLauncherAsync(check, 7, isolated: true, failDuringWrite: false, acknowledgeTree: true);
            await VerifyExitedLauncherAsync(check, 7, isolated: true, failDuringWrite: true, acknowledgeTree: true);
        }

        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        var modules = ServerModules.CreateManaged(new HashSet<string>());
        modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
        builder.Services.AddSingleton<ModuleHostSupervisor>();
        await using var application = builder.Build();
        var supervisor = application.Services.GetRequiredService<ModuleHostSupervisor>();
        await supervisor.StartAsync(default); await supervisor.StopAsync(default);
        check(supervisor.Snapshot().Count == 0 && application.Services.GetRequiredService<ModuleResidencyCoordinator>().ModuleIds.Count == 0,
            "an all-disabled host still resolves and stops the mandatory ModuleHost work boundary with an empty catalog");
    }

    private static async Task VerifyExitedLauncherAsync(Action<bool, string> check, int exitCode, bool isolated, bool failDuringWrite, bool acknowledgeTree = false)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in OperatingSystem.IsWindows()
            ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$null=[Console]::ReadLine(); exit " + exitCode }
            : new[] { "-c", "read ignored; "
                + (acknowledgeTree ? "printf '%s\\n' '{\"type\":\"tree-stopped\",\"moduleId\":\"fixture-host\"}'; " : "")
                + "exit " + exitCode }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Fixture launcher did not start.");
        var input = process.StandardInput;
        var client = new ModuleHostProcessClient("unused-fixture-path", "fixture-host", "unused-fixture-data", isolated: isolated);
        var clientProcess = typeof(ModuleHostProcessClient).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!;
        clientProcess.SetValue(client, process);
        var processInput = typeof(Process).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => typeof(StreamWriter).IsAssignableFrom(field.FieldType) && ReferenceEquals(field.GetValue(process), input));
        ExitThenFailWriter? failing = null;
        try
        {
            if (failDuringWrite)
            {
                failing = new(process, input);
                processInput.SetValue(process, failing);
            }
            else
            {
                await input.WriteLineAsync("finish"); await input.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            var refused = false;
            try { await client.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (ModulePackageException error) when (error.ProblemCode == "module-host-exit-unconfirmed") { refused = true; }
            if (acknowledgeTree)
            {
                check(!refused && process.ExitCode == exitCode,
                    "a Linux monitor tree acknowledgement can settle cleanup after a crash without rewriting its nonzero exit");
            }
            else if (exitCode != 0 || isolated)
            {
                check(refused && ReferenceEquals(clientProcess.GetValue(client), process) && process.HasExited,
                    failDuringWrite
                        ? $"actual launcher IO failure after exit cannot hide {(isolated ? "missing Linux tree proof" : "a nonzero exit code")} in the shutdown catch path"
                        : $"an already exited launcher cannot replace {(isolated ? "explicit Linux tree proof" : "a successful exit code")}");
                try { await client.ShutdownAsync(); throw new Exception("Unknown launcher cleanup was replayed"); }
                catch (ModulePackageException error) when (error.ProblemCode == "module-host-exit-unconfirmed") { }
                if (failing is not null && failing.Writes != 1) throw new Exception("Unknown launcher shutdown was replayed.");
            }
            else
            {
                check(!refused && process.HasExited && failing?.Writes == 1,
                    "a confirmed zero-exit development launcher may finish after its input closes without replaying the shutdown write");
            }
        }
        finally
        {
            // Only a test-created, now exited launcher is cleaned up here; no production recovery proof is manufactured.
            processInput.SetValue(process, input); failing?.Dispose();
            if (!process.HasExited)
            {
                await input.WriteLineAsync("finish"); await input.FlushAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class ExitThenFailWriter(Process process, StreamWriter original) : StreamWriter(Stream.Null)
    {
        internal int Writes;
        public override async Task WriteLineAsync(string? value)
        {
            Writes++;
            await original.WriteLineAsync("finish"); await original.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw new IOException("Owned fixture launcher exited while the shutdown write was completing.");
        }
    }

    private sealed class CountingLease : IModuleWorkLease
    {
        internal bool Disposed;
        internal bool RecoveryRequired;
        public CancellationToken Stopping => default;
        public void RequireRecovery() => RecoveryRequired = true;
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebApplication _application;
        internal ModuleResidencyCoordinator Coordinator { get; }
        internal int Active => Coordinator.Describe("fixture-host")!.ActiveLeases;
        internal Fixture()
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.Configuration.Sources.Clear();
            _application = builder.Build();
            var ids = new HashSet<string> { "fixture-host" };
            Coordinator = new([new ResidentModuleWorkRuntimeFactory("fixture-host")],
                ModuleResidencyOptions.Parse(builder.Configuration, ids, new HashSet<string>()), _application.Services,
                ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"),
                _application.Services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>(), residentWorkModuleIds: ids);
        }
        internal async Task<ModuleHostGeneration> Generation(Func<Task> cleanup, bool startupFinished = true)
        {
            var lease = await new ResidentModuleWorkLeaseSource(Coordinator).AcquireAsync("fixture-host");
            var generation = new ModuleHostGeneration("fixture-host", new string('a', 64), lease, cleanup, () => { }, _ => { });
            generation.Start(); generation.PublishReady(new Uri("http://127.0.0.1:1"));
            if (startupFinished) generation.StartupFinished();
            return generation;
        }
        public async ValueTask DisposeAsync() { await Coordinator.DisposeAsync(); await _application.DisposeAsync(); }
    }
}
