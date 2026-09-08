using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Guardian.Agent;

var options = GuardianAgentOptions.Load(args);
if (string.IsNullOrWhiteSpace(options.SharedSecret))
    throw new InvalidOperationException("RELAXKONOS_GUARDIAN_SHARED_SECRET must be configured for the Guardian Agent.");

var builder = Host.CreateApplicationBuilder(args);
// These lifetimes activate only when launched by the corresponding service manager.
// The same executable remains convenient to run interactively during development.
builder.Services.AddWindowsService(service => service.ServiceName = "RelaxKonOSGuardian");
builder.Services.AddSystemd();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WorkloadSupervisor>();
builder.Services.AddSingleton<GuardianPipeServer>();
builder.Services.AddSingleton<ProtectedServerMonitor>();
builder.Services.AddHostedService<GuardianWorker>();

await builder.Build().RunAsync();
