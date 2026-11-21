using System.Net;
using System.Text;
using RelaxKonOS.CloudStorage.Transport;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.ServerModule.CloudWebDav;

internal static class CloudWebDavConnectorAcceptance
{
    internal static async Task RunAsync()
    {
        if (!Uri.TryCreate(Required("ENDPOINT"), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Expected a dedicated WebDAV HTTPS endpoint.");
        var allowPrivate = Environment.GetEnvironmentVariable("RELAXKONOS_WEBDAV_ALLOW_PRIVATE") == "1";
        using var http = CloudTransport.Create(endpoint, allowPrivate);
        var settings = new CloudConnectionRequest("WebDAV acceptance", "webdav",
            endpoint.AbsoluteUri, null, null, null, Required("USER"), Required("SECRET"), AllowPrivateWebDav: allowPrivate,
            EnableWrites: true);
        var connector = new WebDavConnector(new(CloudWebDavAction.List, settings.Endpoint, settings.UserName, settings.Secret,
            settings.AllowPrivateWebDav, ""), http);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        var parent = $"relaxkonos-dav-{Guid.NewGuid():N}/";
        await connector.CreateFolderAsync(parent, ct);
        var stage = "create source directories";
        try
        {
            var source = parent + "source/";
            await connector.CreateFolderAsync(source, ct);
            await connector.CreateFolderAsync(source + "empty/", ct);
            stage = "upload original";
            using (var original = new MemoryStream(Encoding.UTF8.GetBytes("original")))
                await connector.UploadAsync(source + "file.txt", original, ct);
            stage = "replace file";
            using (var replacement = new MemoryStream(Encoding.UTF8.GetBytes("replacement")))
                await connector.UploadAsync(source + "file.txt", replacement, ct, replace: true);
            var destination = parent + "destination/";
            stage = "move directory";
            await connector.MoveAsync(source, destination, ct);
            stage = "verify moved directory";
            var items = await connector.ListAsync(destination, null, ct);
            if (!items.Entries.Any(entry => entry is { Name: "empty", IsDirectory: true }))
                throw new InvalidOperationException("WebDAV move did not preserve the empty directory.");
            using (var download = await connector.DownloadAsync(destination + "file.txt", ct))
            {
                download.EnsureSuccessStatusCode();
                if (await download.Content.ReadAsStringAsync(ct) != "replacement")
                    throw new InvalidOperationException("WebDAV move did not preserve the replacement bytes.");
            }
            stage = "delete moved directory";
            await connector.DeleteAsync(destination, ct);
            stage = "round trip";
            var testRoot = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/" + parent);
            var temporary = Directory.CreateTempSubdirectory("relaxkonos-dav-");
            try
            {
                await CloudStorageChecks.RunRealTreeRoundTripAsync(settings with { Endpoint = testRoot.AbsoluteUri }, temporary.FullName, ct);
                await connector.DeleteAsync(parent + "roundtrip/", ct);
            }
            finally { temporary.Delete(recursive: true); }
            var remaining = await connector.ListAsync(parent, null, ct);
            if (remaining.NextCursor is not null || remaining.Entries.Count != 0)
                throw new InvalidOperationException("WebDAV acceptance directory still contains entries.");
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"WebDAV acceptance: {stage} failed with HTTP {(int?)error.StatusCode}.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await connector.DeleteAsync(parent, cleanup.Token);
                if (await connector.InfoAsync(parent, cleanup.Token) is not null)
                    throw new InvalidOperationException("WebDAV acceptance directory remained after cleanup.");
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
            catch (HttpRequestException error)
            {
                throw new InvalidOperationException($"WebDAV acceptance cleanup failed with HTTP {(int?)error.StatusCode}.");
            }
        }
        Console.WriteLine("PASS: WebDAV replaced, moved, deleted and round-tripped directories with file bytes and empty children.");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable("RELAXKONOS_WEBDAV_" + name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing RELAXKONOS_WEBDAV_{name}.");
}
