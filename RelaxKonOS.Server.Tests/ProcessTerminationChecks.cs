using System.Diagnostics;
using RelaxKonOS.Server.SystemMonitor;
using RelaxKonOS.Protocol.SystemMonitor;

public static class ProcessTerminationChecks
{
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
}
