using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

internal static class MySqlProductSmoke
{
    public static async Task RunAsync(string mode)
    {
        if (File.ReadAllText("/etc/relaxkonos-db09-product-disposable").Trim() != "DB-09-product")
            throw new InvalidOperationException("Disposable fixture required");
        var transport = new NativeProcess();
        var service = new MySqlInstallationService(transport);
        var coordinator = new InstallationCoordinator(new(new FixtureEnvironment()), [service], new Lifetime());
        await coordinator.StartAsync(default);
        try
        {
            async Task<InstallationOperationDto> Submit(InstallationOperationKind kind, MySqlRequest request)
            {
                var operation = coordinator.Start(service.Id, kind, JsonSerializer.SerializeToElement(request), "fixture", Guid.NewGuid().ToString("N"));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                while (coordinator.Get(operation.OperationId)!.Operation.State is InstallationOperationState.Queued or InstallationOperationState.Running)
                    await Task.Delay(100, deadline.Token);
                return coordinator.Get(operation.OperationId)!.Operation;
            }
            async Task Execute(MySqlAction action)
            {
                var before = await service.ReadAsync(default);
                var done = await Submit(action == MySqlAction.Install ? InstallationOperationKind.Install : InstallationOperationKind.Configure,
                    new(true, action, before.Revision));
                Check(done.State == InstallationOperationState.Succeeded, action + " commits via the production queue: " + done.ProblemCode);
            }

            var initial = await service.ReadAsync(default);
            if (mode == "--after-restart")
            {
                Check(initial.InstanceId is not null && initial.Installed && !initial.Running, "Helper reads durable state after systemd restart");
                if (initial.PendingOperationId is { } pending)
                {
                    var recovery = await Submit(InstallationOperationKind.Repair, new(true, RecoverOperationId: pending));
                    var resumed = await service.ReadAsync(default, pending);
                    Check(recovery.State == InstallationOperationState.Succeeded && resumed.CommittedOperationId == pending,
                        "original interrupted Start resumes through the production queue after reboot");
                }
                else await Execute(MySqlAction.Start);
                Check((await service.ReadAsync(default)).Verified, "authenticated socket verified after systemd restart");
                await Execute(MySqlAction.Stop);
                return;
            }

            Check(!initial.Installed && initial.PendingOperationId is null, "fresh supported host");
            transport.DropInstallReceiptRead = true;
            var interrupted = await Submit(InstallationOperationKind.Install, new(true, MySqlAction.Install, initial.Revision));
            Check(interrupted.State == InstallationOperationState.Interrupted, "lost post-install response preserves Core interruption: " + interrupted.ProblemCode);
            var committed = await service.ReadAsync(default, interrupted.OperationId);
            Check(committed.Installed && committed.CommittedOperationId == interrupted.OperationId, "fresh Helper process returns original install receipt");
            var repair = await Submit(InstallationOperationKind.Repair, new(true, RecoverOperationId: interrupted.OperationId));
            Check(repair.State == InstallationOperationState.Succeeded && transport.InstallDispatches == 1,
                "user recovery reconciles install without repeating package dispatch");
            await Execute(MySqlAction.Initialize);
            Check((await service.ReadAsync(default)) is { InstanceId: not null, Running: true, Verified: true }, "initialized instance authenticates");
            Check(!Directory.Exists("/var/lib/mysql"), "no distribution default cluster");
            await Execute(MySqlAction.Verify);
            await Execute(MySqlAction.Stop);
            Check((await service.ReadAsync(default)) is { Running: false, Verified: false }, "stopped state is observed");
            await Execute(MySqlAction.Start);
            Check((await service.ReadAsync(default)).Verified, "start verifies local authentication");
            await Execute(MySqlAction.Stop);
        }
        finally { await coordinator.StopAsync(default); }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class NativeProcess : IPrivilegedOperationTransport
    {
        public bool DropInstallReceiptRead { get; set; }
        public int InstallDispatches { get; private set; }
        private bool dropNextQuery;

        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            if (dropNextQuery && request.Operation == PrivilegedOperationKind.MySqlQuery)
            {
                dropNextQuery = false;
                return new(false, Error: "fixture.response_lost");
            }
            if (request.MySql?.Action == MySqlAction.Install) InstallDispatches++;
            var id = request.OperationId ?? Guid.NewGuid();
            request = request with { OperationId = id, Correlation = CorrelationContext.Create(id, "privileged.operation") };
            using var process = new Process
            {
                StartInfo = new(Environment.ProcessPath!, "helper-stdin")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            await stderr;
            var reply = JsonSerializer.Deserialize<PrivilegedOperationResult>(await stdout)!;
            if (DropInstallReceiptRead && request.MySql?.Action == MySqlAction.Install && reply.Success)
            {
                dropNextQuery = true;
                DropInstallReceiptRead = false;
                return new(false, Error: "fixture.response_lost");
            }
            if (!reply.Success) Console.WriteLine("Helper: " + reply.Error);
            return reply;
        }
    }

    private sealed class FixtureEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "DB09";
        public string ContentRootPath { get; set; } = "/var/lib/relaxkonos/db09-product-acceptance";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
