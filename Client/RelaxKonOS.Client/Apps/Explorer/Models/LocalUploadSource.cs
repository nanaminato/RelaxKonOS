namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>
/// A file or folder selected from the client host. The source is only held for the duration
/// of an upload and is never sent to the RelaxKonOS Server as a client-host path.
/// </summary>
public sealed record LocalUploadSource(string Path);
