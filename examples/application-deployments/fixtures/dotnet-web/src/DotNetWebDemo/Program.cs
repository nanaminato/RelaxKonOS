// RelaxKonOS application-deployment fixture: a framework-dependent ASP.NET Core publish.
//
// The DotNetPublish template reads this project's publish output and requires:
//   * exactly one top-level *.runtimeconfig.json whose runtimeOptions carries tfm = net10.0,
//   * Microsoft.AspNetCore.App present in runtimeOptions.frameworks (because the application
//     definition declares the Web workload),
//   * no runtimeOptions.includedFrameworks (because selfContained is false),
//   * DotNetWebDemo.dll next to DotNetWebDemo.runtimeconfig.json.
//
// The template also sets ASPNETCORE_URLS=http://0.0.0.0:{containerPort} in the generated image, so
// this workload must never pin a port of its own: whatever container port the definition uses is
// what the process binds.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 200 only on the readiness path and on the index; anything else is a 404, so a wrong
// healthCheckPath in the deployment definition fails the deployment instead of passing silently.
app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/", () => Results.Json(new
{
    app = "relaxkonos-ad-dotnet-web",
    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    machine = Environment.MachineName,
    time = DateTimeOffset.UtcNow,
}));

app.Logger.LogInformation(
    "[dotnet-web] ASPNETCORE_URLS={Urls}",
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(unset)");

app.Run();
