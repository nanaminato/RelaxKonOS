using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Protocol.Files;

/// <summary>
/// The provider vocabulary sits in the shared models, so it is stated here without loading the
/// view. The workspace's own layout is checked by the desktop layout suite, which references the
/// client assembly and can open the real view.
/// </summary>
internal static class CloudBrowserChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(CloudProtocolText.FormIndex("baidu-netdisk") == 2
              && CloudProtocolText.FormIndex("s3") == 1
              && CloudProtocolText.FormIndex("webdav") == 0
              && CloudProtocolText.FormIndex("minio") == 0,
            "Cloud providers map to their own form and an unknown one keeps the generic endpoint form");
        check(CloudProtocolText.UsesAuthorization("baidu-netdisk")
              && !CloudProtocolText.UsesAuthorization("s3")
              && !CloudProtocolText.UsesAuthorization("webdav"),
            "Only OAuth providers ask for authorization instead of stored credentials");
        check(CloudProtocolText.Name("baidu-netdisk").Length > 0
              && CloudProtocolText.Name("baidu-netdisk") != "baidu-netdisk"
              && CloudProtocolText.Name("minio") == "minio",
            "A known cloud provider is named in the current language and an unknown one keeps its identifier");

        var folder = new CloudEntryRow(new("ref-1", "Photos", true, 4096, null));
        var file = new CloudEntryRow(new("ref-2", "report.pdf", false, 3 * 1024 * 1024,
            new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero)));
        var unknownSize = new CloudEntryRow(new("ref-3", "blob", false, null, null));
        check(folder.SizeText.Length == 0 && folder.EntryType == FileSystemEntryType.Directory,
            "A cloud folder states no size");
        check(file.SizeText == "3 MB" && file.EntryType == FileSystemEntryType.File
              && file.ModifiedText.StartsWith("2026/11/12", StringComparison.Ordinal),
            "A cloud object formats its size and modification time");
        check(unknownSize.SizeText.Length == 0 && unknownSize.ModifiedText.Length == 0,
            "An object that reports neither size nor time prints neither");

        var account = new CloudAccountRow(new(Guid.NewGuid(), "Personal", "baidu-netdisk", true));
        var readonlyAccount = new CloudAccountRow(new(Guid.NewGuid(), "Archive", "s3", false));
        check(account.CanWrite && account.AccessText != readonlyAccount.AccessText,
            "An account row states the provider and the write access it was granted");
        check(account.KindText == CloudProtocolText.Name("baidu-netdisk"),
            "The account rail and the connection form name a provider the same way");

        var crumbs = new CloudBreadcrumb[]
        {
            new("Personal", 0), new("Photos", 1), new("2026", 2),
        };
        check(crumbs[0].Depth == 0 && crumbs[^1].Depth == 2,
            "A crumb counts the folders below the connection root so the first crumb returns to it");
    }
}
