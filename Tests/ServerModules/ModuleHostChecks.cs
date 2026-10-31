using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
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
            var health = await client.HealthAsync();
            await client.ShutdownAsync();
            check(ready.ModuleId == "fixture-host" && health.Healthy,
                "Server can launch and control an isolated ModuleHost over the line protocol");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
