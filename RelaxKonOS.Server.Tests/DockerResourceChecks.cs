using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ImageMirrors;

internal static class DockerResourceChecks
{
    internal static async Task RunAsync()
    {
        var imageId = "sha256:" + new string('a', 64);
        TestAssert.Assert(DockerCliEngineService.IsImageId(imageId) && !DockerCliEngineService.IsImageId("sha256:" + new string('a', 63))
            && !DockerCliEngineService.IsImageId("sha256:" + new string('z', 64)) && !DockerCliEngineService.IsImageId("--force"), "Full image IDs must validate as current Engine identities.");
        TestAssert.Assert(DockerCliEngineService.ParseTable("", 3).Count == 0 && DockerCliEngineService.ParseTable("data\tlocal\t\n", 3)[0].Length == 3,
            "Only a successful explicit empty table or complete rows may be read as facts.");
        foreach (var row in new[] { "data", "\tlocal\t/path", "data\tlocal\t/path\textra" })
        {
            var refused = false;
            try { DockerCliEngineService.ParseTable(row, 3); } catch (DockerReadException) { refused = true; }
            TestAssert.Assert(refused, "Malformed resource table became partial facts.");
        }
        foreach (var payload in new[] { "{}", "{\"Labels\":[]}", "{\"Labels\":{\"owner\":42}}" })
        {
            using var invalid = JsonDocument.Parse(payload); var refused = false;
            try { DockerCliEngineService.ReadLabels(invalid.RootElement); } catch (DockerReadException) { refused = true; }
            TestAssert.Assert(refused, "Missing or malformed ownership labels became an unowned resource.");
        }
        using (var empty = JsonDocument.Parse("{\"Labels\":null}")) TestAssert.Assert(DockerCliEngineService.ReadLabels(empty.RootElement).Count == 0, "Actual null Docker labels represent an empty map.");
        var logs = DockerCliEngineService.ContainerLogs("2026-09-30T00:00:01.000000000Z stdout\n", "2026-09-30T00:00:02.000000000Z stderr\n", 200);
        TestAssert.Assert(logs.Lines.Count == 2 && logs.Lines[1].EndsWith("stderr") && !logs.Truncated, "Container stderr logs must not disappear from a successful read.");
        var bounded = DockerCliEngineService.ContainerLogs("2026-09-30T00:00:01.000000000Z old\n", "2026-09-30T00:00:02.000000000Z " + new string('x', 600), 1);
        TestAssert.Assert(bounded.Lines.Count == 1 && bounded.Lines[0].Length == 512 && bounded.Truncated, "Container log tails and line lengths must be bounded with an explicit truncation flag.");
        var labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "stack" };
        var networkJson = JsonSerializer.Serialize(new DockerNetworkDetailsDto("abc123", "custom", "bridge", "local", [], labels), RelaxKonOSJsonOptions.Default);
        using (var json = JsonDocument.Parse(networkJson))
            TestAssert.Assert(json.RootElement.GetProperty("labels").GetProperty("com.docker.compose.project").GetString() == "stack", "Network details lost ownership labels.");
        // This guaranteed-missing executable makes every CLI operation safe on a developer host.
        var missing = Path.Combine(Path.GetTempPath(), $"missing-docker-{Guid.NewGuid():N}.exe");
        var engine = new DockerCliEngineService(new DockerCliEngineOptions { ExecutablePath = missing }, new DisabledDockerProxyResolver(), NullLogger<DockerCliEngineService>.Instance);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IDockerEngineService>(engine);
        builder.Services.AddSingleton<IServerModeResolver, ProgressTestMode>();
        builder.Services.AddSingleton<IDockerImageMirrorResolver>(new DockerImageMirrorResolver(new InMemoryImageMirrorRepository()));
        builder.Services.AddSingleton<IDockerEngineControlService>(_ => throw new NotSupportedException("Resource-only fixture"));
        builder.Services.AddSingleton<IDockerComposeService>(_ => throw new NotSupportedException("Resource-only fixture"));
        builder.Services.AddSingleton<DockerStackOperationCoordinator>(_ => throw new NotSupportedException("Resource-only fixture"));
        builder.Services.AddAuthentication(); builder.Services.AddAuthorization();
        await using var app = builder.Build(); app.UseRouting();
        app.Use((context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.Empty.ToString())], "Fixture")); return next(context); });
        app.UseAuthorization(); app.MapDockerEndpoints(); await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            foreach (var path in new[] { "containers", "images", "networks", "volumes", "containers/abc123/stats" })
            {
                using var response = await client.GetAsync("/api/v1.0/docker/" + path);
                TestAssert.Assert(response.StatusCode == HttpStatusCode.ServiceUnavailable && (await response.Content.ReadAsStringAsync()).Contains("docker.not_installed"),
                    "Failed resource reads must return 503, never a successful empty collection.");
            }
            using var deletedVolume = await client.DeleteAsync("/api/v1.0/docker/volumes/data?confirmed=true");
            TestAssert.Assert(deletedVolume.StatusCode == HttpStatusCode.ServiceUnavailable, "Failed volume reference read must block removal.");
            using var imageRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/v1.0/docker/images/" + Uri.EscapeDataString(imageId)) {
                Content = JsonContent.Create(new DockerImageOperationRequest(imageId, true)) };
            using var deletedImage = await client.SendAsync(imageRequest);
            var result = await deletedImage.Content.ReadFromJsonAsync<DockerOperationResult>();
            TestAssert.Assert(deletedImage.StatusCode == HttpStatusCode.OK && result is { Success: false, ProblemCode: "docker.not_installed" },
                "A valid full image ID must reach the configured CLI, rather than fail ID validation.");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("Docker resource HTTP, failed-read protection, full image identity and network ownership checks passed.");
    }
}
