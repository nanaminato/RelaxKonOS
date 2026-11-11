using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ImageMirrors;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.ImageMirrors;

internal static class DockerMirrorChecks
{
    internal static async Task RunAsync()
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        var mirrors = new InMemoryImageMirrorRepository();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IImageMirrorRepository>(mirrors);
        builder.Services.AddAuthentication(); builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(options => {
            foreach (var converter in RelaxKonOSJsonOptions.Default.Converters) options.SerializerOptions.Converters.Add(converter);
        });
        await using var app = builder.Build();
        app.UseRouting();
        // Test identities are local fixtures, never host accounts or production authentication.
        app.Use((context, next) => {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,
                context.Request.Headers["Fixture-Owner"].ToString())], "Fixture"));
            return next(context);
        });
        app.UseAuthorization(); app.MapImageMirrorEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("Fixture-Owner", owner.ToString());
            const string root = "/api/v1.0/image-mirrors/docker";
            var initial = await ListAsync(client, root);
            TestAssert.Assert(initial is [{ Id: var defaultId, IsSelected: true, Target: ImageMirrorTarget.Docker }] && defaultId == Guid.Empty,
                "Mirror list must start with the current selected default resolver.");
            foreach (var endpoint in new[] { "http://host", "https://user:secret@host", "https://host/path", "https://host?secret=x", "https://host#x" })
            {
                using var invalid = await client.PostAsJsonAsync(root, new CreateImageMirrorRequest("Mirror", endpoint));
                TestAssert.Assert(invalid.StatusCode == HttpStatusCode.BadRequest && mirrors.List(owner, ImageMirrorTarget.Docker).Count == 0,
                    "Invalid mirror endpoint was persisted.");
            }
            using var created = await client.PostAsJsonAsync(root, new CreateImageMirrorRequest("Mirror", "https://MIRROR.example:8443/"));
            TestAssert.Assert(created.StatusCode == HttpStatusCode.Created, "Mirror creation failed.");
            var value = JsonSerializer.Deserialize<ImageMirrorDto>(await created.Content.ReadAsStringAsync(), RelaxKonOSJsonOptions.Default)!;
            TestAssert.Assert(value.Endpoint == "mirror.example:8443" && !value.IsSelected && value.Target == ImageMirrorTarget.Docker,
                "Mirror endpoint normalization or current enum serialization changed.");
            client.DefaultRequestHeaders.Remove("Fixture-Owner"); client.DefaultRequestHeaders.Add("Fixture-Owner", other.ToString());
            TestAssert.Assert((await ListAsync(client, root)).Count == 1, "A mirror leaked to another account.");
            using var wrongSelect = await client.PutAsJsonAsync(root + "/selection", new SelectImageMirrorRequest(value.Id));
            using var wrongUpdate = await client.PutAsJsonAsync(root + "/" + value.Id, new UpdateImageMirrorRequest("Other", "other.example"));
            using var wrongDelete = await client.DeleteAsync(root + "/" + value.Id);
            TestAssert.Assert(wrongSelect.StatusCode == HttpStatusCode.NotFound && wrongUpdate.StatusCode == HttpStatusCode.NotFound && wrongDelete.StatusCode == HttpStatusCode.NotFound,
                "Another account was allowed to change the original mirror.");
            client.DefaultRequestHeaders.Remove("Fixture-Owner"); client.DefaultRequestHeaders.Add("Fixture-Owner", owner.ToString());
            using var selected = await client.PutAsJsonAsync(root + "/selection", new SelectImageMirrorRequest(value.Id));
            TestAssert.Assert(selected.StatusCode == HttpStatusCode.NoContent, "Selecting the original mirror failed.");
            var resolver = new DockerImageMirrorResolver(mirrors);
            TestAssert.Assert(resolver.Resolve(owner, "nginx:alpine") == "mirror.example:8443/library/nginx:alpine"
                && resolver.Resolve(owner, "ghcr.io/example/app:tag") == "ghcr.io/example/app:tag",
                "Mirror selection must resolve eligible Docker Hub references without rewriting explicit registries.");
            using var updated = await client.PutAsJsonAsync(root + "/" + value.Id, new UpdateImageMirrorRequest("Changed", "changed.example"));
            TestAssert.Assert(updated.StatusCode == HttpStatusCode.OK && resolver.Resolve(owner, "nginx:alpine") == "changed.example/library/nginx:alpine",
                "Updating a selected mirror did not affect the current resolver.");
            using var reset = await client.PutAsJsonAsync(root + "/selection", new SelectImageMirrorRequest(null));
            TestAssert.Assert(reset.StatusCode == HttpStatusCode.NoContent && resolver.Resolve(owner, "nginx:alpine") == "nginx:alpine", "Default selection did not reset resolution.");
            using var reselection = await client.PutAsJsonAsync(root + "/selection", new SelectImageMirrorRequest(value.Id));
            using var removed = await client.DeleteAsync(root + "/" + value.Id);
            TestAssert.Assert(reselection.StatusCode == HttpStatusCode.NoContent && removed.StatusCode == HttpStatusCode.NoContent
                && (await ListAsync(client, root)) is [{ Id: var finalId, IsSelected: true }] && finalId == Guid.Empty,
                "Deleting a selected mirror must return the account to the default resolver.");
            Console.WriteLine("Docker mirror HTTP, owner isolation, validation, selection and resolver checks passed.");
        }
        finally { await app.StopAsync(); }
    }
    private static async Task<IReadOnlyList<ImageMirrorDto>> ListAsync(HttpClient client, string root) =>
        JsonSerializer.Deserialize<List<ImageMirrorDto>>(await client.GetStringAsync(root), RelaxKonOSJsonOptions.Default)!;
}
