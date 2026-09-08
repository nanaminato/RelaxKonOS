namespace RelaxKonOS.Protocol.Files;

/// <summary>特殊文件夹种类。用于 Explorer 导航窗格的快捷入口（参考 Windows File Explorer Navigation Pane）。
/// 序列化为 camelCase 字符串（由 <c>RemoteOsJsonOptions.Default</c> 的 <c>JsonStringEnumConverter</c> 统一生效）。</summary>
public enum SpecialFolderKind
{
    /// <summary>用户家目录（Home）。Windows 为 <c>%USERPROFILE%</c>，Linux 为 <c>$HOME</c>。</summary>
    Home,
    /// <summary>桌面（Desktop）。</summary>
    Desktop,
    /// <summary>文档（Documents / MyDocuments）。</summary>
    Documents,
    /// <summary>下载（Downloads，<see cref="System.Environment.SpecialFolder"/> 无此项，服务端手动拼接 <c>$HOME/Downloads</c>）。</summary>
    Downloads,
    /// <summary>图片（Pictures / MyPictures）。</summary>
    Pictures,
    /// <summary>音乐（Music / MyMusic）。</summary>
    Music,
    /// <summary>视频（Videos / MyVideos）。</summary>
    Videos
}
