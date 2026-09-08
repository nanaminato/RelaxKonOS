namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Shared, user-facing limits for workspace wallpaper uploads.</summary>
public static class WorkspaceWallpaperUploadLimits
{
    public const long MinFileBytes = 1;
    public const int MaxFileMegabytes = 32;
    public const long MaxFileBytes = MaxFileMegabytes * 1024L * 1024L;
}
