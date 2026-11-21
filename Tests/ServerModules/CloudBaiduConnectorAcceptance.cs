using RelaxKonOS.CloudStorage.Transport;
using System.Text;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.ServerModule.CloudStorage;

internal static class CloudBaiduConnectorAcceptance
{
    internal static async Task RunAsync()
    {
        var parent = Required("TEST_PARENT").Trim('/');
        if (parent.Length == 0 || !CloudTransport.ValidPath(parent))
            throw new InvalidOperationException("Expected a dedicated Baidu test parent directory.");
        var settings = new CloudConnectionRequest("Baidu acceptance", "baidu-netdisk", "https://pan.baidu.com", null,
            null, null, Required("CLIENT_ID"), Required("CLIENT_SECRET"), Required("REFRESH_TOKEN"), EnableWrites: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        var grant = await OAuthDriveConnector.RefreshAsync(settings, CloudTransport.Create, ct);
        var connector = new OAuthDriveConnector(grant.Access, CloudTransport.Create);
        var root = $"{parent}/relaxkonos-baidu-{Guid.NewGuid():N}/";
        await connector.CreateFolderAsync(root, ct);
        try
        {
            var source = root + "source/";
            await connector.CreateFolderAsync(source, ct);
            await connector.CreateFolderAsync(source + "empty/", ct);
            using (var original = new MemoryStream(Encoding.UTF8.GetBytes("original")))
                await connector.UploadAsync(source + "file.txt", original, ct);
            using (var replacement = new MemoryStream(Encoding.UTF8.GetBytes("replacement")))
                await connector.UploadAsync(source + "file.txt", replacement, ct, replace: true);
            var moved = root + "moved/";
            await connector.MoveAsync(source, moved, ct);
            var page = await connector.ListAsync(moved, null, ct);
            if (!page.Entries.Any(entry => entry is { Name: "empty", IsDirectory: true }))
                throw new InvalidOperationException("Baidu did not preserve the empty child directory.");
            var file = page.Entries.Single(entry => entry is { Name: "file.txt", IsDirectory: false });
            await connector.DownloadAsync(CloudTransport.PathFrom(file.Reference), async (_, stream, _) =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                if (await reader.ReadToEndAsync(ct) != "replacement")
                    throw new InvalidOperationException("Baidu did not preserve the replacement bytes.");
            }, ct);
            await connector.DeleteAsync(moved, ct);

            var temporary = Directory.CreateTempSubdirectory("relaxkonos-baidu-");
            try
            {
                await CloudStorageChecks.RunRealTreeRoundTripAsync(settings with { SessionToken = grant.Refresh },
                    temporary.FullName, ct, CloudTransport.Reference(root));
                await connector.DeleteAsync(root + "roundtrip/", ct);
            }
            finally { temporary.Delete(recursive: true); }
            var remaining = await connector.ListAsync(root, null, ct);
            if (remaining is not { Entries.Count: 0, NextCursor: null })
                throw new InvalidOperationException("Baidu acceptance directory still contains entries.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await connector.DeleteAsync(root, cleanup.Token);
        }
        Console.WriteLine("PASS: Baidu replaced, moved, deleted and round-tripped directories with file bytes and empty children.");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable("RELAXKONOS_BAIDU_" + name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing RELAXKONOS_BAIDU_{name}.");
}
