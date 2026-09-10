namespace RelaxKonOS.Protocol.FileServices;

public static class FileServiceApiRoutes
{
    public const string Smb = $"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/file-services/smb";
    public const string Status = Smb + "/status";
    public const string Capabilities = Smb + "/capabilities";
    public const string Install = Smb + "/install";
    public const string Start = Smb + "/start";
    public const string Stop = Smb + "/stop";
    public const string Restart = Smb + "/restart";
    public const string Shares = Smb + "/shares";
    public const string ShareById = Shares + "/{shareId}";
    public const string Users = Smb + "/users";
    public const string EnableUser = Users + "/{username}/enable";
    public const string DisableUser = Users + "/{username}/disable";
    public const string UserPassword = Users + "/{username}/password";
    public const string Connection = Smb + "/connection";

    /// <summary>Returns a route relative to the SMB endpoint group without duplicating literals in callers.</summary>
    public static string RelativeToSmb(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        if (!route.StartsWith(Smb, StringComparison.Ordinal))
            throw new ArgumentException("The route does not belong to the SMB API.", nameof(route));
        return route[Smb.Length..].TrimStart('/');
    }
}
