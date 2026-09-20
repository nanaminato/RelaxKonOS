// RelaxKonOS application-deployment fixture: a framework-dependent console worker publish.
//
// This is the negative twin of fixtures/dotnet-web for the template's Web/Worker check: the project
// uses Microsoft.NET.Sdk (not Sdk.Web), so the published runtimeconfig.json has no
// Microsoft.AspNetCore.App entry. Deploying it with workloadKind=Web must therefore fail with
// application-deployment.runtime_mismatch, and deploying it with workloadKind=Worker must succeed.
//
// A worker has no HTTP surface, so the deployment definition must use readinessLevel=Process. That
// level is explicitly the weaker "process is alive" check, which is why this loop keeps logging:
// the container log is the only evidence of progress an operator gets.

using System.Runtime.InteropServices;

using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

Console.WriteLine($"[dotnet-worker] started at {DateTimeOffset.UtcNow:O}");
Console.WriteLine($"[dotnet-worker] framework {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"[dotnet-worker] machine {Environment.MachineName}");

var ticks = 0;
while (!stopping.IsCancellationRequested)
{
    ticks++;
    Console.WriteLine($"[dotnet-worker] heartbeat={ticks} at {DateTimeOffset.UtcNow:O}");
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
    }
    catch (TaskCanceledException)
    {
        break;
    }
}

Console.WriteLine($"[dotnet-worker] stopping after {ticks} heartbeats");
