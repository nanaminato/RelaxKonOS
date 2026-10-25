namespace RelaxKonOS.Server.WebServer;

/// <summary>
/// The one place that decides where the RelaxKonOS-owned Nginx marker lives. Both the Web Server
/// manager and the component adapter must agree, so the layout is defined once instead of being
/// re-derived next to each caller.
/// </summary>
public static class NginxManagedPaths
{
    public const string MarkerName = ".relaxkonos-managed";
    public const string MarkerContent = "RelaxKonOS owns this Nginx installation. Do not move this marker.\n";

    /// <summary>Absolute marker root; empty options select the platform default.</summary>
    public static string Root(NginxManagedOptions options)
    {
        var configured = options.InstallationRoot.Trim();
        var root = string.IsNullOrWhiteSpace(configured)
            ? OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "webserver", "nginx")
                : "/var/lib/relaxkonos/webserver/nginx"
            : Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(root);
    }

    public static string MarkerPath(NginxManagedOptions options) => Path.Combine(Root(options), MarkerName);

    /// <summary>
    /// True only when the marker file exists, is not a link, and carries the exact owned content.
    /// A marker that cannot be read is not ownership evidence.
    /// </summary>
    public static bool IsManaged(NginxManagedOptions options)
    {
        try
        {
            var marker = MarkerPath(options);
            return File.Exists(marker) && !IsSymbolicLink(marker) && File.ReadAllText(marker) == MarkerContent;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsSymbolicLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
