namespace RelaxKonOS.Server.Storage;

/// <summary>服务端持久化配置。对应 appsettings.json 的 "Storage" 节。</summary>
public sealed class StorageOptions
{
    /// <summary>持久化提供程序：sqlite（默认，EF Core + SQLite）或 memory（内存仓储，开发回退）。</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>SQLite 数据库文件相对路径（相对 ContentRoot）。默认 data/relaxkonos.db。</summary>
    public string DatabasePath { get; set; } = "data/relaxkonos.db";

    /// <summary>Workspace 托管壁纸文件的相对目录（相对 ContentRoot）。</summary>
    public string WallpaperPath { get; set; } = "data/wallpapers";
}
