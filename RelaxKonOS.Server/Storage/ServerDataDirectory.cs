namespace RelaxKonOS.Server.Storage;

/// <summary>Resolve the installer's persistent data junction before crossing a no-reparse-point Helper boundary.</summary>
internal static class ServerDataDirectory
{
    public static string Resolve(IHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "data");
        var directory = new DirectoryInfo(path);
        return directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)
            ? directory.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Server data link cannot be resolved.")
            : path;
    }
}
