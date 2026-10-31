using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.ServerModuleHost;

internal static class ModuleHostChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "module-host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            var relative = "server/" + rid + "/HostFixture.dll";
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "HostFixture.dll"), path);
            var bytes = await File.ReadAllBytesAsync(path);
            var manifest = new
            {
                schemaVersion = 1,
                moduleId = "fixture-host",
                version = "1.0.0",
                entryAssembly = relative,
                entryType = "HostFixture.FixtureHostEntry",
                files = new[]
                {
                    new { path = relative, length = bytes.Length,
                        sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var output = new StringWriter();
            var exit = await ModuleHostProgram.RunAsync(["--package-root", root, "--module-id", "fixture-host",
                "--data-path", Path.Combine(root, "data")], new StringReader("health\nshutdown\n"), output);
            var text = output.ToString();
            check(exit == 0 && text.Contains("\"type\":\"ready\"", StringComparison.Ordinal)
                && text.Contains("\"healthy\":true", StringComparison.Ordinal)
                && text.Contains("\"type\":\"stopped\"", StringComparison.Ordinal),
                "ModuleHost skeleton verifies, loads and health-checks a package in isolation");

            await using var client = new ModuleHostProcessClient(root, "fixture-host", Path.Combine(root, "process-data"));
            var ready = await client.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(ready.Url) };
            using var unauthenticated = new HttpClient { BaseAddress = new Uri(ready.Url) };
            check((await unauthenticated.GetAsync("/hello")).StatusCode == System.Net.HttpStatusCode.Unauthorized,
                "ModuleHost rejects a request without its service token");
            http.DefaultRequestHeaders.TryAddWithoutValidation(ModuleHostProtocol.ServiceTokenHeader, client.ServiceToken);
            var body = await http.GetStringAsync("/hello");
            var health = await client.HealthAsync();
            await client.ShutdownAsync();
            check(ready.ModuleId == "fixture-host" && health.Healthy && body == "hello from module host",
                "Server can launch, route to and control an isolated ModuleHost over the line protocol");

            var adminId = Guid.NewGuid();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModuleHosts:Enabled"] = "true",
                ["ModuleHosts:Entries:0:ModuleId"] = "fixture-host",
                ["ModuleHosts:Entries:0:PackageRoot"] = root,
                ["ModuleHosts:Entries:0:DataPath"] = Path.Combine(root, "supervisor-data"),
                ["ModuleManagement:Administrators:0"] = adminId.ToString(),
            }).Build();
            var environment = new TestEnvironment(root);
            using var supervisor = new ModuleHostSupervisor(environment, configuration,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModuleHostSupervisor>.Instance);
            await supervisor.StartAsync(CancellationToken.None);
            for (var attempt = 0; attempt < 100 && !supervisor.TryGetEndpoint("fixture-host", out _); attempt++)
                await Task.Delay(50);
            check(supervisor.TryGetConnection("fixture-host", out var endpoint, out var serviceToken)
                && supervisor.Snapshot().Single().Status == "running",
                "ModuleHost supervisor starts a configured isolated process: "
                + string.Join(",", supervisor.Snapshot().Select(host => host.Status + ":" + host.ProblemCode)));
            using var supervisedHttp = new HttpClient { BaseAddress = endpoint };
            supervisedHttp.DefaultRequestHeaders.TryAddWithoutValidation(ModuleHostProtocol.ServiceTokenHeader, serviceToken);
            check(await supervisedHttp.GetStringAsync("/hello") == "hello from module host",
                "ModuleHost supervisor exposes the isolated HTTP endpoint");
            var administrator = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", adminId.ToString())], "test"));
            var stranger = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "test"));
            check(supervisor.CanAccess("fixture-host", administrator, configuration)
                && !supervisor.CanAccess("fixture-host", stranger, configuration),
                "ModuleHost access defaults to configured server administrators");
            await supervisor.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ModuleHostChecks";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
