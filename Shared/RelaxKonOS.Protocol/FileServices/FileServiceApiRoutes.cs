namespace RelaxKonOS.Protocol.FileServices;

public static class FileServiceApiRoutes
{
    public const string Smb = $"/{RelaxKonOS.Protocol.Common.RelaxKonOSEndpoints.ApiVersionPrefix}/file-services/smb";
    public const string Status = Smb + "/status";
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
}
