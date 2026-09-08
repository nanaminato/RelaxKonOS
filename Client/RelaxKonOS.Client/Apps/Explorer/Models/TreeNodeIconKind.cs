namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>导航树节点图标种类。驱动 <c>TreeNodeIconKindToGlyphConverter</c> 选择对应 emoji。
/// 与 <c>SpecialFolderKind</c> 一一对应（除 Home 组节点本身用 <see cref="Home"/>，网络占位用 <see cref="Network"/>，
/// Computer/Drive/Folder 用于此电脑节点与盘符懒加载子目录）。</summary>
public enum TreeNodeIconKind
{
    /// <summary>未加载占位（dummy child，不渲染）。</summary>
    Placeholder,
    /// <summary>"此电脑" 根节点。</summary>
    Computer,
    /// <summary>盘符节点（Windows C:/D: 等 / Linux "/" 根）。</summary>
    Drive,
    /// <summary>普通目录（懒加载子节点）。</summary>
    Folder,
    /// <summary>"主目录" 组节点（家目录入口）。</summary>
    Home,
    /// <summary>桌面快捷入口。</summary>
    Desktop,
    /// <summary>文档快捷入口。</summary>
    Documents,
    /// <summary>下载快捷入口。</summary>
    Downloads,
    /// <summary>图片快捷入口。</summary>
    Pictures,
    /// <summary>音乐快捷入口。</summary>
    Music,
    /// <summary>视频快捷入口。</summary>
    Videos,
    /// <summary>"网络" 占位节点（当前不实现浏览）。</summary>
    Network
}
