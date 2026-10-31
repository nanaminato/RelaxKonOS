using System.Text;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using RelaxKonOS.ModuleHost.Sdk;

namespace HostFixture;

/// <summary>Test-only v2 entry used by the isolated ModuleHost skeleton.</summary>
public class FixtureHostEntry : IModuleHostEntry, IModuleHostHealth, IModuleHostHttp
{
    private volatile bool _healthy = true;
    private readonly List<Process> _spawned = [];
    public virtual string Id => "fixture-host";
    public async ValueTask StartAsync(IModuleHostContext context, CancellationToken cancellationToken)
    {
        await context.Storage.WriteAsync("hello.txt", Encoding.UTF8.GetBytes("hello from module storage"),
            cancellationToken);
        context.Logger.LogInformation("Fixture module storage initialized.");
    }
    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<ModuleHostHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleHostHealth(_healthy));
    public void MapEndpoints(IEndpointRouteBuilder endpoints, IModuleHostContext context)
    {
        endpoints.MapGet("/isolation", (string readPath, string writePath, string socketPath) =>
        {
            bool readable;
            try { using var stream = File.OpenRead(readPath); readable = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { readable = false; }
            bool writable;
            try { using var stream = File.Open(writePath, FileMode.Open, FileAccess.Write); writable = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { writable = false; }
            bool helper;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var pipe = new NamedPipeClientStream(".", socketPath, PipeDirection.InOut);
                    pipe.Connect(300);
                }
                else
                {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
                }
                helper = true;
            }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or TimeoutException) { helper = false; }
            string? sid = null;
            var administrator = false;
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value;
                administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            return Results.Json(new { user = Environment.UserName, sid, administrator, readable, writable, helper,
                serverEnvironmentLeaked = Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SERVER_SECRET") is not null });
        });
        endpoints.MapPost("/spawn-child", () =>
        {
            var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/usr/bin/sleep";
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in OperatingSystem.IsWindows() ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" } : new[] { "120" }) info.ArgumentList.Add(argument);
            var process = Process.Start(info) ?? throw new IOException();
            lock (_spawned) _spawned.Add(process);
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            return Results.Json(new { pid = process.Id });
        });
        endpoints.MapPost("/unhealthy", () => { _healthy = false; return Results.Ok(); });
        endpoints.MapPost("/privilege", async (RelaxKonOS.ModuleHost.Protocol.ModulePrivilegeRequest request) =>
            Results.Json(await context.Privileges.PrepareAsync(request, CancellationToken.None)));
        endpoints.MapGet("/hello", () => "hello from module host");
        endpoints.MapGet("/storage", async () =>
        {
            var bytes = await context.Storage.ReadAsync("hello.txt", 4096, CancellationToken.None);
            return bytes is null ? Results.NotFound() : Results.Text(Encoding.UTF8.GetString(bytes));
        });
    }
}

public sealed class FixtureHttpHostEntry : FixtureHostEntry
{
    public override string Id => "fixture-http";
}
